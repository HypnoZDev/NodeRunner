using System.Diagnostics;
using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;
using NodeRunner.Editor.Online;

namespace NodeRunner.Tests;

// Sessions between "computers" in this process, over the loopback network.
public class OnlineSessionTests
{
    internal sealed class Doc : IOnlineDocument
    {
        private OnlineEditTests.World _world;

        public Doc() => _world = OnlineEditTests.Build(History);

        public OnlineEditTests.World World => _world;
        public EditCodecs Codecs { get; } = EditCodecs.ForEditor();
        public EditHistory History { get; } = new();
        public EditContext? Context => _world.Context;
        public bool IsReady => true;
        public string Fingerprint { get; set; } = "carte";
        public List<string> Notes { get; } = [];
        public int Opened { get; private set; }

        // The project's files are the starting map: a fresh one.
        public void OpenSnapshot(SessionSnapshot snapshot)
        {
            History.Clear();
            _world = OnlineEditTests.Build(History);
            Opened++;
        }

        public void Applied(IEdit edit, string author, SessionAction action) => Notes.Add($"{author}: {action} {edit.Description}");
        public void Notify(string text, bool warning = false) => Notes.Add(text);

        public void Move(int index, Vector3 position, string description)
        {
            var entity = _world.Map.Entities[index];
            var before = Placement.Of(entity);
            var after = before with { Position = position };
            after.ApplyTo(entity);
            History.Add(new PlacementEdit(entity, before, after, description));
        }
    }

    private static readonly SessionSnapshot Start = new("projet", [1, 2, 3], "carte");

    private static void Pump(Func<bool> until, params OnlineSession[] sessions)
    {
        var clock = Stopwatch.StartNew();
        while (!until())
        {
            if (clock.ElapsedMilliseconds > 10_000)
                throw new TimeoutException(string.Join(" | ", sessions.Select(s => $"{s.Role} {s.Status} {s.EndReason}")));
            foreach (var s in sessions)
                s.Update();
            Thread.Sleep(2);
        }
    }

    private static (OnlineSession Session, Doc Doc) Guest(OnlineSession host, Doc hostDoc, string name, string version = "1.0")
    {
        var doc = new Doc();
        var guest = OnlineSession.Join(doc, name, version, $"127.0.0.1:{host.Port}");
        Pump(() => host.JoinRequests.Any(j => j.Name.StartsWith(name, StringComparison.Ordinal)) || guest.IsOver, host, guest);
        if (!guest.IsOver)
        {
            host.Accept(host.JoinRequests.First(j => j.Name.StartsWith(name, StringComparison.Ordinal)));
            Pump(() => guest.Status == "Connecté", host, guest);
        }
        return (guest, doc);
    }

    [Fact]
    public void Host_and_guests_see_the_same_map_whoever_edits_or_undoes()
    {
        var hostDoc = new Doc();
        using var host = OnlineSession.Host(hostDoc, "Ben", "1.0", Start, port: 47900);
        hostDoc.Move(3, new Vector3(1, 2, 3), "Déplacer avant l'arrivée");

        var (marc, marcDoc) = Guest(host, hostDoc, "Marc");
        var (lea, leaDoc) = Guest(host, hostDoc, "Léa");
        var all = new[] { host, marc, lea };
        Assert.Equal(["Ben", "Marc", "Léa"], host.Participants);
        Pump(() => marc.Participants.Count == 3 && lea.Participants.Count == 3, all);
        bool Same() => OnlineEditTests.Snapshot(hostDoc.World) == OnlineEditTests.Snapshot(marcDoc.World)
            && OnlineEditTests.Snapshot(hostDoc.World) == OnlineEditTests.Snapshot(leaDoc.World);
        Assert.True(Same());

        hostDoc.Move(0, new Vector3(5, 5, 5), "Hôte");
        Pump(() => marc.ChangeCount == 2 && lea.ChangeCount == 2, all);
        Assert.True(Same());

        marcDoc.Move(1, new Vector3(7, 7, 7), "Marc");
        Pump(() => host.ChangeCount == 3 && lea.ChangeCount == 3 && marc.ChangeCount == 3, all);
        Assert.True(Same());
        Assert.Contains("Marc: Edit Marc", leaDoc.Notes);

        // Léa undoes the last change of the session (Marc's), then redoes it.
        Assert.True(lea.RequestUndo());
        Pump(() => marc.ChangeCount == 4 && lea.ChangeCount == 4, all);
        Assert.True(Same());
        Assert.Equal(new Vector3(10, 0, 0), hostDoc.World.Map.Entities[1].Position);
        Assert.True(lea.RequestUndo(undo: false));
        Pump(() => marc.ChangeCount == 5 && lea.ChangeCount == 5, all);
        Assert.True(Same());

        // The host undoes on its side: everyone follows.
        hostDoc.History.Undo();
        Pump(() => marc.ChangeCount == 6 && lea.ChangeCount == 6, all);
        Assert.True(Same());

        marc.End("Au revoir.");
        Pump(() => host.Participants.Count == 2, host, lea);
        host.End("fin");
        Pump(() => lea.IsOver, lea);
        Assert.Equal("L'hôte a terminé la session.", lea.EndReason);
    }

