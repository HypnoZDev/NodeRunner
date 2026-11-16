using ImGuiNET;
using NodeRunner.Data.Hashing;
using NodeRunner.App.World;
using NodeRunner.Data.Audio;
using NodeRunner.Data.Maps;

namespace NodeRunner.App.Ui;

internal sealed partial class PropertiesPanel
{
    // What the audio occlusion section shows for a placed interior: built once, then kept until the interior, its
    // position, its rooms or the language change.
    private sealed record AudioOcclusionView(AudioOcclusionCatalog Catalog, MapEntity Interior, uint Hash, uint FileHash,
        int RoomVersion, Editor.Localization.Language Language, string? Path, string? Error, bool Found, bool OriginalLost, int Links, int Outside,
        int Doors, int Glass, int Paths, IReadOnlyList<(string Text, bool Ok)> Lines, bool Mismatch);

    private AudioOcclusionView? _audioOcclusion;

    private AudioOcclusionView AudioOcclusionOf(AudioOcclusionCatalog catalog, MapEntity interior, Archetype archetype,
        MapLibrary library, HashNames names)
    {
        uint hash = AudioOcclusionHash.Of(interior.ArchetypeHash, interior.Position);
        uint fileHash = AudioOcclusionHash.Of(interior.FileArchetypeHash, interior.FilePosition);
        if (_audioOcclusion is { } kept && kept.Catalog == catalog && kept.Interior == interior && kept.Hash == hash
            && kept.FileHash == fileHash && kept.RoomVersion == library.RoomVersion && kept.Language == Editor.Localization.Loc.Current)
            return kept;

        var (occlusion, error) = catalog.Get(hash);
        var rooms = archetype.InteriorRooms;
        string RoomName(int r) => r == 0 ? L("extérieur") : r < rooms.Count && rooms[r].Name.Length > 0 ? rooms[r].Name : $"{r}";
        var lines = new List<(string Text, bool Ok)>();
        int links = 0, outside = 0, doors = 0, glass = 0;
        foreach (var link in occlusion?.Links ?? [])
        {
            doors += link.Entities.Count(e => e.IsDoor);
            glass += link.Entities.Count(e => e.IsGlass);
            if (link.DestInteriorProxyHash != link.InteriorProxyHash)
                outside++;
            if (link.InteriorProxyHash != hash)
                continue;
            links++;

            // PortalIndex counts the portals touching the room, in the archetype's order.
            InteriorPortal? portal = null;
            if (link.RoomIndex < rooms.Count)
            {
                int rank = 0;
                foreach (var p in archetype.InteriorPortals)
                    if ((p.RoomFrom == link.RoomIndex || p.RoomTo == link.RoomIndex) && rank++ == link.PortalIndex)
                    {
                        portal = p;
                        break;
                    }
            }
            bool ok = portal is not null;
            string destination = L("un autre intérieur");
            if (link.DestInteriorProxyHash == hash)
            {
                destination = RoomName(link.DestRoomIndex);
                ok = portal is not null && (portal.RoomFrom == link.RoomIndex ? portal.RoomTo : portal.RoomFrom) == link.DestRoomIndex;
            }
            var text = new System.Text.StringBuilder($"{RoomName(link.RoomIndex)} → {destination}");
            foreach (var e in link.Entities)
                text.Append($" · {(e.IsDoor ? L("porte") : e.IsGlass ? L("vitre") : L("objet"))} {names.Describe(e.ModelHash)} ({e.MaxOcclusion * 100:0} %)");
            if (!ok)
                text.Append("  ?");
            lines.Add((text.ToString(), ok));
        }
        return _audioOcclusion = new AudioOcclusionView(catalog, interior, hash, fileHash, library.RoomVersion, Editor.Localization.Loc.Current,
            catalog.PathOf(hash), error, occlusion is not null, occlusion is null && fileHash != hash && catalog.PathOf(fileHash) is not null,
            links, outside, doors, glass, occlusion?.Paths.Count ?? 0, lines, lines.Any(l => !l.Ok));
    }

