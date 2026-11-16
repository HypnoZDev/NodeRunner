using NodeRunner.App.World;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;
using NodeRunner.Render;

namespace NodeRunner.App;

internal sealed partial class AppState
{
    public string OccluderFileName => $"{Project?.ResourceName ?? FiveMResource.DefaultName}_occl.ymap";

    // A new 10 m box occluder 25 m in front of the camera, standing on the ground there.
    public void CreateOccluderBox()
    {
        if (OccluderMaps.ProjectFile(World, OccluderFileName) is not { } file)
            return;
        var forward = Camera.Forward with { Z = 0 };
        forward = forward.LengthSquared() > 1e-6f ? System.Numerics.Vector3.Normalize(forward) : System.Numerics.Vector3.UnitY;
        var point = Camera.Position + forward * 25f;
        float ground = GroundHeightAt(point) ?? Camera.Position.Z - 10f;
        var size = new System.Numerics.Vector3(10f);
        var box = new OccluderBox(0, point with { Z = ground + size.Z / 2 }, size, MathF.Atan2(forward.Y, forward.X)).Quantized();
        AddOccluderBoxes(file, [box], L("Nouvelle boîte d'occlusion"));
    }

    // Box occluders inside each selected object (OccluderFit): inside the walls of its collision, one per closed
    // building, or its model's box a little smaller where there is no collision.
    public void CreateOccluderBoxesFromSelection()
    {
        if (World.Maps is not { } library || OccluderMaps.ProjectFile(World, OccluderFileName) is not { } file)
            return;
        var boxes = new List<OccluderBox>();
        int fromModel = 0, open = 0, skipped = 0, mlo = 0, mloEmpty = 0;
        // An interior, or an object inside one, gets boxes in the outer walls of the interior: a box filling it would
        // hide what is in its rooms.
        foreach (var interior in SelectedEntities.Select(e => e.IsInterior ? e : e.Interior).OfType<MapEntity>().Distinct())
        {
            if (library.GetArchetype(interior.ArchetypeHash) is not { } archetype)
                continue;
            var walls = MloOccluders.Fit(interior, archetype, InteriorCollision(interior), World.Collisions?.Materials);
            boxes.AddRange(walls);
            if (walls.Count > 0)
                mlo++;
            else
                mloEmpty++;
        }
        foreach (var entity in SelectedEntities.Where(e => !e.IsInterior && e.Interior is null))
        {
            switch (FitOccluderBoxes(entity, library.GetArchetype(entity.ArchetypeHash)))
            {
                case { Source: OccluderFitSource.NoClosedSpace }:
                    open++;
                    break;
                case { Boxes.Count: > 0 } fit:
                    boxes.AddRange(fit.Boxes);
                    if (fit.Source == OccluderFitSource.Model)
                        fromModel++;
                    break;
                default:
                    skipped++;
                    break;
            }
        }
        var notes = new List<string>();
        if (mloEmpty > 0)
            notes.Add(L($"{mloEmpty} intérieur(s) sans mur extérieur plein"));
        if (open > 0)
            notes.Add(L($"{open} objet(s) qui ne sont pas des bâtiments fermés"));
        if (skipped > 0)
            notes.Add(L($"{skipped} objet(s) sans volume ou trop plat(s)"));
        if (fromModel > 0)
            notes.Add(L($"{fromModel} objet(s) sans collision ni modèle lisible : boîte du modèle"));
        if (boxes.Count == 0)
        {
            Notify(L($"aucune boîte créée : {string.Join(", ", notes)}"), warning: true);
            return;
        }
        AddOccluderBoxes(file, boxes, mlo > 0 ? L($"{boxes.Count} boîtes d'occlusion dans les murs de l'intérieur")
            : boxes.Count == 1 ? L("Boîte d'occlusion sur l'objet") : L($"{boxes.Count} boîtes d'occlusion sur les objets"));
        if (notes.Count > 0)
            Notify(L($"{boxes.Count} boîte(s) créée(s) ; {string.Join(", ", notes)}"), warning: true);
    }

    // The interior's own collision, in its local space.
    private Data.Collisions.CollisionMesh? InteriorCollision(MapEntity interior)
    {
        if (World.Collisions is not { } catalog || catalog.FindInteriorFile(interior.ArchetypeHash) is not { } path)
            return null;
        try
        {
            return catalog.Read(path);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return null;
        }
    }

