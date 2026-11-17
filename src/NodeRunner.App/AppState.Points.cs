using NodeRunner.App.World;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;
using NodeRunner.Render;

namespace NodeRunner.App;

internal sealed partial class AppState
{
    // A new parking spot (5 x 3 m, any car) 12 m in front of the camera, facing away from it, in the project's own
    // map; selected, with the "move" tool.
    public void AddCarGenerator()
    {
        Notice = null;
        if (World.Maps is not { } library || ProjectMap(library) is not { } map)
            return;
        var forward = Camera.Forward with { Z = 0 };
        forward = forward.LengthSquared() > 1e-6f ? System.Numerics.Vector3.Normalize(forward) : System.Numerics.Vector3.UnitY;
        var point = Camera.Position + forward * 12f;
        float ground = GroundHeightAt(point) ?? Camera.Position.Z - 10f;
        var spot = new CarGenerator
        {
            // The game's spots are about 1 m above the road (the car is put on the ground below).
            Position = point with { Z = ground + 1f },
            Orientation = new System.Numerics.Vector2(forward.X, forward.Y) * 5f,
            Width = 3f,
            Flags = 0xE60, // The most common value in the game's spots.
        };
        var edit = CarGeneratorAddEdit.Apply(map, spot, L("Ajouter une place de véhicule"));
        History.Add(edit);
        SetLayerVisible(CarGeneratorLayer.LayerName, true);
        Select(null);
        SelectedCarGenerator = new CarGeneratorSelection(map, edit.Index);
        if (Tool == TransformTool.Select)
            Tool = TransformTool.Move;
        NotifyUndoable(L($"Place de véhicule ajoutée dans {Path.GetFileName(map.FilePath ?? "")}. Ctrl+Z pour annuler."));
    }

    // Node waiting for a click on another node to link them; forgotten as soon as it is no longer selected.
    public Data.Paths.PathNode? PathLinkSource
    {
        get => _pathLinkSource is { } source && source == SelectedPathNode ? source : null;
        set => _pathLinkSource = value;
    }

    private Data.Paths.PathNode? _pathLinkSource;

    // A road node: 10 m from "from" (away from its neighbours) and linked to it, or 15 m in front of the camera with
    // the settings of the nearest node. Selected, with the "move" tool.
    public void AddPathNode(Data.Paths.PathNode? from)
    {
        Notice = null;
        if (World.PathNetwork is not { } network)
            return;
        var flatForward = Camera.Forward with { Z = 0 };
        flatForward = flatForward.LengthSquared() > 1e-6f ? System.Numerics.Vector3.Normalize(flatForward) : System.Numerics.Vector3.UnitY;
        System.Numerics.Vector3 point;
        var template = from;
        int forward = 1, backward = 1;
        if (from is not null)
        {
            var away = System.Numerics.Vector3.Zero;
            var links = network.GetArea(from.Id.AreaId)?.LinksOf(from).ToList() ?? [];
            foreach (var link in links)
                if (network.GetNode(link.Target) is { } neighbour && (from.Position - neighbour.Position) with { Z = 0 } is { } d && d.LengthSquared() > 0.01f)
                    away += System.Numerics.Vector3.Normalize(d);
            var direction = away.LengthSquared() > 0.01f ? System.Numerics.Vector3.Normalize(away) : flatForward;
            point = from.Position + direction * 10f;
            if (links.Count > 0)
                (forward, backward) = (links[0].LanesForward, links[0].LanesBackward);
        }
        else
        {
            point = Camera.Position + flatForward * 15f;
            template = network.Areas.SelectMany(a => a.Nodes).Where(n => !n.IsPedestrian)
                .MinBy(n => System.Numerics.Vector3.DistanceSquared(n.Position, point));
        }
        point = point with { Z = GroundHeightAt(point) ?? from?.Position.Z ?? Camera.Position.Z - 10f };
        bool pedestrian = template?.IsPedestrian ?? false;
        var values = template?.Values ?? new Data.Paths.PathNodeValues(2, 0, 0, 0, 8, 2);
        if (!pedestrian)
            values = values.WithSpecial(Data.Paths.PathNodeSpecialType.None);
        if (PathNodeAddEdit.Apply(network, point, pedestrian, values, template?.StreetNameHash ?? 0, from, forward, backward,
                L("Ajouter un nœud")) is not { } edit)
        {
            Notify(L("Impossible d'ajouter un nœud ici : hors de la carte, ou le nœud de départ a déjà 31 liens."), warning: true);
            return;
        }
        History.Add(edit);
        Select(null);
        SelectedPathNode = edit.Node;
        Tool = TransformTool.Move;
    }