    private void DrawAudioOcclusion(MapEntity interior, Archetype archetype, MapLibrary library, HashNames names)
    {
        Widgets.Hint(L("Fichier qui dit au jeu comment le son passe d'une salle à l'autre (portails, portes, vitres). ")
            + L("Sans lui, les sons traversent les murs de l'intérieur."), wrap: true);
        if (state.World.AudioOcclusion is not { } catalog)
        {
            Widgets.Hint(state.World.AudioOcclusionError is { } error ? L($"Lecture impossible : {error}") : L("Lecture des fichiers d'occlusion audio..."));
            return;
        }
        var view = AudioOcclusionOf(catalog, interior, archetype, library, names);
        if (Widgets.BeginProperties("##audio_occlusion"))
        {
            Widgets.PropertyText(L("Fichier attendu"), $"{view.Hash}.ymt",
                L("Nom calculé par le jeu à partir du type de l'intérieur et de sa position : il change si l'intérieur est déplacé."));
            if (view.Path is not null)
                FileRow(L("Trouvé dans"), view.Path);
            else
                Widgets.PropertyText(L("Trouvé dans"), L("aucun fichier"), null, UiTheme.Warning);
            if (view.Error is not null)
                Widgets.PropertyText(L("Erreur"), view.Error, view.Error, UiTheme.Error);
            if (view.Found)
            {
                Widgets.PropertyText(L("Passages du son"), $"{view.Links}",
                    L("Un passage par portail et par sens : de quelle salle vers quelle salle le son peut aller."));
                if (view.Outside > 0)
                    Widgets.PropertyText(L("Vers d'autres intérieurs"), $"{view.Outside}");
                Widgets.PropertyText(L("Portes / vitres"), $"{view.Doors} / {view.Glass}",
                    L("Objets des portails qui étouffent le son quand ils sont fermés."));
                Widgets.PropertyText(L("Chemins précalculés"), $"{view.Paths}");
            }
            Widgets.EndProperties();
        }

        if (view.OriginalLost)
            WarningText(L($"Intérieur déplacé : son occlusion audio d'origine ({view.FileHash}.ymt) ne s'applique plus, les sons traversent ses murs. ")
                + L("L'export FiveM en crée une nouvelle à cette position."));
        else if (!view.Found && view.Error is null)
            Widgets.Hint(L("Cet intérieur n'a pas d'occlusion audio à cet endroit : les sons traversent ses murs. ")
                + L("L'export FiveM en crée une pour les intérieurs placés par le projet."), wrap: true);
        if (view.Mismatch)
            WarningText(L("Le fichier ne correspond plus aux salles et portails de l'intérieur (modifiés depuis ?) : marqués « ? » ci-dessous. ")
                + L("S'ils ont été modifiés dans le projet, l'export FiveM refait le fichier."));
        if (view.Lines.Count > 0 && ImGui.TreeNode(L($"Détail des passages ({view.Lines.Count})###audio_links")))
        {
            ImGui.PushTextWrapPos(0);
            foreach (var (text, ok) in view.Lines)
                ImGui.TextColored(ok ? UiTheme.TextMuted : UiTheme.Warning, text);
            ImGui.PopTextWrapPos();
            ImGui.TreePop();
        }
    }

    private string AudioName(uint hash, AudioGameData data, HashNames names) =>
        hash == 0 ? L("aucun") : data.Name(hash) ?? names.Describe(hash);

    private void DrawInteriorAudio(MapEntity interior, Archetype archetype, HashNames names)
    {
        Widgets.Hint(L("Réglages du jeu pour le son dans l'intérieur et dans chaque salle (écho, son de fond, pluie...), ")
            + L("lus dans les fichiers .dat151.rel. Cliquez une salle pour voir les siens."), wrap: true);
        if (state.World.AudioGameData is not { } data)
        {
            Widgets.Hint(state.World.AudioGameDataError is { } error ? L($"Lecture impossible : {error}") : L("Lecture des données audio..."));
            return;
        }
        if (data.Interior(interior.ArchetypeHash) is not { } settings)
        {
            WarningText(L("Cet intérieur n'a pas de réglages audio : le jeu le traite comme l'extérieur (pas d'écho de pièce, pluie audible). L'export FiveM lui en crée (ceux de l'intérieur copié s'il y en a un, sinon des réglages neutres)."));
            return;
        }
        if (Widgets.BeginProperties("##interior_audio"))
        {
            FileRow(L("Défini dans"), settings.File);
            Widgets.PropertyText(L("Bruit de foule"), AudioName(settings.WallaSoundSet, data, names), L("Sons de foule (walla) joués dans l'intérieur."));
            Widgets.PropertyText(L("Réflexions"), AudioName(settings.Reflections, data, names));
            Widgets.PropertyText(L("Drapeaux"), $"0x{settings.Flags:X8}");
            Widgets.PropertyText(L("Salles audio"), $"{settings.Rooms.Count}");
            Widgets.EndProperties();
        }
        for (int r = 1; r < archetype.InteriorRooms.Count; r++)
        {
            var room = archetype.InteriorRooms[r];
            int index = r;
            if (Widgets.Link($"{(room.Name.Length > 0 ? room.Name : $"{r}")}##audio_room{r}", L("Sélectionner cette salle")))
            {
                state.SelectRoom(new RoomSelection(interior, index));
                state.SetLayerVisible(RoomLayer.LayerName(RoomLayerPart.Rooms), true);
            }
            ImGui.SameLine();
            if (data.RoomOf(interior.ArchetypeHash, room.Name) is { } audio)
                ImGui.TextColored(UiTheme.TextMuted, L($"écho {audio.ReverbSmall:0.##} / {audio.ReverbMedium:0.##} / {audio.ReverbLarge:0.##}"));
            else
                ImGui.TextColored(UiTheme.Warning, L("sans réglage audio"));
        }
    }

