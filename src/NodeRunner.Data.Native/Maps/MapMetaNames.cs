namespace NodeRunner.Data.Native.Maps;

// Names of the structures, fields and enum values used by the game's .ymap and .ytyp files (the files only keep their
// hashes), as the XML exports write them.
internal static class MapMetaNames
{
    private static (Dictionary<uint, string> AsWritten, Dictionary<uint, string> Lower)? _byHash;

    // Some names are hashed as written, others in lower case (where "Name" and "name" meet, the camel case one is
    // the lower case hash: PascalCase names are hashed as written).
    public static string? Find(uint hash)
    {
        var (asWritten, lower) = _byHash ??= Build();
        return asWritten.GetValueOrDefault(hash) ?? lower.GetValueOrDefault(hash);
    }

    private static (Dictionary<uint, string>, Dictionary<uint, string>) Build()
    {
        var asWritten = new Dictionary<uint, string>();
        var lower = new Dictionary<uint, string>();
        foreach (string name in All)
        {
            asWritten.TryAdd(Resources.MetaXml.LiteralHash(name), name);
            uint hash = Hashing.Joaat.Hash(name);
            if (!lower.TryGetValue(hash, out string? known) || char.IsUpper(known[0]) && char.IsLower(name[0]))
                lower[hash] = name;
        }
        return (asWritten, lower);
    }

