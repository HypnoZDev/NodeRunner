using System.Globalization;
using System.Numerics;
using System.Text;

namespace NodeRunner.Editor;

// A simple room for an interior made behind an opening (BuildingOpening): floor, walls and ceiling seen from inside,
// the door left free in the front wall, and its collision (solid slabs around the room). In the interior's space: the
// opening on y = 0, x across, y into the building, z up from the floor.
public static class RoomShell
{
    public const float Thickness = 0.2f;

    public enum Surface { Floor, Wall, Ceiling }

    // A rectangle turned towards the inside of the room: corners Origin, Origin + U, Origin + U + V, Origin + V
    // (anticlockwise seen from the side it faces, as in the game's files: cross(U, V) points inwards).
    public readonly record struct Face(Surface Surface, Vector3 Origin, Vector3 U, Vector3 V)
    {
        public Vector3 Normal => Vector3.Normalize(Vector3.Cross(U, V));

        public Vector3[] Corners => [Origin, Origin + U, Origin + U + V, Origin + V];

        // Texture coordinates repeated every metre.
        public Vector2[] TexCoords => [Vector2.Zero, new(U.Length(), 0), new(U.Length(), -V.Length()), new(0, -V.Length())];
    }

    // roomSize: across, into the building, high; door: the opening's width and height.
    public static List<Face> Faces(Vector3 roomSize, float doorWidth, float doorHeight)
    {
        float hw = roomSize.X * 0.5f, depth = roomSize.Y, height = roomSize.Z;
        float dw = Math.Min(doorWidth, roomSize.X) * 0.5f, dh = Math.Min(doorHeight, height);
        float t = Math.Min(Thickness, depth * 0.5f);
        var faces = new List<Face>
        {
            new(Surface.Floor, new(-hw, 0, 0), new(2 * hw, 0, 0), new(0, depth, 0)),            // up
            new(Surface.Ceiling, new(-hw, depth, height), new(2 * hw, 0, 0), new(0, -depth, 0)), // down
            new(Surface.Wall, new(-hw, depth, 0), new(2 * hw, 0, 0), new(0, 0, height)),         // back wall, towards -Y
            new(Surface.Wall, new(-hw, 0, 0), new(0, depth, 0), new(0, 0, height)),              // left wall, towards +X
            new(Surface.Wall, new(hw, depth, 0), new(0, -depth, 0), new(0, 0, height)),          // right wall, towards -X
        };
        // Front wall, inside face (towards +Y), around the door.
        if (hw - dw > 0.01f)
        {
            faces.Add(new(Surface.Wall, new(-dw, t, 0), new(-(hw - dw), 0, 0), new(0, 0, height)));
            faces.Add(new(Surface.Wall, new(hw, t, 0), new(-(hw - dw), 0, 0), new(0, 0, height)));
        }
        if (height - dh > 0.01f)
            faces.Add(new(Surface.Wall, new(dw, t, dh), new(-2 * dw, 0, 0), new(0, 0, height - dh)));
        // Sides and top of the doorway, through the front wall's thickness, towards the doorway.
        faces.Add(new(Surface.Wall, new(-dw, 0, 0), new(0, t, 0), new(0, 0, dh)));
        faces.Add(new(Surface.Wall, new(dw, t, 0), new(0, -t, 0), new(0, 0, dh)));
        if (height - dh > 0.01f)
            faces.Add(new(Surface.Wall, new(-dw, t, dh), new(2 * dw, 0, 0), new(0, -t, 0)));
        return faces;
    }

    // The collision: solid slabs around the room, the front one with the door left free; floor tells its room.
    public static List<(Vector3 Min, Vector3 Max, bool Floor)> Slabs(Vector3 roomSize, float doorWidth, float doorHeight)
    {
        float hw = roomSize.X * 0.5f, depth = roomSize.Y, height = roomSize.Z;
        float dw = Math.Min(doorWidth, roomSize.X) * 0.5f, dh = Math.Min(doorHeight, height);
        float t = Thickness;
        var slabs = new List<(Vector3, Vector3, bool)>
        {
            (new(-hw - t, -t, -t), new(hw + t, depth + t, 0), true),
            (new(-hw - t, -t, height), new(hw + t, depth + t, height + t), false),
            (new(-hw, depth, 0), new(hw, depth + t, height), false),
            (new(-hw - t, 0, 0), new(-hw, depth + t, height), false),
            (new(hw, 0, 0), new(hw + t, depth + t, height), false),
        };
        float front = Math.Min(t, depth * 0.5f);
        if (hw - dw > 0.01f)
        {
            slabs.Add((new(-hw, 0, 0), new(-dw, front, height), false));
            slabs.Add((new(dw, 0, 0), new(hw, front, height), false));
        }
        if (height - dh > 0.01f)
            slabs.Add((new(-dw, 0, dh), new(dw, front, height), false));
        return slabs;
    }