    // Links two nodes both ways, with the lanes of the first one's first link (1 each way without one).
    public void LinkPathNodes(Data.Paths.PathNode a, Data.Paths.PathNode b)
    {
        if (World.PathNetwork is not { } network)
            return;
        var first = network.GetArea(a.Id.AreaId)?.LinksOf(a).FirstOrDefault();
        int forward = first?.LanesForward ?? 1, backward = first?.LanesBackward ?? 1;
        if (PathLinkAddEdit.Apply(network, a, b, forward, backward,
                L($"Relier les nœuds {a.Id.AreaId}:{a.Id.NodeId} et {b.Id.AreaId}:{b.Id.NodeId}")) is not { } edit)
        {
            Notify(L("Ces nœuds sont déjà reliés, ou l'un d'eux a déjà 31 liens."), warning: true);
            return;
        }
        History.Add(edit);
        NotifyUndoable(L($"Nœuds {a.Id.AreaId}:{a.Id.NodeId} et {b.Id.AreaId}:{b.Id.NodeId} reliés. Ctrl+Z pour annuler."));
    }

    public void DeleteSelectedPathNode()
    {
        if (SelectedPathNode is not { } node || World.PathNetwork is not { } network)
            return;
        var id = node.Id;
        if (PathNodeDeleteEdit.Apply(network, node, L($"Supprimer le nœud {id.AreaId}:{id.NodeId}")) is not { } edit)
            return;
        History.Add(edit);
        SelectedPathNode = null;
        NotifyUndoable(L($"Nœud {id.AreaId}:{id.NodeId} supprimé avec ses liens. Ctrl+Z pour annuler."));
    }

    // A scenario point: a copy of from (2 m beside it), or 15 m in front of the camera on the ground, in the region of the
    // nearest point (whose models, group... it takes). Selected, with the "move" tool.
    public void AddScenarioPoint(Data.Scenarios.ScenarioPoint? from)
    {
        Notice = null;
        if (World.Scenarios is not { } set)
            return;
        var flatForward = Camera.Forward with { Z = 0 };
        flatForward = flatForward.LengthSquared() > 1e-6f ? System.Numerics.Vector3.Normalize(flatForward) : System.Numerics.Vector3.UnitY;
        var position = from is null ? Camera.Position + flatForward * 15f : from.Position + new System.Numerics.Vector3(-flatForward.Y, flatForward.X, 0) * 2f;
        position = position with { Z = GroundHeightAt(position) ?? from?.Position.Z ?? Camera.Position.Z - 10f };
        // The region of the nearest point (its box in sp_manifest.ymt is grown when saved).
        var template = from ?? set.Nearest(position);
        if (template?.Region is not { } region)
            return;
        var values = template.Values with { Position = position };
        var edit = ScenarioAddEdit.Apply(set, region, values, template, L($"Ajouter un point de scénario ({region.Name}.ymt)"));
        History.Add(edit);
        Select(null);
        SelectedScenario = edit.Point;
        Tool = TransformTool.Move;
    }

    public void DeleteSelectedScenario()
    {
        if (SelectedScenario is not { } point || World.Scenarios is not { } set)
            return;
        string region = point.Region?.Name ?? "";
        if (ScenarioDeleteEdit.Apply(set, point, L($"Supprimer le point de scénario {point.Index} ({region}.ymt)")) is not { } edit)
        {
            return;
        }
        History.Add(edit);
        SelectedScenario = null;
        NotifyUndoable(L($"Point de scénario supprimé de {region}.ymt. Ctrl+Z pour annuler."));
    }

    public void DeleteSelectedCarGenerator()
    {
        if (SelectedCarGenerator is not { } selected)
            return;
        History.Add(CarGeneratorDeleteEdit.Apply(selected.Map, selected.Index, L($"Supprimer la place de véhicule {selected.Index}")));
        SelectedCarGenerator = null;
        NotifyUndoable(L($"Place de véhicule {selected.Index} supprimée de {Path.GetFileName(selected.Map.FilePath ?? "")}. Ctrl+Z pour annuler."));
    }

