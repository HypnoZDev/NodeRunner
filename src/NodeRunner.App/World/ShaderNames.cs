using NodeRunner.Data.Hashing;
using NodeRunner.Data.Models;

namespace NodeRunner.App.World;

// Names of the game's shaders and of their texture parameters (the files only keep hashes), for the model viewer.
internal static class ShaderNames
{
    private static readonly Dictionary<uint, string> Shaders = new[]
    {
        "vehicle_paint1", "vehicle_paint1_enveff", "vehicle_paint2", "vehicle_paint2_enveff", "vehicle_paint3",
        "vehicle_paint3_enveff", "vehicle_paint3_lvr", "vehicle_paint4", "vehicle_paint4_emissive", "vehicle_paint4_enveff",
        "vehicle_paint5_enveff", "vehicle_paint6", "vehicle_paint6_enveff", "vehicle_paint7", "vehicle_paint7_enveff",
        "vehicle_paint8", "vehicle_paint9", "vehicle_mesh", "vehicle_mesh_enveff", "vehicle_mesh2_enveff", "vehicle_tire",
        "vehicle_tire_emissive", "vehicle_interior", "vehicle_interior2", "vehicle_vehglass", "vehicle_vehglass_inner",
        "vehicle_lightsemissive", "vehicle_lightsemissive_siren", "vehicle_lights", "vehicle_dash_emissive",
        "vehicle_dash_emissive_opaque", "vehicle_licenseplate", "vehicle_badges", "vehicle_decal", "vehicle_decal2",
        "vehicle_detail", "vehicle_detail2", "vehicle_shuts", "vehicle_track", "vehicle_track2", "vehicle_track_ammo",
        "vehicle_track_cutout", "vehicle_track_emissive", "vehicle_track_siren", "vehicle_blurredrotor",
        "vehicle_blurredrotor_emissive", "vehicle_cloth", "vehicle_cloth2", "vehicle_cutout", "vehicle_emissive_opaque",
        "vehicle_emissive_alpha", "vehicle_generic", "vehicle_basic", "vehicle_nosplash", "vehicle_nowater",
        "default", "default_detail", "default_spec", "default_terrain_wet", "default_tnt", "default_um", "default_noedge",
        "normal", "normal_alpha", "normal_cutout", "normal_decal", "normal_decal_tnt", "normal_detail", "normal_detail_dpm",
        "normal_diffspec", "normal_diffspec_detail", "normal_diffspec_detail_dpm", "normal_diffspec_detail_tnt",
        "normal_diffspec_tnt", "normal_pxm", "normal_reflect", "normal_reflect_alpha", "normal_reflect_decal", "normal_spec",
        "normal_spec_alpha", "normal_spec_cutout", "normal_spec_decal", "normal_spec_decal_detail", "normal_spec_decal_nopuddle",
        "normal_spec_decal_tnt", "normal_spec_detail", "normal_spec_detail_dpm", "normal_spec_detail_dpm_tnt",
        "normal_spec_detail_tnt", "normal_spec_dpm", "normal_spec_emissive", "normal_spec_pxm", "normal_spec_pxm_tnt",
        "normal_spec_reflect", "normal_spec_reflect_alpha", "normal_spec_reflect_decal", "normal_spec_reflect_emissivenight",
        "normal_spec_tnt", "normal_spec_um", "normal_spec_wrinkle", "normal_tnt", "normal_tnt_alpha", "normal_um",
        "normal_um_tnt", "spec", "spec_alpha", "spec_const", "spec_decal", "spec_reflect", "spec_reflect_alpha",
        "spec_reflect_decal", "spec_tnt", "spec_twiddle_tnt", "cutout", "cutout_fence", "cutout_fence_normal", "cutout_hard",
        "cutout_spec_tnt", "cutout_tnt", "cutout_um", "decal", "decal_amb_only", "decal_diff_only_um", "decal_dirt",
        "decal_emissive_only", "decal_emissivenight_only", "decal_glue", "decal_normal_only", "decal_shadow_only",
        "decal_spec_only", "decal_tnt", "alpha", "emissive", "emissive_alpha", "emissive_alpha_tnt", "emissive_clip",
        "emissive_speclum", "emissive_tnt", "emissivenight", "emissivenight_alpha", "emissivenight_geomnightonly",
        "emissivestrong", "emissivestrong_alpha", "glass", "glass_breakable", "glass_breakable_screendooralpha",
        "glass_displacement", "glass_emissive", "glass_emissive_alpha", "glass_emissivenight", "glass_emissivenight_alpha",
        "glass_env", "glass_normal_spec_reflect", "glass_pv", "glass_pv_env", "glass_reflect", "glass_spec", "cpv_only",
        "grass", "grass_batch", "grass_fur", "grass_fur_mask", "parallax", "parallax_specmap", "parallax_steep", "mirror_default",
        "mirror_decal", "mirror_crack", "ped", "ped_alpha", "ped_cloth", "ped_decal", "ped_default", "ped_fur", "ped_hair_cutout_alpha",
        "ped_hair_spiked", "ped_wrinkle", "ped_emissive", "terrain_cb_4lyr", "terrain_cb_4lyr_2tex", "terrain_cb_w_4lyr",
        "terrain_cb_w_4lyr_2tex", "terrain_cb_w_4lyr_2tex_blend", "terrain_cb_w_4lyr_2tex_blend_lod", "terrain_cb_w_4lyr_2tex_pxm",
        "terrain_cb_w_4lyr_cm", "terrain_cb_w_4lyr_cm_pxm", "terrain_cb_w_4lyr_cm_pxm_tnt", "terrain_cb_w_4lyr_cm_tnt",
        "terrain_cb_w_4lyr_lod", "terrain_cb_w_4lyr_pxm", "terrain_cb_w_4lyr_pxm_spm", "terrain_cb_w_4lyr_spec",
        "terrain_cb_w_4lyr_spec_pxm", "trees", "trees_camera_aligned", "trees_camera_facing", "trees_lod", "trees_lod2",
        "trees_lod_tnt", "trees_normal", "trees_normal_diffspec", "trees_normal_diffspec_tnt", "trees_normal_spec",
        "trees_normal_spec_tnt", "trees_tnt", "trees_shadow_proxy", "water_fountain", "water_poolenv", "water_river",
        "water_riverfoam", "water_riverlod", "water_riverocean", "water_rivershallow", "water_shallow", "water_terrainfoam",
        "water_decal", "radar", "sky_system", "clouds_altitude", "distance_map", "blend_2lyr", "minimap", "weapon_normal_spec_tnt",
        "weapon_normal_spec_alpha", "weapon_normal_spec_cutout_palette", "weapon_normal_spec_detail_palette",
        "weapon_normal_spec_detail_tnt", "weapon_normal_spec_palette", "weapon_emissive_tnt", "weapon_emissivestrong_alpha",
    }.DistinctBy(Joaat.Hash).ToDictionary(Joaat.Hash, n => n);

