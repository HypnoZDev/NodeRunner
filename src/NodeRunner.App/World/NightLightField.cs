using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Gta4;
using NodeRunner.Render;

namespace NodeRunner.App.World;

// The map's lights lit at the hour shown (the game's distant lights: street lamps, windows, signs), for NightLights.
// Worked out again in the background when the map, the hour or a light changes.
internal sealed class NightLightField
{
    private MapLibrary? _library;
    private int _hour = -1;
    private int _revision = -1;
    private Task<(MapLibrary Library, NightLight[] Lights)>? _building;
    private Gta4Definitions? _gta4;

    // Lights to send to the renderer (null: unchanged since the last call). With GTA IV's definitions: the lamps of
    // its models (lit all night, whatever the hour).
    public NightLight[]? Update(MapLibrary? library, int hour, Gta4Definitions? gta4 = null)
    {
        if (gta4 is not null)
            hour = 0;
        NightLight[]? result = null;
        if (_building is { IsCompleted: true } finished)
        {
            _building = null;
            if (finished.IsCompletedSuccessfully)
            {
                if (finished.Result.Library == library)
                    result = finished.Result.Lights;
            }
            else
            {
                Diagnostics.ErrorLog.Add(L("Lumières de nuit"), finished.Exception!.GetBaseException());
            }
        }
        if (library is null)
        {
            if (_library is null)
                return result;
            _library = null;
            return [];
        }
        if (_building is null && (library != _library || hour != _hour || gta4 != _gta4 || _revision != MapData.LodLightRevision))
        {
            (_library, _hour, _gta4, _revision) = (library, hour, gta4, MapData.LodLightRevision);
            _building = Task.Run(() => (library, gta4 is null ? Build(library, hour) : BuildGta4(library, gta4)));
        }
        return result;
    }

    private static NightLight[] Build(MapLibrary library, int hour)
    {
        var distant = new Dictionary<uint, MapData>();
        foreach (var map in library.Maps)
        {
            if (map.DistantLights.Count > 0)
                distant.TryAdd(map.NameHash, map);
        }
        var details = new Dictionary<MapData, MapData>();
        foreach (var map in library.Maps)
        {
            if (map.LodLights.Count > 0 && distant.TryGetValue(map.ParentHash, out var parent))
                details.TryAdd(parent, map);
        }

        bool night = hour >= 20 || hour < 6;
        var lights = new List<NightLight>();
        foreach (var map in distant.Values)
        {
            details.TryGetValue(map, out var lod);
            for (int i = 0; i < map.DistantLights.Count; i++)
            {
                if (map.IsLightDeleted(i))
                    continue;
                var light = map.DistantLights[i];
                LodLight? detail = lod is not null && i < lod.LodLights.Count ? lod.LodLights[i] : null;
                if (detail is { } d ? !d.IsOnAt(hour) : !night)
                    continue;
                lights.Add(Make(light, detail));
            }
        }
        return lights.ToArray();
    }

    // GTA IV: the lamps declared for the models, on every detailed object placed (inside the interiors too).
    private static NightLight[] BuildGta4(MapLibrary library, Gta4Definitions definitions)
    {
        var lights = new List<NightLight>();
        void Add(MapEntity entity)
        {
            if (entity.LodLevel is not (LodLevel.Hd or LodLevel.OrphanHd) || !definitions.Lights.TryGetValue(entity.ArchetypeHash, out var lamps))
                return;
            var transform = entity.Transform;
            foreach (var lamp in lamps)
                lights.Add(Make(lamp, transform, entity.Orientation));
        }
        foreach (var map in library.Maps)
        {
            foreach (var entity in map.Entities)
            {
                if (entity.IsInterior)
                {
                    foreach (var inside in entity.InteriorEntities)
                        Add(inside);
                }
                else
                {
                    Add(entity);
                }
            }
        }
        return lights.ToArray();
    }

    private static NightLight Make(Gta4ModelLight lamp, Matrix4x4 transform, Quaternion orientation)
    {
        var color = new Vector3(Linear(lamp.Red), Linear(lamp.Green), Linear(lamp.Blue));
        float brightness = 2.5f * Math.Clamp(lamp.Intensity, 0.1f, 2f);
        float reach = Math.Clamp(lamp.Reach, 1f, 60f);
        var direction = Vector3.Zero;
        float cosOuter = -2f, cosInner = -2f;
        if (lamp.IsSpot && lamp.Direction.LengthSquared() > 1e-6f && lamp.ConeOuter >= 1f)
        {
            direction = Vector3.Normalize(Vector3.Transform(lamp.Direction, orientation));
            float outer = MathF.Min(lamp.ConeOuter, 89f);
            float inner = Math.Clamp(lamp.ConeInner, 0f, outer * 0.9f);
            cosOuter = MathF.Cos(outer * MathF.PI / 180f);
            cosInner = MathF.Cos(inner * MathF.PI / 180f);
        }
        return new NightLight(Vector3.Transform(lamp.Position, transform), reach, color * brightness, 0.8f, direction, cosOuter, cosInner);
    }

    private static NightLight Make(DistantLodLight light, LodLight? detail)
    {
        var color = new Vector3(Linear(light.Red), Linear(light.Green), Linear(light.Blue));
        // Brightness byte of the game (0 to 255): never quite off, so every lamp shows.
        float brightness = 0.6f + light.Intensity / 255f * 3.4f;
        float reach = detail is { } d ? Math.Clamp(d.Falloff, 1f, 60f) : 8f;
        var direction = Vector3.Zero;
        float cosOuter = -2f, cosInner = -2f;
        if (detail is { Type: LodLightType.Spot } spot && spot.Direction.LengthSquared() > 1e-6f)
        {
            direction = Vector3.Normalize(spot.Direction);
            // Half angles in steps of 180 / 255 degrees.
            float outer = MathF.Min(spot.ConeOuterAngleOrCapExt * 180f / 255f, 89f);
            float inner = MathF.Min(spot.ConeInnerAngle * 180f / 255f, outer * 0.9f);
            if (outer >= 1f)
            {
                cosOuter = MathF.Cos(outer * MathF.PI / 180f);
                cosInner = MathF.Cos(inner * MathF.PI / 180f);
            }
        }
        float corona = 0.5f + (detail?.CoronaIntensity ?? 0) / 255f * 2f;
        return new NightLight(light.Position, reach, color * brightness, corona, direction, cosOuter, cosInner);
    }

    private static float Linear(byte value) => MathF.Pow(value / 255f, 2.2f);
}
