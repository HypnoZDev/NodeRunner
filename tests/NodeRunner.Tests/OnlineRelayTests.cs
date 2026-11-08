using System.Diagnostics;
using System.Numerics;
using NodeRunner.Editor;
using NodeRunner.Editor.Online;

namespace NodeRunner.Tests;

public class OnlineRelayTests
{
    [Theory]
    [InlineData("NRS-AB2C", "NRS-AB2C")]
    [InlineData(" nrs ab2c ", "NRS-AB2C")]
    [InlineData("nrsab2c", "NRS-AB2C")]
    [InlineData("ab2c", "NRS-AB2C")]
    [InlineData("192.168.1.20", null)]
    [InlineData("localhost:47800", null)]
    [InlineData("NRS-AB1C", null)]
    [InlineData("NRS-ABCDE", null)]
    public void Codes_are_told_from_addresses(string text, string? code) => Assert.Equal(code, OnlineSession.ParseCode(text));

    // Against a running relay: NODERUNNER_RELAY=ws://127.0.0.1:8787 (relay folder: npx wrangler dev). Skipped without it.
    [Fact]
    public void A_session_through_the_relay_survives_drops_of_the_guest_and_of_the_host()
    {
        if (Environment.GetEnvironmentVariable("NODERUNNER_RELAY") is not { Length: > 0 } relay)
            return;

        // A project bigger than one relay message.
        var zip = new byte[700_000];
        new Random(1).NextBytes(zip);
        var start = new SessionSnapshot("projet", zip, "carte");

        var hostDoc = new OnlineSessionTests.Doc();
        using var host = OnlineSession.HostOnRelay(hostDoc, "Ben", "1.0", start, relay);
        Pump(() => host.Code is not null, host);
        Assert.Matches("^NRS-[A-Z2-9]{4}$", host.Code);

        var marcDoc = new OnlineSessionTests.Doc();
        using var marc = OnlineSession.Join(marcDoc, "Marc", "1.0", host.Code!.ToLowerInvariant(), relay);
        Pump(() => host.JoinRequests.Count == 1, host, marc);
        Assert.Equal("relais", host.JoinRequests[0].Address);
        host.Accept(host.JoinRequests[0]);
        Pump(() => marc.Status == "Connecté", host, marc);
        Assert.Equal(1, marcDoc.Opened);

        bool Same() => OnlineEditTests.Snapshot(hostDoc.World) == OnlineEditTests.Snapshot(marcDoc.World);
        hostDoc.Move(0, new Vector3(5, 5, 5), "Hôte");
        Pump(() => marc.ChangeCount == 1, host, marc);
        marcDoc.Move(1, new Vector3(7, 7, 7), "Marc");
        Pump(() => host.ChangeCount == 2 && marc.ChangeCount == 2, host, marc);
        Assert.True(Same());

        // The guest's link drops: it comes back on its own, without a new request.
        marc.CutLink();
        Pump(() => marc.IsReconnecting, host, marc);
        Pump(() => !marc.IsReconnecting && marc.Status == "Connecté", host, marc);
        Assert.Equal(2, marcDoc.Opened);
        Assert.Empty(host.JoinRequests);
        Assert.True(Same());
        Assert.Contains("Marc s'est reconnecté.", hostDoc.Notes);

        // The host's link drops: same code, the guest comes back too.
        string code = host.Code!;
        host.CutLink();
        Pump(() => marcDoc.Opened == 3 && marc.Status == "Connecté" && !host.IsReconnecting, host, marc);
        Assert.Equal(code, host.Code);
        marcDoc.Move(2, new Vector3(4, 4, 4), "Marc encore");
        Pump(() => host.ChangeCount == 3 && marc.ChangeCount == 3, host, marc);
        Assert.True(Same());

        // A wrong code.
        var nobody = OnlineSession.Join(new OnlineSessionTests.Doc(), "Max", "1.0", "NRS-ZZZZ", relay);
        Pump(() => nobody.IsOver, nobody);
        Assert.Contains("aucune session avec ce code", nobody.EndReason);

        host.End("fin");
        Pump(() => marc.IsOver, marc);
        Assert.Equal("L'hôte a terminé la session.", marc.EndReason);
    }

    private static void Pump(Func<bool> until, params OnlineSession[] sessions)
    {
        var clock = Stopwatch.StartNew();
        while (!until())
        {
            if (clock.ElapsedMilliseconds > 40_000)
                throw new TimeoutException(string.Join(" | ", sessions.Select(s => $"{s.Role} {s.Status} {s.EndReason}")));
            foreach (var s in sessions)
                s.Update();
            Thread.Sleep(5);
        }
    }
}