    [Fact]
    public void A_change_that_crosses_another_is_refused_and_the_guest_resynced()
    {
        var hostDoc = new Doc();
        using var host = OnlineSession.Host(hostDoc, "Ben", "1.0", Start, port: 47920);
        var (marc, marcDoc) = Guest(host, hostDoc, "Marc");
        Assert.Equal(1, marcDoc.Opened);

        // Both change the same object before hearing of the other's change: the host's goes first.
        hostDoc.Move(3, new Vector3(1, 1, 1), "Hôte");
        marcDoc.Move(3, new Vector3(9, 9, 9), "Marc");
        Pump(() => marcDoc.Opened == 2 && marc.Status == "Connecté", host, marc);
        Assert.Equal(OnlineEditTests.Snapshot(hostDoc.World), OnlineEditTests.Snapshot(marcDoc.World));
        Assert.Equal(new Vector3(1, 1, 1), marcDoc.World.Map.Entities[3].Position);

        // And it goes on normally.
        marcDoc.Move(2, new Vector3(4, 4, 4), "Marc encore");
        Pump(() => host.ChangeCount == 2 && marc.ChangeCount == 2, host, marc);
        Assert.Equal(OnlineEditTests.Snapshot(hostDoc.World), OnlineEditTests.Snapshot(marcDoc.World));
    }

    [Fact]
    public void Another_version_or_other_game_files_are_refused()
    {
        var hostDoc = new Doc();
        using var host = OnlineSession.Host(hostDoc, "Ben", "1.0", Start, port: 47940);

        var oldDoc = new Doc();
        using var old = OnlineSession.Join(oldDoc, "Marc", "0.9", $"localhost:{host.Port}");
        Pump(() => old.IsOver, host, old);
        Assert.Contains("Versions différentes", old.EndReason);
        Assert.Empty(host.JoinRequests);

        var otherDoc = new Doc { Fingerprint = "autre" };
        using var other = OnlineSession.Join(otherDoc, "Léa", "1.0", $"[::1]:{host.Port}");
        Pump(() => host.JoinRequests.Count == 1, host, other);
        host.Accept(host.JoinRequests[0]);
        Pump(() => other.IsOver, host, other);
        Assert.Contains("fichiers du jeu", other.EndReason);

        var refusedDoc = new Doc();
        using var refused = OnlineSession.Join(refusedDoc, "Zoé", "1.0", $"127.0.0.1:{host.Port}");
        Pump(() => host.JoinRequests.Count == 1, host, refused);
        host.Refuse(host.JoinRequests[0]);
        Pump(() => refused.IsOver, host, refused);
        Assert.Equal("L'hôte a refusé votre demande.", refused.EndReason);

        using var nobody = OnlineSession.Join(new Doc(), "Max", "1.0", "127.0.0.1:47999");
        Pump(() => nobody.IsOver, nobody);
        Assert.StartsWith("Impossible de rejoindre", nobody.EndReason);
    }

    [Fact]
    public void Addresses_are_read_with_or_without_port()
    {
        Assert.Equal(("192.168.1.20", OnlineSession.DefaultPort), OnlineSession.ParseAddress("192.168.1.20"));
        Assert.Equal(("192.168.1.20", 5000), OnlineSession.ParseAddress(" 192.168.1.20:5000 "));
        Assert.Equal(("pc-de-marc", 5000), OnlineSession.ParseAddress("pc-de-marc:5000"));
        Assert.Equal(("pc-de-marc", OnlineSession.DefaultPort), OnlineSession.ParseAddress("pc-de-marc"));
        Assert.Equal(("::1", 5000), OnlineSession.ParseAddress("[::1]:5000"));
    }
}
