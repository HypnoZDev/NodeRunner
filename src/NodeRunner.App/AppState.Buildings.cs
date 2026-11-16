using System.Numerics;
using NodeRunner.App.World;
using NodeRunner.Data.Collisions;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Models;
using NodeRunner.Editor;

namespace NodeRunner.App;

internal sealed partial class AppState
{
    // Opening a building (an interior behind a door drawn on its wall), from Properties; null when not in progress.
    public BuildingOpeningTool? OpeningTool { get; set; }

    public void StartBuildingOpening(MapEntity building)
    {
        if (Online.IsActive)
        {
            Notify(L("Pas pendant une session Online : le modèle du bâtiment est réécrit et la carte rechargée."), warning: true);
            return;
        }
        var (model, error) = ModelPartTool.Open(building, World);
        if (model is null)
        {
            Notify(error ?? L("Modèle 3D introuvable."), warning: true);
            return;
        }
        PartTool = null;
        var tool = new BuildingOpeningTool(building, model)
        {
            Name = InteriorCreation.SuggestName(World.Names?.TryGet(building.ArchetypeHash) ?? Project?.ResourceName ?? "mlo", IsFreeInteriorName),
        };
        if (World.Maps is { } library)
            tool.Door = BuildingOpening.ChooseDoor(library.GetArchetypeHashes().Select(h => (h, World.Names?.TryGet(h), library.GetArchetype(h)!)), tool.Width);
        OpeningTool = tool;
        Notify(L("Cliquez sur la porte dessinée sur le mur du bâtiment (Échap pour arrêter)."));
    }

    // Makes the interior behind the opening, in this order: the game's doors in the opening go, the interior is created
    // with its room and its portal on the opening, the door is attached to the portal, the collision and the occluders
    // in the way are cut or removed; the project is saved; then the building's model is cut and saved, which reloads the
    // map (the changes can no longer be undone with Ctrl+Z afterwards: "Remettre le modèle d'origine" and Suppr do it).
    public void OpenBuilding()
    {
        if (OpeningTool is not { Current: { } opening } tool || World.Maps is not { } library)
            return;
        if (Project is null)
        {
            Notify(L("Ouvrez ou créez d'abord un projet : l'intérieur et le bâtiment percé y sont enregistrés."), warning: true);
            return;
        }
        if (!IsFreeInteriorName(tool.Name))
        {
            Notify(L($"Nom d'intérieur « {tool.Name} » invalide ou déjà pris (lettres minuscules, chiffres et _)."), warning: true);
            return;
        }
        if (ProjectMap(library) is not { } map)
            return;
        var door = tool.AddDoor && tool.Door is { } chosen && library.GetArchetype(chosen.Hash) is { } doorType ? (chosen.Hash, chosen.Name, doorType) : default;
        var plan = BuildingOpening.Make(opening, tool.RoomSize, door.Hash, door.doorType, door.Name);
        string building = World.Names?.Describe(tool.Building.ArchetypeHash) ?? L("le bâtiment");
        var report = new List<string>();

        // The game's doors standing in the opening.
        if (tool.RemoveGameDoors)
        {
            var doors = GameDoorsIn(library, opening);
            if (doors.Count > 0)
            {
                History.Add(DeleteEdit.Apply(library, DeletionPlan.For(doors), L($"Retirer {doors.Count} porte(s) du jeu")));
                report.Add(L($"{doors.Count} porte(s) du jeu retirée(s)"));
            }
        }

        var create = InteriorCreateEdit.Apply(library, map, tool.Name, plan.Position, plan.Orientation, L($"Créer l'intérieur {tool.Name} derrière {building}"));
        History.Add(create);
        World.Names?.Add(tool.Name);
        var interior = create.Interior;
        var archetype = library.GetArchetype(interior.ArchetypeHash)!;
        History.Add(RoomEdit.Apply(library, interior, 1, plan.Room with { SourceIndex = archetype.InteriorRooms[1].SourceIndex }, L("Salle de l'intérieur")));
        History.Add(PortalEdit.Apply(library, interior, 0, plan.Portal with { SourceIndex = archetype.InteriorPortals[0].SourceIndex }, L("Porte de l'intérieur")));
        if (plan.Door is { } definition)
        {
            var added = InteriorAddEdit.Apply(library, interior, [definition], L($"Poser la porte {door.Name}"));
            History.Add(added);
            History.Add(EntityPortalEdit.Apply(library, added.Selection, [(-1, 0)], L($"Rattacher {door.Name} au portail")));
            report.Add(L($"porte {door.Name} rattachée au portail"));
        }

        if (tool.AddRoom)
        {
            var (shell, shellError) = WriteRoomShell(tool, opening);
            if (shell is { } shellHash)
            {
                var placed = new MapEntity
                {
                    ArchetypeHash = shellHash,
                    Position = Vector3.Zero,
                    StoredRotation = Quaternion.Identity,
                    Flags = Adding.StaticFlags,
                    Guid = (uint)Random.Shared.Next(1, int.MaxValue),
                    LodDistance = RoomShellLodDistance,
                    LodLevel = LodLevel.OrphanHd,
                    InteriorRoom = 0,
                };
                History.Add(InteriorAddEdit.Apply(library, interior, [placed], L("Pièce de l'intérieur (sol, murs, plafond)")));
                report.Add(L("pièce simple créée (sol, murs, plafond, collision)"));
            }
            else
                report.Add(L($"pièce simple non créée : {shellError}"));
        }

        if (tool.CutCollision)
        {
            var files = CollisionFilesCut(opening);
            if (files.Count > 0)
            {
                var cut = CollisionOpening.Of(opening);
                var edit = new CollisionOpeningEdit(CollisionEdits, L("Percer la collision"), [.. files.Select(f => (f, cut))]);
                edit.Redo();
                History.Add(edit);
                report.Add(L($"collision percée ({files.Count} fichier(s))"));
            }
        }
        if (tool.RemoveOccluders)
        {
            var occluders = OccludersIn(plan);
            if (occluders.Count > 0)
            {
                var edit = new OccluderPresenceEdit(occluders, true, L($"Retirer {occluders.Count} occluder(s) sur l'intérieur"));
                edit.Redo();
                History.Add(edit);
                report.Add(L($"{occluders.Count} occluder(s) retiré(s)"));
            }
        }

        SaveMaps();
        if (LastSave is { Success: false } failed)
        {
            Notify(L($"Intérieur créé mais projet non enregistré : {failed.Text}. Le modèle du bâtiment n'a pas été percé."), warning: true);
            OpeningTool = null;
            return;
        }
        var (count, error) = tool.Model.CutOpening(BuildingOpening.ToModel(opening, Inverse(tool.Building.Transform)));
        if (error is not null)
        {
            Notify(L($"Intérieur « {tool.Name} » créé et enregistré, mais le modèle n'a pas pu être percé : {error}"), warning: true);
            OpeningTool = null;
            return;
        }
        var (done, message) = tool.Model.Save(this);
        OpeningTool = null;
        report.Insert(0, L($"modèle percé ({count} triangles)"));
        Notify(done
            ? L($"Bâtiment ouvert : intérieur « {tool.Name} » créé, ") + string.Join(", ", report) + L(". Projet enregistré, rechargement de la carte...")
            : message, warning: !done);
    }

