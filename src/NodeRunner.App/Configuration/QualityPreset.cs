namespace NodeRunner.App.Configuration;

// A quality preset of the display settings: the values it sets. Low and Medium keep the simple rendering of small
// PCs; High and Ultra add the scene's own HDR image and its effects.
internal sealed record QualityPreset(string Name, string Tooltip, float ViewDistance, float FogDistance, int Anisotropy, bool Shadows,
    float ShadowDistance, bool WaterReflections, bool Hdr, int ShadowResolution, bool Antialiasing, int AmbientOcclusion, bool Bloom, bool WaterWaves, bool SurfaceDetail, bool NightLights, bool PhysicalSky, bool SunRays)
{
    public static readonly QualityPreset[] All =
    [
        new("Basse", "Pour un PC modeste : moins d'objets détaillés au loin, brouillard plus proche (rien n'est dessiné derrière), textures filtrées simplement, sans ombres ni reflets sur l'eau.",
            0.6f, 2000f, 1, false, 400f, false, false, 1024, false, 0, false, false, false, false, false, false),
        new("Moyenne", "Rendu simple, comme les versions précédentes : ombres et reflets sur l'eau, sans les effets de lumière.",
            1f, 4000f, 8, true, 400f, true, false, 2048, false, 0, false, false, false, false, false, false),
        new("Haute", "Pour une carte graphique récente : lumière réaliste (HDR), détails plus loin, brouillard lointain, textures du sol nettes au loin, ombres plus loin.",
            1.5f, 8000f, 16, true, 800f, true, true, 2048, true, 1, true, true, true, true, true, true),
        new("Ultra", "Pour une carte graphique puissante : tous les effets au plus haut, ombres plus fines et plus loin, détails encore plus loin.",
            2f, 12000f, 16, true, 1000f, true, true, 4096, true, 2, true, true, true, true, true, true),
    ];

    public void ApplyTo(AppSettings settings)
    {
        settings.ViewDistanceMultiplier = ViewDistance;
        settings.FogDistance = FogDistance;
        settings.Anisotropy = Anisotropy;
        settings.Shadows = Shadows;
        settings.ShadowDistance = ShadowDistance;
        settings.WaterReflections = WaterReflections;
        settings.Fog = true;
        ApplyEffectsTo(settings);
    }

    // Only the rendering effects, keeping the distances and the other choices.
    public void ApplyEffectsTo(AppSettings settings)
    {
        settings.HdrRendering = Hdr;
        settings.ShadowResolution = ShadowResolution;
        settings.Antialiasing = Antialiasing;
        settings.AmbientOcclusion = AmbientOcclusion;
        settings.Bloom = Bloom;
        settings.WaterWaves = WaterWaves;
        settings.SurfaceDetail = SurfaceDetail;
        settings.NightLights = NightLights;
        settings.PhysicalSky = PhysicalSky;
        settings.SunRays = SunRays;
    }

    public bool Matches(AppSettings settings) =>
        settings.Fog && MathF.Abs(settings.ViewDistanceMultiplier - ViewDistance) < 0.01f
        && MathF.Abs(settings.FogDistance - FogDistance) < 1f && settings.Anisotropy == Anisotropy
        && settings.Shadows == Shadows && (!Shadows || MathF.Abs(settings.ShadowDistance - ShadowDistance) < 1f)
        && settings.WaterReflections == WaterReflections && settings.HdrRendering == Hdr && settings.ShadowResolution == ShadowResolution
        && settings.Antialiasing == Antialiasing && settings.AmbientOcclusion == AmbientOcclusion
        && settings.Bloom == Bloom && settings.WaterWaves == WaterWaves
        && settings.SurfaceDetail == SurfaceDetail && settings.NightLights == NightLights
        && settings.PhysicalSky == PhysicalSky && settings.SunRays == SunRays;

    // The preset for a graphics card, by its own video memory: the simple rendering without one (software rendering,
    // graphics chip sharing the PC's memory) or with less than 3 GB.
    public static QualityPreset ForCard(bool software, long dedicatedMemory)
    {
        const long Gb = 1L << 30;
        if (software || dedicatedMemory < 3 * Gb)
            return All[1];
        return dedicatedMemory < 15 * Gb / 2 ? All[2] : All[3];
    }
}
