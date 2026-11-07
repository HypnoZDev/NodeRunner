using System.Numerics;
using ImGuiNET;
using NodeRunner.App.Online;
using NodeRunner.Editor.Online;

namespace NodeRunner.App.Ui;

// "Online" window (toolbar button): create a session on the open project, or join one by address; participants, join
// requests (accept / refuse), leave.
internal sealed class OnlineWindow(AppState state)
{
    public const string Title = $"{Icons.Users}  Online###Online";

    private bool _open;
    private string _name = "";
    private string _address = "";
    private int _shownRequests;

    public bool IsOpen => _open;

    public void Toggle()
    {
        _open = !_open;
        _name = state.Online.Name;
        _address = state.Settings.OnlineAddress ?? "";
    }

    public void Draw()
    {
        var online = state.Online;
        // A new join request opens the window.
        int requests = online.Session?.JoinRequests.Count ?? 0;
        if (requests > _shownRequests)
            _open = true;
        _shownRequests = requests;
        if (!_open)
            return;

        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSizeConstraints(new Vector2(440f, 0f), new Vector2(440f, float.MaxValue));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 14));
        bool visible = ImGui.Begin(L(Title), ref _open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking);
        ImGui.PopStyleVar();
        if (visible)
        {
            if (online.IsPreparing)
                Widgets.Hint(L("Préparation de la session : enregistrement du projet et chargement de sa carte..."), wrap: true);
            else if (online.Session is { IsOver: false } session)
                DrawSession(online, session);
            else
                DrawStart(online);
        }
        ImGui.End();
    }

    private void DrawStart(OnlineController online)
    {
        if (online.Session is { EndReason: { } ended })
            Message(ended, warning: true);
        if (online.LastError is { } error)
            Message(error, warning: true);

        if (Widgets.BeginProperties("##online_name", 0.3f))
        {
            Widgets.Property(L("Votre nom"), L("Le nom que voient les autres participants."));
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##online_name_field", ref _name, 24) || ImGui.IsItemDeactivatedAfterEdit())
                online.Name = _name.Trim();
            Widgets.EndProperties();
        }

        if (Widgets.Section(L("Créer une session"), "online_host"))
        {
            Widgets.Hint(state.Project is { } project
                ? L($"Partage le projet « {project.Name} » : il est enregistré, sa carte rechargée, puis vos collègues le reçoivent. Jusqu'à {OnlineSession.MaxParticipants} personnes ; seul l'hôte enregistre et exporte.")
                : L("Ouvrez ou créez d'abord un projet : la session partage le projet ouvert."), wrap: true);
            ImGui.BeginDisabled(state.Project is null);
            if (Widgets.PrimaryButton(L("Créer une session"), -1))
                online.Host();
            ImGui.EndDisabled();
        }

        if (Widgets.Section(L("Rejoindre une session"), "online_join"))
        {
            Widgets.Hint(L("L'adresse affichée chez l'hôte (même réseau, ou réseau virtuel type Tailscale / Radmin VPN). Vos modifications non enregistrées sont enregistrées dans votre projet d'abord."), wrap: true);
            ImGui.SetNextItemWidth(-1);
            bool enter = ImGui.InputTextWithHint("##online_address", "192.168.1.20", ref _address, 64, ImGuiInputTextFlags.EnterReturnsTrue);
            if (Widgets.Button(L("Rejoindre"), width: -1) || enter)
                online.Join(_address);
        }

        if (online.PreviousProject is not null || online.Session is { Role: SessionRole.Guest })
        {
            ImGui.Spacing();
            string back = online.PreviousProject is { } previous ? L($"Revenir à mon projet ({Path.GetFileName(previous)})") : L("Fermer le projet de la session");
            if (Widgets.Button(back, L("Vous travaillez encore sur la copie du projet de l'hôte."), -1))
                online.BackToPreviousProject();
        }
    }

    private void DrawSession(OnlineController online, OnlineSession session)
    {
        bool host = session.Role == SessionRole.Host;
        if (Widgets.BeginProperties("##online_state", 0.3f))
        {
            Widgets.PropertyText(L("Rôle"), host ? L("Hôte (le projet est sur ce PC)") : L("Invité"));
            Widgets.PropertyText(L("État"), session.IsLoading && !host ? session.Status : host ? L("Session ouverte") : L("Connecté"));
            if (host)
            {
                Widgets.Property(L("Adresse"), L("À donner à vos collègues. Sur un autre réseau : réseau virtuel (Tailscale, Radmin VPN...) en attendant les codes NRS."));
                var addresses = OnlineSession.LocalAddresses();
                if (addresses.Count == 0)
                    ImGui.TextDisabled(L("aucun réseau"));
                foreach (var address in addresses)
                {
                    string text = session.Port == OnlineSession.DefaultPort ? address : $"{address}:{session.Port}";
                    ImGui.TextUnformatted(text);
                    ImGui.SameLine();
                    if (Widgets.IconButton(Icons.Copy, $"copy_{address}", L("Copier")))
                        ImGui.SetClipboardText(text);
                }
            }
            Widgets.PropertyText(L("Modifications"), session.ChangeCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Widgets.EndProperties();
        }

        if (session.JoinRequests.Count > 0 && Widgets.Section(L("Demandes"), "online_requests"))
        {
            foreach (var request in session.JoinRequests.ToList())
            {
                ImGui.TextUnformatted(L($"{request.Name} veut rejoindre"));
                ImGui.SameLine();
                ImGui.TextDisabled(request.Address);
                if (Widgets.PrimaryButton(L($"Accepter##{request.Id}")))
                    session.Accept(request);
                ImGui.SameLine();
                if (Widgets.Button(L($"Refuser##{request.Id}")))
                    session.Refuse(request);
            }
        }

        if (Widgets.Section(L($"Participants ({session.Participants.Count}/{OnlineSession.MaxParticipants})"), "online_people"))
        {
            for (int i = 0; i < session.Participants.Count; i++)
            {
                string who = session.Participants[i];
                ImGui.TextUnformatted(who == session.Name ? $"{who} (vous)" : who);
                if (i == 0)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled(L("hôte"));
                }
            }
        }
        if (host)
            Widgets.Hint(L("Seul l'hôte enregistre (Ctrl+S) et exporte. Ctrl+Z annule la dernière modification de la session, quel qu'en soit l'auteur."), wrap: true);
        else
            Widgets.Hint(L("Vous travaillez sur une copie du projet de l'hôte ; c'est lui qui enregistre. Ctrl+Z annule la dernière modification de la session."), wrap: true);

        ImGui.Spacing();
        if (Widgets.Button(host ? L("Terminer la session") : L("Quitter la session"), width: -1))
            online.Leave();
    }

    private static void Message(string text, bool warning)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, warning ? UiTheme.Warning : UiTheme.Text);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 408f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
        ImGui.Spacing();
    }
}
