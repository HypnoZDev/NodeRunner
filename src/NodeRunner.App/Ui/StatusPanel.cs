using System.Numerics;
using ImGuiNET;
using NodeRunner.App.Input;
using NodeRunner.App.World;
using NodeRunner.Data.Installation;

namespace NodeRunner.App.Ui;

/// <summary>
/// "État" panel (docked at the bottom right): game installation, what is loaded, rendering figures, camera,
/// files that could not be read and settings files, as label / value grids.
/// </summary>
internal sealed class StatusPanel(AppState state, MainUi ui, CameraController cameraController, NavMeshLayer navMeshLayer,
    CollisionLayer collisionLayer, EntityBoxLayer entityBoxLayer, ModelLayer modelLayer)
{
    public const string Title = $"{Icons.Activity}  État###État";

    public void Draw(ref bool visible)
    {
        if (ImGui.Begin(Title, ref visible))
        {
            if (Widgets.Section("Projet", "status_project"))
                DrawProject();
            if (Widgets.Section("GTA V", "status_game"))
                DrawGame();
            if (Widgets.Section("Carte chargée", "status_world"))
                DrawWorld();
            if (Widgets.Section("Rendu", "status_render"))
                DrawRender();
            if (Widgets.Section("Caméra", "status_camera"))
                DrawCamera();
            DrawProblems();
            if (Widgets.Section("Commandes de la caméra", "status_controls", defaultOpen: false))
                DrawControls();
            if (Widgets.Section("Fichiers de NodeRunner", "status_files", defaultOpen: false))
                DrawFiles();
        }
        ImGui.End();
    }

    private void DrawProject()
    {
        if (state.Project is not { } project)
        {
            ImGui.TextColored(UiTheme.TextMuted, "Aucun projet ouvert.");
            Widgets.Hint("Les modifications seront enregistrées dans un projet (menu Fichier > Nouveau projet).", wrap: true);
            return;
        }
        if (Widgets.BeginProperties("##project"))
        {
            Widgets.PropertyText("Nom", project.Name);
            Widgets.PropertyText("Dossier", project.Folder, project.Folder);
            Widgets.PropertyText("Ressource FiveM", project.ResourceName, "Nom utilisé à l'export (ensure ...)");
            Widgets.PropertyText("Export", project.ExportFolder ?? "pas encore exporté", project.ExportFolder);
            Widgets.EndProperties();
        }
    }

    private void DrawGame()
    {
        var game = state.Game;
        if (game is null)
        {
            ImGui.TextColored(UiTheme.Warning, "Aucune installation de GTA V trouvée.");
            Widgets.Hint("Indique le dossier du jeu manuellement (menu Fichier).", wrap: true);
            if (Widgets.PrimaryButton($"{Icons.FolderOpen}  Choisir le dossier..."))
                ui.BrowseGameFolderRequested = true;
            return;
        }

        if (Widgets.BeginProperties("##game"))
        {
            Widgets.PropertyText("Dossier", game.FolderPath);
            Widgets.PropertyText("Version", game.Edition == GameEdition.Legacy ? "Legacy" : "Enhanced");
            Widgets.PropertyText("Trouvée via", game.Source.ToString());
            foreach (var other in state.OtherInstallations)
            {
                Widgets.Property("Autre installation");
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted($"{other.FolderPath} ({other.Edition})");
                ImGui.SameLine();
                if (Widgets.Button($"Utiliser##{other.FolderPath}"))
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
        Widgets.PropertyText("État", world.Status, world.Error, statusColor);
        if (world.Content is { } content)
        {
            Widgets.PropertyText("Version", Toolbar.VersionName(content.Version));
            Widgets.PropertyText("Packs DLC", $"{content.Packs.Count} / {content.AllPacks.Count}");
            Widgets.PropertyText("Archives actives", Widgets.Count(content.ActiveArchiveCount));
        }
        if (world.Maps is { } maps)
        {
            Widgets.PropertyText("YMAP", Widgets.Count(maps.Maps.Count));
            int savedCount = maps.Maps.Count(m => m.IsSaved);
            if (savedCount > 0)
                Widgets.PropertyText("YMAP enregistrés", $"{savedCount} repris du projet", state.SaveFolder);
            Widgets.PropertyText("Objets placés", Widgets.Count(maps.EntityCount - maps.DeletedCount));
            if (maps.DeletedCount > 0)
                Widgets.PropertyText("Objets supprimés", Widgets.Count(maps.DeletedCount), "Supprimés dans cette session (Ctrl+Z pour les remettre)", UiTheme.Warning);
            Widgets.PropertyText("Types (archétypes)", Widgets.Count(maps.ArchetypeCount));
            if (world.Names is { } names)
            {
                var (userCount, userError) = world.UserNames;
                string user = userError is not null ? " · strings.txt illisible" : userCount > 0 ? $" · dont {Widgets.Count(userCount)} de strings.txt" : "";
                Widgets.PropertyText("Noms connus", Widgets.Count(names.Count) + user,
                    userError ?? $"Retrouvés dans les noms des fichiers du jeu. Pour en ajouter : un nom par ligne dans\n{world.UserNamesFile}\n(lu au chargement de la carte). "
                        + "Les autres s'affichent en hexadécimal (0x...) : Ctrl+K les retrouve par leur hash.",
                    userError is not null ? UiTheme.Warning : null);
            }
            if (entityBoxLayer.MissingArchetypeCount > 0)
                Widgets.PropertyText("Sans type connu", $"{Widgets.Count(entityBoxLayer.MissingArchetypeCount)} objets proches (croix roses)");
        }
        if (world.PathNetwork is { } network)
            Widgets.PropertyText("Pathnodes", $"{Widgets.Count(network.NodeCount)} nœuds · {Widgets.Count(network.LinkCount)} liens");
        if (world.Scenarios is { } scenarios)
            Widgets.PropertyText("Scénarios", $"{Widgets.Count(scenarios.PointCount)} points · {scenarios.Regions.Count} régions");
        if (world.NavMeshes is not null)
        {
            string loading = navMeshLayer.PendingTileCount > 0 ? $" ({navMeshLayer.PendingTileCount} en cours)" : "";
            Widgets.PropertyText("Navmesh", $"{navMeshLayer.LoadedTileCount} / {navMeshLayer.AvailableTileCount} cases{loading}");
        }
        if (world.Collisions is { } collisions)
        {
            string loading = collisionLayer.PendingFileCount > 0 ? $" ({collisionLayer.PendingFileCount} en cours)" : "";
            Widgets.PropertyText("Collisions", $"{Widgets.Count(collisionLayer.LoadedFileCount)} / {Widgets.Count(collisions.Files.Count)} fichiers{loading}",
                $"{Widgets.Count(collisions.InteriorFileCount)} collisions d'intérieurs (pas encore affichées), {collisions.UnplacedCount} fichiers non situés par le jeu");
        }
        Widgets.EndProperties();
    }

    private void DrawRender()
    {
        if (!Widgets.BeginProperties("##render"))
            return;
        Widgets.PropertyText("Carte graphique", state.AdapterName);
        Widgets.PropertyText("Images par seconde", $"{ImGui.GetIO().Framerate:0}");
        if (state.World.Models is not null)
        {
            string loading = modelLayer.PendingModelCount > 0 ? $" ({modelLayer.PendingModelCount} en cours)" : "";
            Widgets.PropertyText("Objets dessinés", $"{Widgets.Count(modelLayer.DrawnObjectCount)} / {Widgets.Count(modelLayer.NearbyObjectCount)}{loading}");
            Widgets.PropertyText("Triangles", Widgets.Count(modelLayer.DrawnTriangleCount));
            Widgets.PropertyText("En mémoire", $"{Widgets.Count(modelLayer.LoadedModelCount)} modèles · {Widgets.Count(modelLayer.LoadedTextureCount)} textures");
            Widgets.PropertyText("Mémoire vidéo", $"{modelLayer.GpuBytes / (1024 * 1024)} Mo");
        }
        if (state.World.NavMeshes is not null)
            Widgets.PropertyText("Triangles du navmesh", Widgets.Count(navMeshLayer.TriangleCount));
        if (state.World.Collisions is not null && collisionLayer.LoadedFileCount > 0)
            Widgets.PropertyText("Triangles de collision", Widgets.Count(collisionLayer.TriangleCount));
        Widgets.EndProperties();
    }

    /// <summary>Files that could not be read, one foldable list per kind; nothing at all when everything was read.</summary>
    private void DrawProblems()
    {
        var world = state.World;
        var lists = new List<(string Title, IReadOnlyCollection<string> Errors, string? Note)>();
        if (state.ProjectError is { } projectError)
            lists.Add(("Projet", [projectError], null));
        if (world.PathNetwork is { Errors.Count: > 0 } network)
            lists.Add(("Pathnodes illisibles", network.Errors, null));
        if (world.Content is { Errors.Count: > 0 } content)
            lists.Add(("Problèmes de DLC", content.Errors, null));
        if (world.Maps is { SavedMapErrors.Count: > 0 } savedMaps)
            lists.Add(("YMAP enregistrés non repris", savedMaps.SavedMapErrors, "La version du jeu est affichée à leur place."));
        if (world.Maps is { Errors.Count: > 0 } maps)
            lists.Add(("Fichiers de carte ignorés", maps.Errors, "Fichiers dans un autre format (XML, PSO), pas encore lus."));
        if (world.Textures is { Errors.Count: > 0 } textures)
            lists.Add(("Liens de textures illisibles", textures.Errors, null));
        if (modelLayer.Errors.Count > 0)
            lists.Add(("Modèles ou textures illisibles", modelLayer.Errors, null));
        if (world.Collisions is { Errors.Count: > 0 } collisionCatalog)
            lists.Add(("Fichiers cache illisibles", collisionCatalog.Errors, "Les collisions qu'ils situent ne sont pas affichées."));
        if (collisionLayer.Errors.Count > 0)
            lists.Add(("Collisions illisibles", collisionLayer.Errors, null));
        if (navMeshLayer.Errors.Count > 0)
            lists.Add(("Cases de navmesh illisibles", navMeshLayer.Errors, null));
        if (world.Scenarios is { Errors.Count: > 0 } scenarios)
            lists.Add(("Fichiers de scénarios ignorés", scenarios.Errors, null));
        if (lists.Count == 0)
            return;

        if (!Widgets.Section($"Problèmes ({lists.Sum(l => l.Errors.Count)})", "info_problems", defaultOpen: false))
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
        Widgets.PropertyText("Position", $"X {p.X:0.0}   Y {p.Y:0.0}   Z {p.Z:0.0}", $"Clic : copier {state.FormatPosition(p)}");
        if (ImGui.IsItemClicked())
            ImGui.SetClipboardText(state.FormatPosition(p));
        float heading = (90f - camera.Yaw * 180f / MathF.PI) % 360f;
        if (heading < 0f) heading += 360f;
        Widgets.PropertyText("Cap", $"{heading:0}° (0° = nord)");
        Widgets.PropertyText("Inclinaison", $"{camera.Pitch * 180f / MathF.PI:0}°");
        Widgets.Property("Vitesse");
        float speed = cameraController.Speed;
        if (ImGui.SliderFloat("##speed", ref speed, CameraController.MinSpeed, CameraController.MaxSpeed, "%.0f m/s", ImGuiSliderFlags.Logarithmic))
            cameraController.Speed = speed;
        Widgets.Property("");
        if (Widgets.Button($"{Icons.House}  Revenir à l'origine"))
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
        Widgets.PropertyText("Regarder autour", "clic droit maintenu + souris");
        Widgets.PropertyText("Se déplacer", "Z Q S D, W A S D ou flèches");
        Widgets.PropertyText("Monter / descendre", "Espace ou E / C");
        Widgets.PropertyText("Plus vite", "Maj maintenue (x 5)");
        Widgets.PropertyText("Vitesse", "molette");
        Widgets.PropertyText("Sélectionner", "clic gauche");
        Widgets.PropertyText("Désélectionner", "Échap");
        Widgets.PropertyText("Annuler / rétablir", "Ctrl+Z / Ctrl+Y");
        Widgets.PropertyText("Grille au sol", "G");
        Widgets.EndProperties();
    }

    private void DrawFiles()
    {
        if (!Widgets.BeginProperties("##files", 0.25f))
            return;
        Widgets.Property("Réglages");
        ImGui.TextWrapped(state.Paths.SettingsFile);
        Widgets.Property("Données");
        ImGui.TextWrapped(state.Paths.DataDirectory);
        Widgets.EndProperties();
    }
}