    private static Matrix4x4 Inverse(Matrix4x4 m) => Matrix4x4.Invert(m, out var inverse) ? inverse : Matrix4x4.Identity;

    private const float RoomShellLodDistance = 100f;

    // The room's own model (made from the building's model: its materials), its type file and the interior's
    // collision, written into the project's stream folder: <name>_shell.ydr, <name>_shell.ytyp and <name>.ybn (the
    // collision of an interior is the .ybn named after it). Returns the model's type, or an error message.
    private (uint? Archetype, string? Error) WriteRoomShell(BuildingOpeningTool tool, Opening opening)
    {
        if (SaveFolder is not { } stream || World.Maps is not { } library || World.Content is not { } content)
            return (null, L("projet non ouvert"));
        if (library.GetArchetype(tool.Building.ArchetypeHash) is not { } type || World.Models?.Locate(type) is not { } location)
            return (null, L("modèle du bâtiment introuvable"));
        if (!location.Path.EndsWith(".ydr", StringComparison.OrdinalIgnoreCase))
            return (null, L("le modèle du bâtiment n'est pas un .ydr"));
        if (content.ReadFile(location.Path) is not { } data || content.GetResourceHeader(location.Path) is not { } header
            || library.GetArchetypeFile(tool.Building.ArchetypeHash) is not { } typesFile || content.ReadFile(typesFile) is not { } typesData)
            return (null, L("fichiers du bâtiment illisibles"));

        string name = tool.Name + "_shell";
        var faces = RoomShell.Faces(tool.RoomSize, opening.Width, opening.Height)
            .Select(f => new Data.Native.Models.ShellModel.Face(f.Surface == RoomShell.Surface.Floor, f.Corners, f.TexCoords, f.Normal, Vector3.Normalize(f.U)))
            .ToList();
        if (Data.Native.Models.ShellModel.Build(header, data, location.ModelHash, faces, out var error) is not { } model)
            return (null, error);
        int concrete = Enumerable.Range(0, 256).FirstOrDefault(i => World.Collisions?.Materials.Name((byte)i) == "CONCRETE", 1);
        try
        {
            var schema = new Data.Native.Resources.MetaSchema([typesData], []);
            string textures = type.TextureDictionaryHash == 0 ? ""
                : World.Names?.TryGet(type.TextureDictionaryHash) ?? $"hash_{type.TextureDictionaryHash:X8}";
            var size = tool.RoomSize;
            var typesXml = RoomShell.TypesXml(name, textures, RoomShellLodDistance, new Vector3(-size.X * 0.5f, 0, 0), size with { X = size.X * 0.5f });
            var types = Data.Native.Maps.YtypXml.Convert(new XmlExport(name + ".ytyp.xml", typesXml, _ => null), schema);
            var collision = Data.Native.Collisions.YbnXml.Convert(RoomShell.CollisionXml(RoomShell.Slabs(size, opening.Width, opening.Height), concrete));
            Directory.CreateDirectory(stream);
            SafeFile.WriteAllBytes(Path.Combine(stream, name + ".ydr"), Data.Native.Resources.ResourceFile.Write(model.Header, model.Content));
            SafeFile.WriteAllBytes(Path.Combine(stream, name + ".ytyp"), types);
            SafeFile.WriteAllBytes(Path.Combine(stream, tool.Name + ".ybn"), collision);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return (null, ex.Message);
        }
        World.Names?.Add(name);
        return (Data.Hashing.Joaat.Hash(name), null);
    }