    public static readonly string[] All =
    [
        "ActivateVehicleSiren", "AerialVehiclePoint", "AggressiveVehicleDriving", "AllowInvestigation",
        "ambientOcclusionMultiplier", "AnimatedModel", "Animations", "AnimDict", "AnimName", "Ao", "archetypeName",
        "archetypes", "artificialAmbientOcclusion", "ASSET_TYPE_ASSETLESS", "ASSET_TYPE_DRAWABLE",
        "ASSET_TYPE_DRAWABLEDICTIONARY", "ASSET_TYPE_FRAGMENT", "ASSET_TYPE_UNINITIALIZED", "assetName", "assetType",
        "attachedObjects", "audioHash", "audioOcclusion", "AvailabilityInMpSp", "availableInMpSp", "BatchAABB",
        "bbMax", "bbMin", "blend", "block", "bmax", "bmin", "bodyColorRemap1", "bodyColorRemap2", "bodyColorRemap3",
        "bodyColorRemap4", "boneTag", "bottom", "BOUND_DEF_IS_PLANE", "BoxOccluder", "boxOccluders", "bsCentre",
        "bsRadius", "canBreak", "canGetOffAtBottom", "canGetOffAtTop", "CapsuleHalfHeight", "CapsuleHalfWidth",
        "CapsuleLen", "CapsuleRadius", "carGenerators", "carModel", "category", "CBaseArchetypeDef", "CBlockDesc",
        "CCarGen", "CCompEntityAnims", "CCompEntityEffectsData", "CCompositeEntityType", "CDistantLODLight",
        "CEntityDef", "CExtensionDefAudioCollisionSettings", "CExtensionDefAudioEmitter", "CExtensionDefBuoyancy",
        "CExtensionDefDoor", "CExtensionDefExplosionEffect", "CExtensionDefExpression", "CExtensionDefLadder",
        "CExtensionDefLadderMaterialType", "CExtensionDefLightEffect", "CExtensionDefLightShaft",
        "CExtensionDefLightShaftDensityType", "CExtensionDefLightShaftVolumeType", "CExtensionDefParticleEffect",
        "CExtensionDefProcObject", "CExtensionDefSpawnPoint", "CExtensionDefSpawnPointOverride",
        "CExtensionDefWindDisturbance", "CheckCrossedArrivalPlane", "childLodDist", "CLightAttrDef", "clipDictionary",
        "CLODLight", "CMapData", "CMapTypes", "CMloArchetypeDef", "CMloEntitySet", "CMloInstanceDef", "CMloPortalDef",
        "CMloRoomDef", "CMloTimeCycleModifier", "CollisionData", "Color", "color", "colour", "compositeEntityTypes",
        "coneInnerAngle", "coneOuterAngle", "coneOuterAngleOrCapExt", "containerLods", "contentFlags", "cornerA",
        "cornerB", "cornerC", "cornerD", "corners", "coronaIntensity", "coronaSize", "coronaZBias",
        "creatureMetadataName", "CScenarioPointFlags__Flags", "CSpawnPoint__AvailabilityMpSp", "CTimeArchetypeDef",
        "CTimeCycleModifier", "cullingPlane", "dataSize", "defaultEntitySets", "densityType", "dependencies",
        "direction", "directionAmount", "DistantLODLightsSOA", "disturbanceType", "doorTargetRatio",
        "drawableDictionary", "effectHash", "effectsData", "enableLimitAngle", "end", "endHour", "EndImapFile",
        "EndModel", "endPhase", "EndScenarioIfPlayerWithinRadius", "entities", "entitiesExtentsMax",
        "entitiesExtentsMin", "entitySets", "EventsInRadiusTriggerDisputes", "EventsInRadiusTriggerThreatResponse",
        "explosionName", "explosionTag", "explosionType", "exportedBy", "expressionDictionaryName", "expressionName",
        "ExtendedRange", "extendedRange", "extensions", "extents", "exteriorVisibiltyDepth", "fadeDistanceEnd",
        "fadeDistanceStart", "fadeInTimeEnd", "fadeInTimeStart", "fadeOutTimeEnd", "fadeOutTimeStart", "falloff",
        "falloffExponent", "Flags", "flags", "flashiness", "FloatXYZ", "floorId", "FlyOffToOblivion", "fxName",
        "fxOffsetPos", "fxOffsetRot", "fxType", "GrassInstanceList", "Group", "group", "groupId", "guid", "hash",
        "hdTextureDist", "highPri", "HighPriority", "iCenterX", "iCenterY", "iCenterZ", "iCosZ", "IgnoreLoitering",
        "IgnoreMaxInRange", "IgnoreThreatsIfLosNotClear", "IgnoreWeatherRestrictions", "iHeight", "iLength",
        "ImapLink", "initialiseOnCollision", "instancedData", "InstanceList", "instances", "intensity", "interior",
        "InWater", "iSinZ", "iTimeEndOverride", "iTimeStartOverride", "iWidth", "kBoth", "kOnlyMp", "kOnlySp",
        "LandVehicleOnArrival", "length", "lightFadeDistance", "lightHash", "LIGHTSHAFT_DENSITYTYPE_CONSTANT",
        "LIGHTSHAFT_DENSITYTYPE_LINEAR", "LIGHTSHAFT_DENSITYTYPE_LINEAR_GRADIENT", "LIGHTSHAFT_DENSITYTYPE_QUADRATIC",
        "LIGHTSHAFT_DENSITYTYPE_QUADRATIC_GRADIENT", "LIGHTSHAFT_DENSITYTYPE_SOFT",
        "LIGHTSHAFT_DENSITYTYPE_SOFT_SHADOW", "LIGHTSHAFT_DENSITYTYPE_SOFT_SHADOW_HD",
        "LIGHTSHAFT_VOLUMETYPE_CYLINDER", "LIGHTSHAFT_VOLUMETYPE_SHAFT", "lightType", "limitAngle", "livery",
        "locations", "lodDist", "LodFadeStartDist", "LodInstFadeRange", "lodLevel", "LODLightsSOA",
        "LODTYPES_DEPTH_HD", "LODTYPES_DEPTH_LOD", "LODTYPES_DEPTH_ORPHANHD", "LODTYPES_DEPTH_SLOD1",
        "LODTYPES_DEPTH_SLOD2", "LODTYPES_DEPTH_SLOD3", "LODTYPES_DEPTH_SLOD4", "materialType", "max", "maxExtents",
        "maxScale", "maxScaleZ", "maxZOffset", "METAL_LIGHT_LADDER", "METAL_SOLID_LADDER", "min", "minExtents",
        "minScale", "minScaleZ", "minZOffset", "mirrorPriority", "mloFlags", "MLOInstflags", "ModelSet", "Name",
        "name", "NoRespawnUntilStreamedOut", "Normal", "normal", "NormalX", "NormalY", "NoSpawn",
        "NoVehicleSpawnMaxDistance", "numChildren", "numExitPortals", "numStreetLights", "numTris", "numVertsInBytes",
        "objectHash", "OccludeModel", "occludeModels", "offsetPosition", "offsetRotation", "OnlySpawnInSameInterior",
        "opacity", "OpenDoor", "OrientToTerrain", "orientX", "orientY", "owner", "OwnerName", "Pad", "padding1",
        "padding2", "padding3", "parent", "parentIndex", "pedType", "percentage", "perpendicularLength",
        "physicsDictionaries", "physicsDictionary", "popGroup", "portalCount", "portals", "Position", "position",
        "posn", "PreciseUseTime", "PRI_OPTIONAL_HIGH", "PRI_OPTIONAL_LOW", "PRI_OPTIONAL_MEDIUM", "PRI_REQUIRED",
        "priorityLevel", "probability", "projectedTextureKey", "PropInstanceList", "PtFxAssetName", "ptFxHasTint",
        "ptFxIsTriggered", "ptFxProbability", "ptFxScale", "ptFxSize", "ptFxTag", "ptFxTintB", "ptFxTintG",
        "ptFxTintR", "punchInPhase", "punchOutPhase", "Radius", "radius", "radiusInner", "radiusOuter",
        "rage__eLodType", "rage__ePriorityLevel", "rage__fwArchetypeDef__eAssetType", "rage__fwContainerLodDef",
        "rage__fwGrassInstanceListDef", "rage__fwGrassInstanceListDef__InstanceData", "rage__fwInstancedMapData",
        "rage__fwPropInstanceListDef", "rage__phCapsuleBoundDef", "rage__phCapsuleBoundDef__enCollisionBoundDef",
        "rage__phVerletClothCustomBounds", "rage__spdAABB", "range", "requiredImap", "ResetNoCollisionOnCleanUp",
        "RGBI", "roomFrom", "rooms", "roomTo", "Rotation", "rotation", "Scale", "scale", "scaleBySunIntensity",
        "ScaleRange", "scaleXY", "scaleZ", "ScenarioType", "secondaryTimecycleName", "settings", "shadowBlur",
        "shadowFadeDistance", "shadowNearClip", "ShortRange", "shortRange", "size", "softness", "spacing",
        "SpawnedPedIsArrestable", "spawnType", "specialAttribute", "specularFadeDistance", "sphere", "start",
        "startHour", "StartImapFile", "StartModel", "startPhase", "startsLocked", "StationaryReactions",
        "streamingExtentsMax", "streamingExtentsMin", "strength", "tangent", "TaxiPlaneOnGround", "template",
        "TerritorialScenario", "textureDictionary", "time", "timeAndStateFlags", "timeCycleModifiers",
        "timecycleName", "timeFlags", "TimeTillPedLeaves", "timeTillPedLeaves", "tintValue", "top", "UseSearchlight",
        "UseVehicleFrontForArrival", "version", "verts", "volIntensity", "volOuterColour", "volOuterExponent",
        "volOuterIntensity", "volSizeScale", "volumetricFadeDistance", "volumeType", "WOODEN_LADDER", "x", "y", "z",
    ];
}