    // A new timecycle zone (10 x 10 x 6 m, no modifier yet) 15 m in front of the camera, standing on the ground there,
    // in the project's own map; selected, with the "move" tool.
    public void AddTimecycleZone()
    {
        Notice = null;
        if (World.Maps is not { } library || ProjectMap(library) is not { } map)
            return;
        var forward = Camera.Forward with { Z = 0 };
        forward = forward.LengthSquared() > 1e-6f ? System.Numerics.Vector3.Normalize(forward) : System.Numerics.Vector3.UnitY;
        var point = Camera.Position + forward * 15f;
        float ground = GroundHeightAt(point) ?? Camera.Position.Z - 10f;
        var half = new System.Numerics.Vector3(5f, 5f, 0f);
        var zone = new TimecycleModifier
        {
            NameHash = 0,
            BoundsMin = point with { Z = ground } - half,
            BoundsMax = point with { Z = ground + 6f } + half,
            Percentage = 100f,
            Range = 1f,     // The most common values in the game's zones (measured: 100 %, 1 m).
            StartHour = 0,
            EndHour = 23,
        };
        var edit = TimecycleAddEdit.Apply(map, zone, L("Ajouter une zone de timecycle"));
        History.Add(edit);
        SetLayerVisible(TimecycleLayer.LayerName, true);
        Select(null);
        SelectedTimecycle = new TimecycleSelection(map, edit.Index);
        if (Tool == TransformTool.Select)
            Tool = TransformTool.Move;
        Notify(L($"Zone de timecycle ajoutée dans {Path.GetFileName(map.FilePath ?? "")} : choisissez son modificateur dans Propriétés."));
    }

    public void DeleteSelectedTimecycleZone()
    {
        if (SelectedTimecycle is not { } selected)
            return;
        History.Add(TimecycleDeleteEdit.Apply(selected.Map, selected.Index, L($"Supprimer la zone de timecycle {selected.Index}")));
        SelectedTimecycle = null;
        NotifyUndoable(L($"Zone de timecycle {selected.Index} supprimée de {Path.GetFileName(selected.Map.FilePath ?? "")}. Ctrl+Z pour annuler."));
    }

    // --- LOD lights ---

    // A new distant light 10 m in front of the camera, 5 m above the ground there, in the project's own light files
    // (<resource>_distlodlights.ymap and <resource>_lodlights.ymap, created if needed); selected, with the "move" tool.
    // Its values are the most common ones of the game's lights (measured on 359 600 lights): an orange street lamp
    // pointing down, on from 20:00 to 6:00.
    public void AddLodLight()
    {
        Notice = null;
        if (World.Maps is not { } library)
            return;
        if (Project is not { } project)
        {
            Notify(L("Ouvre ou crée d'abord un projet : les lumières ajoutées vont dans les fichiers de lumières du projet."), warning: true);
            return;
        }
        string resource = project.ResourceName;
        var distant = library.FindCustomMap($"{resource}_distlodlights.ymap") ?? library.CreateCustomMap($"{resource}_distlodlights.ymap");
        var lod = library.FindCustomMap($"{resource}_lodlights.ymap") ?? library.CreateCustomMap($"{resource}_lodlights.ymap");
        lod.ParentHash = distant.NameHash;

        var forward = Camera.Forward with { Z = 0 };
        forward = forward.LengthSquared() > 1e-6f ? System.Numerics.Vector3.Normalize(forward) : System.Numerics.Vector3.UnitY;
        var point = Camera.Position + forward * 10f;
        float ground = GroundHeightAt(point) ?? Camera.Position.Z - 5f;
        point = point with { Z = ground + 5f };
        var light = new DistantLodLight(point, DistantLodLight.Pack(0xFF, 0x9D, 0x66, 170));
        // The game gives each light its own hash: one made from the place is enough to tell them apart.
        uint hash = Data.Hashing.Joaat.Hash(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{resource}_light_{point.X:0.00}_{point.Y:0.00}_{point.Z:0.00}"));
        var details = new LodLight(-System.Numerics.Vector3.UnitZ, 10f, 64f, 0x08F0003F, hash, 14, 85, 7);
        LodLightAddEdit edit;
        try
        {
            edit = LodLightAddEdit.Apply(distant, lod, light, details, L("Ajouter une lumière LOD"));
        }
        catch (InvalidDataException ex)
        {
            Notify(L($"Impossible d'ajouter la lumière : {ex.Message}"), warning: true);
            return;
        }
        History.Add(edit);
        SetLayerVisible(LodLightLayer.LayerName, true);
        Select(null);
        SelectedLodLight = new LodLightSelection(distant, edit.Index, lod);
        if (Tool == TransformTool.Select)
            Tool = TransformTool.Move;
        Notify(L($"Lumière LOD ajoutée dans {distant.FilePath} : réglez sa couleur et ses heures dans Propriétés."));
    }

    public void DeleteSelectedLodLight()
    {
        if (SelectedLodLight is not { } selected)
            return;
        History.Add(LodLightDeleteEdit.Apply(selected.Distant, selected.Lod, selected.Index, L($"Supprimer la lumière LOD {selected.Index}")));
        SelectedLodLight = null;
        NotifyUndoable(L($"Lumière LOD {selected.Index} supprimée de {Path.GetFileName(selected.Distant.FilePath ?? "")}. Ctrl+Z pour annuler."));
    }
}