    // Doors of the game's map (not of an interior) standing in the opening.
    private List<MapEntity> GameDoorsIn(MapLibrary library, Opening opening)
    {
        var center = opening.Center;
        return library.EntitiesAround(new Vector2(center.X, center.Y), opening.Width + 3f)
            .Where(e => !e.IsInterior && !e.IsDeleted && e.Interior is null && e.CanDelete && Doors.IsDoor(World.Names?.TryGet(e.ArchetypeHash)))
            .Where(e =>
            {
                var local = opening.Local(e.Position);
                return MathF.Abs(local.X) < opening.Width * 0.5f + 0.6f && local.Y > -0.5f && local.Y < opening.Height && MathF.Abs(local.Z) < 1f;
            })
            .ToList();
    }

    // Map collision files (every version: walking, vehicles, others) whose triangles the opening cuts.
    private List<string> CollisionFilesCut(Opening opening)
    {
        var files = new List<string>();
        if (World.Collisions is not { } catalog)
            return files;
        var reach = new Vector3(opening.Width + opening.Depth, opening.Width + opening.Depth, 0);
        var min = opening.Bottom - reach;
        var max = opening.Bottom + reach + new Vector3(0, 0, opening.Height);
        foreach (var kind in Enum.GetValues<CollisionKind>())
        {
            foreach (var file in catalog.FilesIn(min, max, kind))
            {
                try
                {
                    if (catalog.Read(file) is { } mesh && !WallOpening.Cut(mesh.Vertices, mesh.Indices, opening).IsEmpty)
                        files.Add(file.Path);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException)
                {
                    Diagnostics.ErrorLog.Add(L("Collisions"), ex);
                }
            }
        }
        return files;
    }

    // The game's occluders reaching into the new room: seen from inside, they would hide it.
    private List<OccluderSelection> OccludersIn(BuildingOpening.Plan plan)
    {
        var (toLocal, min, max) = BuildingOpening.RoomVolume(plan);
        const float Inset = 0.3f;
        bool Inside(Vector3 world)
        {
            var p = Vector3.Transform(world, toLocal);
            return p.X > min.X + Inset && p.Y > min.Y + Inset && p.Z > min.Z + Inset && p.X < max.X - Inset && p.Y < max.Y - Inset && p.Z < max.Z - Inset;
        }
        Matrix4x4.Invert(toLocal, out var toWorld);
        var roomCenter = Vector3.Transform((min + max) * 0.5f, toWorld);
        var found = new List<OccluderSelection>();
        foreach (var file in OccluderMaps.Get(World).Where(f => !f.IsProject))
        {
            for (int i = 0; i < file.Boxes.Count; i++)
            {
                if (file.IsDeleted(OcclusionLayerPart.Boxes, i))
                    continue;
                var box = file.Boxes[i];
                if (box.Corners().Append(box.Center).Any(Inside) || OccluderCheck.Inside(box, roomCenter))
                    found.Add(new OccluderSelection(file, OcclusionLayerPart.Boxes, i));
            }
            for (int i = 0; i < file.Occluders.Models.Count; i++)
            {
                if (file.IsDeleted(OcclusionLayerPart.Models, i))
                    continue;
                var model = file.Occluders.Models[i];
                bool crosses = model.Vertices.Any(Inside);
                for (int t = 0; !crosses && t < model.TriangleCount; t++)
                    crosses = Inside((model.Vertices[model.Indices[t * 3]] + model.Vertices[model.Indices[t * 3 + 1]] + model.Vertices[model.Indices[t * 3 + 2]]) / 3f);
                if (crosses)
                    found.Add(new OccluderSelection(file, OcclusionLayerPart.Models, i));
            }
        }
        return found;
    }
}