    private void DrawRoomAudio(MapEntity interior, InteriorRoom room, HashNames names)
    {
        if (state.World.AudioGameData is not { } data)
        {
            Widgets.Hint(state.World.AudioGameDataError is { } error ? L($"Lecture impossible : {error}") : L("Lecture des données audio..."));
            return;
        }
        if (data.RoomOf(interior.ArchetypeHash, room.Name) is not { } audio)
        {
            WarningText(data.Interior(interior.ArchetypeHash) is null
                ? L("L'intérieur n'a pas de réglages audio : l'export FiveM lui en crée.")
                : L("Aucun réglage audio ne porte le nom de cette salle : le jeu lui donne des réglages par défaut. L'export FiveM lui en crée (ceux de la salle de même nom de l'intérieur copié, sinon des réglages neutres)."));
            return;
        }
        if (!Widgets.BeginProperties("##room_audio"))
            return;
        FileRow(L("Défini dans"), audio.File);
        Widgets.PropertyText(L("Nom audio"), AudioName(audio.NameHash, data, names));
        Widgets.PropertyText(L("Écho petit / moyen / grand"), $"{audio.ReverbSmall:0.##} / {audio.ReverbMedium:0.##} / {audio.ReverbLarge:0.##}",
            L("Réverbération de la pièce, de 0 à 1 : petite pièce, pièce moyenne, grand volume."));
        Widgets.PropertyText(L("Son de fond"), AudioName(audio.RoomTone, data, names), L("Bruit de fond de la salle (ventilation, bourdonnement...)."));
        Widgets.PropertyText(L("Zone d'ambiance"), AudioName(audio.AmbientZone, data, names));
        Widgets.PropertyText(L("Sons de l'extérieur"), $"{audio.ExteriorAudibility:0.##}", L("Part des sons de l'extérieur entendus dans la salle, de 0 à 1."));
        Widgets.PropertyText(L("Portails non marqués"), $"{audio.NonMarkedPortalOcclusion:0.##}",
            L("Étouffement du son par les portails sans réglage, de 0 (aucun) à 1 (bloqué)."));
        Widgets.PropertyText(L("Distance d'occlusion"), $"{audio.DistanceFromPortalForOcclusion:0.##} m / {audio.DistanceFromPortalFadeDistance:0.##} m",
            L("Distance au portail à partir de laquelle le son est étouffé, puis longueur du fondu."));
        Widgets.PropertyText(L("Amortissement"), $"{audio.RoomOcclusionDamping:0.##}");
        Widgets.PropertyText(L("Pluie"), audio.RainType switch { 0 => L("normale"), _ => $"{audio.RainType}" }, L("Type de bruit de pluie dans la salle."));
        Widgets.PropertyText(L("Type d'intérieur"), $"{audio.InteriorType}");
        Widgets.PropertyText(L("Acoustique des armes"), AudioName(audio.WeaponMetrics, data, names));
        Widgets.PropertyText(L("Bruit de foule"), AudioName(audio.WallaSoundSet, data, names));
        Widgets.PropertyText(L("Drapeaux"), $"0x{audio.Flags:X8}");
        Widgets.EndProperties();
    }
}
