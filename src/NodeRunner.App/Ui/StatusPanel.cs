using System.Numerics;
using ImGuiNET;
using NodeRunner.App.Input;
using NodeRunner.App.World;
using NodeRunner.Data.Installation;

namespace NodeRunner.App.Ui;

// "État" panel (docked at the bottom right): game installation, what is loaded, rendering figures, camera, files that
// could not be read and settings files, as label / value grids.
internal sealed class StatusPanel(AppState state, MainUi ui, CameraController cameraController, NavMeshLayer navMeshLayer,
    CollisionLayer collisionLayer, EntityBoxLayer entityBoxLayer, ModelLayer modelLayer)
{
    public const string Title = $"{Icons.Activity}  État###État";

    public void Draw(ref bool visible)
    {
        if (ImGui.Begin(L(Title), ref visible))
        {
            if (Widgets.Section(L("Projet"), "status_project"))
                DrawProject();
            if (Widgets.Section("GTA V", "status_game"))
                DrawGame();
            if (Widgets.Section(L("Carte chargée"), "status_world"))
                DrawWorld();
            if (Widgets.Section(L("Rendu"), "status_render"))
                DrawRender();
            if (Widgets.Section(L("Caméra"), "status_camera"))
                DrawCamera();
            DrawProblems();
            if (Widgets.Section(L("Commandes de la caméra"), "status_controls", defaultOpen: false))
                DrawControls();
            if (Widgets.Section(L("Fichiers de NodeRunner"), "status_files", defaultOpen: false))
                DrawFiles();
        }
        ImGui.End();
    }

    private void DrawProject()
    {
        if (state.Project is not { } project)
        {
            ImGui.TextColored(UiTheme.TextMuted, L("Aucun projet ouvert."));
            Widgets.Hint(L("Les modifications seront enregistrées dans un projet (menu Fichier > Nouveau projet)."), wrap: true);
            return;
        }
        if (Widgets.BeginProperties("##project"))
        {
            Widgets.PropertyText(L("Nom"), project.Name);
            Widgets.PropertyText(L("Dossier"), project.Folder, project.Folder);
            Widgets.PropertyText(L("Ressource FiveM"), project.ResourceName, L("Nom utilisé à l'export (ensure ...)"));
            Widgets.PropertyText(L("Export"), project.ExportFolder ?? L("pas encore exporté"), project.ExportFolder);
            Widgets.EndProperties();
        }
    }

    private void DrawGame()
    {
        var game = state.Game;
        if (game is null)
        {
            ImGui.TextColored(UiTheme.Warning, L("Aucune installation de GTA V trouvée."));
            Widgets.Hint(L("Indique le dossier du jeu manuellement (menu Fichier)."), wrap: true);
            if (Widgets.PrimaryButton(L($"{Icons.FolderOpen}  Choisir le dossier...")))
                ui.BrowseGameFolderRequested = true;
            return;
        }

        if (Widgets.BeginProperties("##game"))
        {
            Widgets.PropertyText(L("Dossier"), game.FolderPath);
            Widgets.PropertyText(L("Version"), game.Edition == GameEdition.Legacy ? "Legacy" : "Enhanced");
            Widgets.PropertyText(L("Trouvée via"), game.Source.ToString());
            foreach (var other in state.OtherInstallations)
            {
                Widgets.Property(L("Autre installation"));
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted($"{other.FolderPath} ({other.Edition})");
                ImGui.SameLine();
                if (Widgets.Button(L($"Utiliser##{other.FolderPath}")))
                    state.UseInstallation(other);
            }
            Widgets.EndProperties();
        }
    }

    private void DrawWorld()
    {
        var world = state.World;
        if (!Widgets.BeginProperties("##world"))
            return;

        var statusColor = world.Error is not null ? UiTheme.Error : world.IsBusy ? UiTheme.AccentText : UiTheme.Success;
        Widgets.PropertyText(L("État"), world.Status, world.Error, statusColor);
        if (world.Content is { } content)
        {
            Widgets.PropertyText(L("Version"), Toolbar.VersionName(content.Version));
            Widgets.PropertyText(L("Packs DLC"), $"{content.Packs.Count} / {content.AllPacks.Count}");
            Widgets.PropertyText(L("Archives actives"), Widgets.Count(content.ActiveArchiveCount));
        }
        if (world.Maps is { } maps)
        {
            Widgets.PropertyText("YMAP", Widgets.Count(maps.Maps.Count));
            int savedCount = maps.Maps.Count(m => m.IsSaved);
            if (savedCount > 0)
                Widgets.PropertyText(L("YMAP enregistrés"), L($"{savedCount} repris du projet"), state.SaveFolder);
            Widgets.PropertyText(L("Objets placés"), Widgets.Count(maps.EntityCount - maps.DeletedCount));
            if (maps.DeletedCount > 0)
                Widgets.PropertyText(L("Objets supprimés"), Widgets.Count(maps.DeletedCount), L("Supprimés dans cette session (Ctrl+Z pour les remettre)"), UiTheme.Warning);
            Widgets.PropertyText(L("Types (archétypes)"), Widgets.Count(maps.ArchetypeCount));
            if (world.Names is { } names)
            {
                var (userCount, userError) = world.UserNames;
                string user = userError is not null ? L(" · strings.txt illisible") : userCount > 0 ? L($" · dont {Widgets.Count(userCount)} de strings.txt") : "";
                Widgets.PropertyText(L("Noms connus"), Widgets.Count(names.Count) + user,
                    userError ?? L($"Retrouvés dans les noms des fichiers du jeu. Pour en ajouter : un nom par ligne dans\n{world.UserNamesFile}\n(lu au chargement de la carte). ")
                        + L("Les autres s'affichent en hexadécimal (0x...) : Ctrl+K les retrouve par leur hash."),
                    userError is not null ? UiTheme.Warning : null);
            }
            if (entityBoxLayer.MissingArchetypeCount > 0)
                Widgets.PropertyText(L("Sans type connu"), L($"{Widgets.Count(entityBoxLayer.MissingArchetypeCount)} objets proches (croix roses)"));
        }
        if (world.PathNetwork is { } network)
            Widgets.PropertyText(L("Pathnodes"), L($"{Widgets.Count(network.NodeCount)} nœuds · {Widgets.Count(network.LinkCount)} liens"));
        if (world.Scenarios is { } scenarios)
            Widgets.PropertyText(L("Scénarios"), L($"{Widgets.Count(scenarios.PointCount)} points · {scenarios.Regions.Count} régions"));
        if (world.NavMeshes is not null)
        {
            string loading = navMeshLayer.PendingTileCount > 0 ? L($" ({navMeshLayer.PendingTileCount} en cours)") : "";
            Widgets.PropertyText(L("Navmesh"), L($"{navMeshLayer.LoadedTileCount} / {navMeshLayer.AvailableTileCount} cases{loading}"));
        }
        if (world.Collisions is { } collisions)
        {
            string loading = collisionLayer.PendingFileCount > 0 ? L($" ({collisionLayer.PendingFileCount} en cours)") : "";
            Widgets.PropertyText(L("Collisions"), L($"{Widgets.Count(collisionLayer.LoadedFileCount)} / {Widgets.Count(collisions.Files.Count)} fichiers{loading}"),
                L($"{Widgets.Count(collisions.InteriorFileCount)} collisions d'intérieurs (pas encore affichées), {collisions.UnplacedCount} fichiers non situés par le jeu"));
        }
        Widgets.EndProperties();
    }

    private void DrawRender()
    {
        if (!Widgets.BeginProperties("##render"))
            return;
        Widgets.PropertyText(L("Carte graphique"), state.AdapterName);
        Widgets.PropertyText(L("Images par seconde"), $"{ImGui.GetIO().Framerate:0}");
        if (state.World.Models is not null)
        {
            string loading = modelLayer.PendingModelCount > 0 ? L($" ({modelLayer.PendingModelCount} en cours)") : "";
            Widgets.PropertyText(L("Objets dessinés"), $"{Widgets.Count(modelLayer.DrawnObjectCount)} / {Widgets.Count(modelLayer.NearbyObjectCount)}{loading}");
            Widgets.PropertyText(L("Triangles"), Widgets.Count(modelLayer.DrawnTriangleCount));
            Widgets.PropertyText(L("En mémoire"), L($"{Widgets.Count(modelLayer.LoadedModelCount)} modèles · {Widgets.Count(modelLayer.LoadedTextureCount)} textures"));
            string limit = modelLayer.MemoryLimit is > 0 and < long.MaxValue and var max ? L($" / {max / (1024 * 1024)} Mo") : "";
            Widgets.PropertyText(L("Mémoire vidéo"), L($"{modelLayer.GpuBytes / (1024 * 1024)} Mo{limit}"));
        }
        if (state.World.NavMeshes is not null)
            Widgets.PropertyText(L("Triangles du navmesh"), Widgets.Count(navMeshLayer.TriangleCount));
        if (state.World.Collisions is not null && collisionLayer.LoadedFileCount > 0)
            Widgets.PropertyText(L("Triangles de collision"), Widgets.Count(collisionLayer.TriangleCount));
        Widgets.EndProperties();
    }

    private void DrawProblems()
    {
        var lists = Diagnostics.ErrorReport.Problems(state, modelLayer, collisionLayer, navMeshLayer);
        if (lists.Count == 0)
            return;

        if (!Widgets.Section(L($"Problèmes ({lists.Sum(l => l.Items.Count)})"), "info_problems", defaultOpen: false))
            return;
        foreach (var (title, errors, note) in lists)
        {
            if (!ImGui.TreeNode($"{title} ({errors.Count})###{title}"))
                continue;
            if (note is not null)
                Widgets.Hint(note, wrap: true);
            foreach (var error in errors.Take(200))
                Widgets.Hint(error, wrap: true);
            ImGui.TreePop();
        }
    }

    private void DrawCamera()
    {
        var camera = state.Camera;
        if (!Widgets.BeginProperties("##camera"))
            return;
        var p = camera.Position;
        Widgets.PropertyText(L("Position"), $"X {p.X:0.0}   Y {p.Y:0.0}   Z {p.Z:0.0}", L($"Clic : copier {state.FormatPosition(p)}"));
        if (ImGui.IsItemClicked())
            ImGui.SetClipboardText(state.FormatPosition(p));
        float heading = (90f - camera.Yaw * 180f / MathF.PI) % 360f;
        if (heading < 0f) heading += 360f;
        Widgets.PropertyText(L("Cap"), L($"{heading:0}° (0° = nord)"));
        Widgets.PropertyText(L("Inclinaison"), $"{camera.Pitch * 180f / MathF.PI:0}°");
        Widgets.Property(L("Vitesse"));
        float speed = cameraController.Speed;
        if (ImGui.SliderFloat("##speed", ref speed, CameraController.MinSpeed, CameraController.MaxSpeed, "%.0f m/s", ImGuiSliderFlags.Logarithmic))
            cameraController.Speed = speed;
        Widgets.Property("");
        if (Widgets.Button(L($"{Icons.House}  Revenir à l'origine")))
        {
            camera.Position = new Vector3(-150f, -150f, 100f);
            camera.LookAt(Vector3.Zero);
        }
        Widgets.EndProperties();
    }

    private static void DrawControls()
    {
        if (!Widgets.BeginProperties("##controls", 0.42f))
            return;
        Widgets.PropertyText(L("Regarder autour"), L("clic droit maintenu + souris"));
        Widgets.PropertyText(L("Se déplacer"), L("Z Q S D, W A S D ou flèches"));
        Widgets.PropertyText(L("Monter / descendre"), L("Espace ou E / C"));
        Widgets.PropertyText(L("Plus vite"), L("Maj maintenue (x 5)"));
        Widgets.PropertyText(L("Vitesse"), L("molette"));
        Widgets.PropertyText(L("Sélectionner"), L("clic gauche"));
        Widgets.PropertyText(L("Désélectionner"), L("Échap"));
        Widgets.PropertyText(L("Annuler / rétablir"), L("Ctrl+Z / Ctrl+Y"));
        Widgets.PropertyText(L("Grille au sol"), "G");
        Widgets.EndProperties();
    }

    private void DrawFiles()
    {
        if (!Widgets.BeginProperties("##files", 0.25f))
            return;
        Widgets.Property(L("Réglages"));
        ImGui.TextWrapped(state.Paths.SettingsFile);
        Widgets.Property(L("Données"));
        ImGui.TextWrapped(state.Paths.DataDirectory);
        Widgets.EndProperties();
    }
}