    // Texture parameters, with what they hold.
    private static readonly Dictionary<uint, (string Name, string Role)> Samplers = new (string, string)[]
    {
        ("DiffuseSampler", "Couleur"), ("DiffuseSampler2", "Livrée / décalcomanie"), ("DiffuseSampler3", "Livrée 2"),
        ("DiffuseExtraSampler", "Couleur (supplément)"), ("BumpSampler", "Relief (normal map)"), ("SpecSampler", "Brillance (specular)"),
        ("DirtSampler", "Saleté"), ("DirtBumpSampler", "Relief de la saleté"), ("DetailSampler", "Détail"),
        ("EnvironmentSampler", "Reflets"), ("SnowSampler0", "Neige 1"), ("SnowSampler1", "Neige 2"), ("TintPaletteSampler", "Palette de teintes"),
        ("PlateBgSampler", "Fond de plaque"), ("PlateBgBumpSampler", "Relief du fond de plaque"), ("FontSampler", "Police de la plaque"),
        ("FontNormalSampler", "Relief de la police"), ("TextureSampler_layer0", "Couche 1"), ("TextureSampler_layer1", "Couche 2"),
        ("TextureSampler_layer2", "Couche 3"), ("TextureSampler_layer3", "Couche 4"), ("BumpSampler_layer0", "Relief couche 1"),
        ("BumpSampler_layer1", "Relief couche 2"), ("BumpSampler_layer2", "Relief couche 3"), ("BumpSampler_layer3", "Relief couche 4"),
        ("lookupSampler", "Masque de mélange"), ("heightSampler", "Hauteur (parallaxe)"), ("DistanceMapSampler", "Carte de distance"),
        ("WrinkleSampler_A", "Rides A"), ("WrinkleSampler_B", "Rides B"), ("WrinkleMaskSampler_0", "Masque de rides"),
        ("StubbleSampler", "Barbe"), ("noiseSampler", "Bruit"), ("FlowSampler", "Écoulement"), ("FoamSampler", "Écume"),
        ("DiffuseSamplerPhase2", "Couleur (phase 2)"), ("ComboHeightSamplerFur01", "Hauteur de fourrure"), ("diffuseSampler2", "Couleur 2"),
    }.DistinctBy(s => Joaat.Hash(s.Item1)).ToDictionary(s => Joaat.Hash(s.Item1), s => s);

    public static string Shader(uint hash) => hash == 0 ? L("(aucun)") : Shaders.GetValueOrDefault(hash) ?? $"0x{hash:X8}";

    public static string SamplerName(uint hash) => Samplers.TryGetValue(hash, out var s) ? s.Name : $"0x{hash:X8}";

    public static string SamplerRole(uint hash) => Samplers.TryGetValue(hash, out var s) ? L(s.Role) : L("Autre");

    // Colour textures first, then the others in a fixed order.
    public static int SamplerOrder(uint hash) => Samplers.TryGetValue(hash, out var s) ? Array.IndexOf(Order, s.Name) is >= 0 and var i ? i : 50 : 99;

    private static readonly string[] Order = ["DiffuseSampler", "DiffuseSampler2", "DiffuseSampler3", "BumpSampler", "SpecSampler", "DirtSampler"];

    public static string Slot(VehiclePaintSlot slot) => slot switch
    {
        VehiclePaintSlot.Primary => L("Primaire"),
        VehiclePaintSlot.Secondary => L("Secondaire"),
        VehiclePaintSlot.Pearlescent => L("Nacré"),
        VehiclePaintSlot.Wheel => L("Jantes"),
        VehiclePaintSlot.Unpainted => L("Non peint"),
        VehiclePaintSlot.InteriorTrim => L("Garnitures"),
        VehiclePaintSlot.Dashboard => L("Tableau de bord"),
        _ => "",
    };

    public static string BlendMode(MeshBlendMode mode) => mode switch
    {
        MeshBlendMode.Cutout => L("Découpé"),
        MeshBlendMode.Translucent => L("Transparent"),
        MeshBlendMode.Water => L("Eau"),
        _ => L("Opaque"),
    };
}