    // Composite flags of the game's interior collisions (991 of their 1 526 parts): what they are (map weapon, dynamic,
    // animal, cover, vehicle) and what they collide with (vehicles, peds, ragdolls, animals, objects, plants...).
    internal const string TypeFlags = "MAP_WEAPON, MAP_DYNAMIC, MAP_ANIMAL, MAP_COVER, MAP_VEHICLE";
    internal const string IncludeFlags = "VEHICLE_NOT_BVH, VEHICLE_BVH, PED, RAGDOLL, ANIMAL, ANIMAL_RAGDOLL, OBJECT, PLANT, PROJECTILE, "
        + "EXPLOSION, FORKLIFT_FORKS, TEST_WEAPON, TEST_CAMERA, TEST_AI, TEST_SCRIPT, TEST_VEHICLE_WHEEL, GLASS";

    // The collision file in the XML form of Sollumz (YbnXml), in the interior's space. material: index in materials.dat.
    public static string CollisionXml(IReadOnlyList<(Vector3 Min, Vector3 Max, bool Floor)> slabs, int material, int room = 1)
    {
        static string F(float v) => v.ToString("0.#####", CultureInfo.InvariantCulture);
        var vertices = new StringBuilder();
        var polygons = new StringBuilder();
        int index = 0;
        foreach (var (min, max, floor) in slabs)
        {
            // The box's four stored corners: centre + X + Y + Z, + X - Y - Z, - X + Y - Z, - X - Y + Z.
            var c = (min + max) * 0.5f;
            var h = (max - min) * 0.5f;
            foreach (var corner in new[] { c + h, c + new Vector3(h.X, -h.Y, -h.Z), c + new Vector3(-h.X, h.Y, -h.Z), c + new Vector3(-h.X, -h.Y, h.Z) })
                vertices.Append(F(corner.X)).Append(", ").Append(F(corner.Y)).Append(", ").Append(F(corner.Z)).Append('\n');
            polygons.Append($"<Box m=\"{(floor ? 1 : 0)}\" v1=\"{index}\" v2=\"{index + 1}\" v3=\"{index + 2}\" v4=\"{index + 3}\" />\n");
            index += 4;
        }
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <BoundsFile>
             <Bounds type="Composite">
              <Children>
               <Item type="GeometryBVH">
                <CompositeTransform>1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1</CompositeTransform>
                <CompositeFlags1>{TypeFlags}</CompositeFlags1>
                <CompositeFlags2>{IncludeFlags}</CompositeFlags2>
                <GeometryCenter x="0" y="0" z="0" />
                <Materials>
                 <Item><Type value="{material}" /></Item>
                 <Item><Type value="{material}" /><RoomID value="{room}" /></Item>
                </Materials>
                <Vertices>
            {vertices}</Vertices>
                <Polygons>
            {polygons}</Polygons>
               </Item>
              </Children>
             </Bounds>
            </BoundsFile>
            """;
    }

    // The archetype of the room's model in the XML form of CodeWalker / Sollumz (YtypXml). Names unknown as text are
    // written "hash_XXXXXXXX".
    public static string TypesXml(string name, string textureDictionary, float lodDistance, Vector3 boxMin, Vector3 boxMax)
    {
        static string F(float v) => v.ToString("0.#####", CultureInfo.InvariantCulture);
        static string V(string tag, Vector3 v) => $"<{tag} x=\"{F(v.X)}\" y=\"{F(v.Y)}\" z=\"{F(v.Z)}\" />";
        var center = (boxMin + boxMax) * 0.5f;
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <CMapTypes>
             <extensions />
             <archetypes>
              <Item type="CBaseArchetypeDef">
               <lodDist value="{F(lodDistance)}" />
               <flags value="0" />
               <specialAttribute value="0" />
               {V("bbMin", boxMin)}
               {V("bbMax", boxMax)}
               {V("bsCentre", center)}
               <bsRadius value="{F(Vector3.Distance(boxMin, boxMax) * 0.5f)}" />
               <hdTextureDist value="5" />
               <name>{name}</name>
               <textureDictionary>{textureDictionary}</textureDictionary>
               <clipDictionary />
               <drawableDictionary />
               <physicsDictionary />
               <assetType>ASSET_TYPE_DRAWABLE</assetType>
               <assetName>{name}</assetName>
               <extensions />
              </Item>
             </archetypes>
             <name>{name}</name>
             <dependencies />
             <compositeEntityTypes />
            </CMapTypes>
            """;
    }
}