    private OccluderFitResult? FitOccluderBoxes(MapEntity entity, Archetype? archetype)
    {
        if (GroundSnap.WorldBox(entity, archetype) is not var (min, max) || World.Maps is not { } library)
            return OccluderFit.Fit(entity, archetype);
        var collisions = World.Collisions is null ? null : CollisionQuery.CollisionIn(World, min, max, CollisionEdits);
        // Interiors (MLO) placed there stay free of occluders.
        var interiors = new List<(System.Numerics.Vector3 Min, System.Numerics.Vector3 Max)>();
        var center = new System.Numerics.Vector2((min.X + max.X) / 2, (min.Y + max.Y) / 2);
        float radius = System.Numerics.Vector2.Distance(center, new System.Numerics.Vector2(max.X, max.Y)) + 200f;
        foreach (var interior in library.EntitiesAround(center, radius))
        {
            if (interior.IsInterior && GroundSnap.WorldBox(interior, library.GetArchetype(interior.ArchetypeHash)) is { } box
                && box.Min.X <= max.X && box.Max.X >= min.X && box.Min.Y <= max.Y && box.Max.Y >= min.Y && box.Min.Z <= max.Z && box.Max.Z >= min.Z)
                interiors.Add(box);
        }
        return OccluderFit.Fit(entity, archetype, collisions, ModelTriangles(entity, archetype), interiors);
    }

    // The triangles of an object's model in world positions (three points each), or null when it cannot be read.
    private List<System.Numerics.Vector3>? ModelTriangles(MapEntity entity, Archetype? archetype)
    {
        if (archetype is null || World.Models?.Locate(archetype) is not { } location || World.ModelReader is not { } reader)
            return null;
        try
        {
            if (reader.ReadModels(location.Path).GetValueOrDefault(location.ModelHash) is not { } model)
                return null;
            var transform = entity.Transform;
            var triangles = new List<System.Numerics.Vector3>(model.TriangleCount * 3);
            foreach (var mesh in model.Meshes)
            {
                foreach (ushort index in mesh.Indices)
                    triangles.Add(System.Numerics.Vector3.Transform(mesh.Positions[index], transform));
            }
            return triangles;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return null;
        }
    }

    private void AddOccluderBoxes(OccluderMapFile file, IReadOnlyList<OccluderBox> boxes, string description)
    {
        var edit = OccluderAddEdit.Apply(file, boxes, description);
        var added = edit.Added;
        History.Add(edit);
        SetLayerVisible(OcclusionLayer.LayerName(OcclusionLayerPart.Boxes), true);
        Select(null);
        SelectedOccluder = added[^1];
        Notify(added.Count == 1 ? L($"boîte d'occlusion ajoutée à {file.FileName}") : L($"{added.Count} boîtes d'occlusion ajoutées à {file.FileName}"));
    }

    // Selects an occluder, shows its layer and moves the camera in front of it.
    public void ShowOccluder(OccluderSelection occluder)
    {
        if (occluder.File.IsDeleted(occluder.Kind, occluder.Index))
            return;
        Select(null);
        SelectedOccluder = occluder;
        SetLayerVisible(OcclusionLayer.LayerName(occluder.Kind), true);
        var (min, max) = occluder.Bounds();
        FocusOn((min + max) / 2, System.Numerics.Vector3.Distance(min, max) / 2);
    }

    public void DeleteSelectedOccluder()
    {
        if (SelectedOccluder is not { } occluder)
            return;
        OccluderMaps.SetDeleted(occluder.File, occluder.Kind, occluder.Index, true);
        bool box = occluder.Kind == OcclusionLayerPart.Boxes;
        History.Add(new OccluderPresenceEdit([occluder], deleted: true,
            box ? L($"Supprimer la boîte d'occlusion {occluder.Index}") : L($"Supprimer le maillage d'occlusion {occluder.Index}")));
        SelectedOccluder = null;
        Notify(box ? L($"Boîte d'occlusion {occluder.Index} supprimée de {occluder.File.FileName}")
            : L($"Maillage d'occlusion {occluder.Index} supprimé de {occluder.File.FileName}"));
    }
}
