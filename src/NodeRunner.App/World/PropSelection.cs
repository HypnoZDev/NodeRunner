using NodeRunner.Data.Hashing;
using NodeRunner.Editor.Localization;

namespace NodeRunner.App.World;

// A short choice of the game's props by kind (chairs, tables...), shown in the prop library: a few different ones per
// kind rather than every variant.
internal static class PropSelection
{
    public sealed record Prop(string Model, string French, string English)
    {
        public string Label => Loc.IsFrench ? French : English;
    }

    public sealed record Category(string French, string English, Prop[] Props)
    {
        public string Label => Loc.IsFrench ? French : English;
    }

    public static readonly Category[] Categories =
    [
        new("Chaises & tabourets", "Chairs & stools",
        [
            new("prop_chair_01a", "Chaise de terrasse", "Patio chair"),
            new("apa_mp_h_din_chair_04", "Chaise de salle à manger", "Dining chair"),
            new("prop_old_wood_chair", "Vieille chaise en bois", "Old wooden chair"),
            new("prop_cs_folding_chair_01", "Chaise pliante", "Folding chair"),
            new("prop_direct_chair_01", "Chaise de réalisateur", "Director's chair"),
            new("prop_rock_chair_01", "Chaise à bascule", "Rocking chair"),
            new("prop_chateau_chair_01", "Fauteuil de jardin", "Garden armchair"),
            new("prop_old_deck_chair", "Chaise longue en bois", "Wooden deck chair"),
            new("prop_waiting_seat_01", "Siège de salle d'attente", "Waiting room seat"),
            new("prop_bar_stool_01", "Tabouret de bar", "Bar stool"),
            new("prop_stool_01", "Tabouret", "Stool"),
            new("prop_chair_pile_01", "Pile de chaises", "Stack of chairs"),
        ]),
        new("Tables", "Tables",
        [
            new("apa_mp_h_din_table_01", "Table de salle à manger", "Dining table"),
            new("prop_table_01", "Petite table", "Small table"),
            new("prop_table_03", "Table", "Table"),
            new("prop_table_08", "Grande table", "Large table"),
            new("prop_t_coffe_table", "Table basse", "Coffee table"),
            new("apa_mp_h_tab_sidelrg_01", "Table d'appoint", "Side table"),
            new("prop_rub_table_01", "Vieille table", "Old table"),
            new("prop_protest_table_01", "Table pliante", "Folding table"),
            new("prop_picnictable_01", "Table de pique-nique", "Picnic table"),
            new("prop_table_tennis", "Table de ping-pong", "Table tennis table"),
        ]),
        new("Canapés & fauteuils", "Sofas & armchairs",
        [
            new("prop_couch_01", "Canapé", "Sofa"),
            new("prop_couch_lg_02", "Grand canapé", "Large sofa"),
            new("prop_couch_sm_02", "Petit canapé", "Small sofa"),
            new("v_res_m_h_sofa", "Canapé moderne", "Modern sofa"),
            new("apa_mp_h_stn_sofacorn_01", "Canapé d'angle", "Corner sofa"),
            new("prop_rub_couch01", "Vieux canapé", "Old sofa"),
            new("prop_armchair_01", "Fauteuil", "Armchair"),
            new("apa_mp_h_stn_chairarm_01", "Fauteuil design", "Designer armchair"),
            new("apa_mp_h_stn_foot_stool_01", "Pouf", "Footstool"),
        ]),
        new("Chambre & rangements", "Bedroom & storage",
        [
            new("v_res_d_bed", "Lit double", "Double bed"),
            new("apa_mp_h_bed_double_08", "Lit double moderne", "Modern double bed"),
            new("apa_mp_h_yacht_bed_01", "Grand lit", "Large bed"),
            new("v_res_tre_bed1", "Vieux lit", "Old bed"),
            new("v_med_bed1", "Lit d'hôpital", "Hospital bed"),
            new("v_res_tre_wardrobe", "Armoire", "Wardrobe"),
            new("apa_mp_h_bed_chestdrawer_02", "Commode", "Chest of drawers"),
            new("apa_mp_h_04_sideboard", "Buffet", "Sideboard"),
            new("prop_shelves_01", "Étagère", "Shelves"),
            new("v_ind_cf_shelf", "Étagère métallique", "Metal shelving"),
        ]),
        new("Bureau & informatique", "Office & computers",
        [
            new("prop_office_desk_01", "Bureau", "Office desk"),
            new("v_res_son_desk", "Bureau de chambre", "Bedroom desk"),
            new("v_corp_offchair", "Fauteuil de bureau", "Office chair"),
            new("ex_prop_offchair_exec_01", "Fauteuil de direction", "Executive chair"),
            new("prop_cs_office_chair", "Chaise de bureau", "Desk chair"),
            new("prop_monitor_01a", "Écran d'ordinateur", "Computer monitor"),
            new("prop_pc_01a", "Tour d'ordinateur", "PC tower"),
            new("prop_keyboard_01a", "Clavier", "Keyboard"),
            new("prop_laptop_01a", "Ordinateur portable", "Laptop"),
            new("prop_printer_01", "Imprimante", "Printer"),
        ]),
        new("Lampes", "Lamps",
        [
            new("apa_mp_h_floorlamp_a", "Lampadaire d'intérieur", "Floor lamp"),
            new("apa_mp_h_lit_lamptable_02", "Lampe de table", "Table lamp"),
            new("apa_mp_h_lit_lightpendant_01", "Suspension", "Pendant light"),
            new("apa_mp_h_ceiling_light_01", "Plafonnier", "Ceiling light"),
            new("prop_wall_light_01a", "Applique murale", "Wall light"),
            new("prop_ind_light_01a", "Lampe industrielle", "Industrial lamp"),
            new("prop_worklight_01a", "Projecteur de chantier", "Work light"),
            new("prop_studio_light_01", "Projecteur de studio", "Studio light"),
            new("apa_mp_h_acc_candles_01", "Bougies", "Candles"),
        ]),
        new("Cuisine & salle de bain", "Kitchen & bathroom",
        [
            new("prop_fridge_01", "Réfrigérateur", "Fridge"),
            new("prop_cooker_03", "Cuisinière", "Cooker"),
            new("prop_microwave_1", "Micro-ondes", "Microwave"),
            new("prop_coffee_mac_02", "Machine à café", "Coffee machine"),
            new("prop_toaster_01", "Grille-pain", "Toaster"),
            new("prop_kettle_01", "Bouilloire", "Kettle"),
            new("prop_sink_02", "Évier", "Sink"),
            new("prop_toilet_01", "Toilettes", "Toilet"),
            new("apa_mp_h_bathtub_01", "Baignoire", "Bathtub"),
            new("apa_mp_h_01_bath_mirror", "Miroir de salle de bain", "Bathroom mirror"),
            new("prop_towel_rail_01", "Porte-serviettes", "Towel rail"),
            new("prop_toilet_roll_01", "Papier toilette", "Toilet roll"),
        ]),
        new("TV & musique", "TV & music",
        [
            new("prop_tv_flat_01", "Télévision écran plat", "Flat-screen TV"),
            new("prop_tv_01", "Vieille télévision", "Old TV"),
            new("prop_console_01", "Console de jeu", "Game console"),
            new("prop_hifi_01", "Chaîne hi-fi", "Hi-fi system"),
            new("prop_speaker_01", "Enceinte", "Speaker"),
            new("prop_radio_01", "Radio", "Radio"),
            new("prop_dj_deck_01", "Platines DJ", "DJ decks"),
        ]),
        new("Plantes", "Plants",
        [
            new("prop_plant_int_01a", "Plante d'intérieur", "Indoor plant"),
            new("prop_pot_plant_01a", "Plante en pot", "Potted plant"),
            new("prop_fib_plant_01", "Plante de bureau", "Office plant"),
            new("prop_plant_palm_01a", "Palmier en pot", "Potted palm"),
            new("prop_cactus_01a", "Cactus", "Cactus"),
            new("prop_agave_01", "Agave", "Agave"),
            new("prop_plant_flower_01", "Fleurs", "Flowers"),
            new("prop_plant_fern_01a", "Fougère", "Fern"),
            new("prop_bush_neat_01", "Buisson taillé", "Trimmed bush"),
            new("prop_bush_lrg_01", "Grand buisson", "Large bush"),
        ]),
        new("Arbres & rochers", "Trees & rocks",
        [
            new("prop_tree_oak_01", "Chêne", "Oak"),
            new("prop_tree_pine_01", "Pin", "Pine"),
            new("prop_palm_med_01a", "Palmier", "Palm tree"),
            new("prop_tree_birch_01", "Bouleau", "Birch"),
            new("prop_tree_cypress_01", "Cyprès", "Cypress"),
            new("prop_tree_olive_01", "Olivier", "Olive tree"),
            new("prop_joshua_tree_01a", "Arbre de Josué", "Joshua tree"),
            new("prop_tree_stump_01", "Souche", "Tree stump"),
            new("prop_tree_log_01", "Tronc couché", "Log"),
            new("prop_rock_1_a", "Rocher", "Rock"),
            new("prop_rock_4_big", "Gros rocher", "Big rock"),
        ]),
        new("Jardin & terrasse", "Garden & patio",
        [
            new("prop_bbq_1", "Barbecue", "Barbecue"),
            new("prop_parasol_01", "Parasol", "Parasol"),
            new("prop_patio_lounger1", "Bain de soleil", "Sun lounger"),
            new("prop_patio_heater_01", "Chauffage de terrasse", "Patio heater"),
            new("prop_hottub2", "Jacuzzi", "Hot tub"),
            new("prop_fountain1", "Fontaine", "Fountain"),
            new("prop_birdbath1", "Bain d'oiseaux", "Birdbath"),
            new("prop_gnome1", "Nain de jardin", "Garden gnome"),
            new("prop_hose_1", "Tuyau d'arrosage", "Garden hose"),
        ]),
        new("Rue", "Street",
        [
            new("prop_bench_01a", "Banc public", "Park bench"),
            new("prop_wait_bench_01", "Banc d'attente", "Waiting bench"),
            new("prop_busstop_02", "Abribus", "Bus shelter"),
            new("prop_streetlight_01", "Lampadaire", "Street light"),
            new("prop_traffic_01a", "Feu tricolore", "Traffic light"),
            new("prop_fire_hydrant_1", "Bouche d'incendie", "Fire hydrant"),
            new("prop_postbox_01a", "Boîte aux lettres", "Mailbox"),
            new("prop_phonebox_01a", "Cabine téléphonique", "Phone booth"),
            new("prop_vend_soda_01", "Distributeur de boissons", "Soda machine"),
            new("prop_atm_01", "Distributeur de billets", "ATM"),
            new("prop_elecbox_01a", "Armoire électrique", "Electrical box"),
            new("prop_bollard_01a", "Potelet", "Bollard"),
            new("prop_flagpole_1a", "Mât de drapeau", "Flagpole"),
        ]),
        new("Poubelles & déchets", "Bins & rubbish",
        [
            new("prop_bin_01a", "Poubelle", "Bin"),
            new("prop_recyclebin_01a", "Poubelle de recyclage", "Recycling bin"),
            new("prop_dumpster_01a", "Benne à ordures", "Dumpster"),
            new("prop_skip_01a", "Benne de chantier", "Skip"),
            new("prop_rub_binbag_01", "Sacs poubelle", "Bin bags"),
            new("prop_rub_cardpile_01", "Tas de cartons", "Pile of cardboard"),
            new("prop_rub_matress_01", "Matelas abandonné", "Dumped mattress"),
        ]),
        new("Barrières & chantier", "Barriers & building site",
        [
            new("prop_roadcone01a", "Cône de signalisation", "Traffic cone"),
            new("prop_barrier_work01a", "Barrière de chantier", "Work barrier"),
            new("prop_mp_barrier_01", "Barrière", "Barrier"),
            new("prop_barier_conc_01a", "Séparateur en béton", "Concrete barrier"),
            new("prop_plas_barier_01a", "Barrière en plastique", "Plastic barrier"),
            new("prop_barrier_wat_01a", "Barrière lestée à l'eau", "Water-filled barrier"),
            new("prop_sec_barier_01a", "Barrière levante", "Boom barrier"),
            new("prop_portaloo_01a", "Toilettes de chantier", "Portable toilet"),
            new("prop_portacabin01", "Bungalow de chantier", "Site cabin"),
            new("prop_cementmixer_01a", "Bétonnière", "Cement mixer"),
            new("prop_conc_blocks01a", "Parpaings", "Concrete blocks"),
            new("prop_scafold_01a", "Échafaudage", "Scaffolding"),
        ]),
        new("Clôtures", "Fences",
        [
            new("prop_fnclink_01a", "Grillage", "Chain-link fence"),
            new("prop_fncwood_01a", "Palissade en bois", "Wooden fence"),
            new("prop_fncres_01a", "Clôture de jardin", "Garden fence"),
            new("prop_fncsec_01a", "Clôture de sécurité", "Security fence"),
            new("prop_fnc_farm_01a", "Clôture de ferme", "Farm fence"),
            new("prop_const_fence01a", "Clôture de chantier", "Construction fence"),
            new("prop_fence_log_01", "Clôture en rondins", "Log fence"),
        ]),
        new("Caisses & conteneurs", "Crates & containers",
        [
            new("prop_box_wood01a", "Caisse en bois", "Wooden crate"),
            new("prop_boxpile_01a", "Pile de cartons", "Stack of boxes"),
            new("prop_pallet_01a", "Palette", "Pallet"),
            new("prop_barrel_01a", "Fût", "Barrel"),
            new("prop_wooden_barrel", "Tonneau en bois", "Wooden barrel"),
            new("prop_container_01a", "Conteneur", "Shipping container"),
            new("prop_gas_tank_01a", "Bouteille de gaz", "Gas tank"),
            new("prop_jerrycan_01a", "Jerrican", "Jerry can"),
            new("prop_toolchest_01", "Servante d'atelier", "Tool chest"),
            new("prop_tool_box_01", "Boîte à outils", "Toolbox"),
        ]),
        new("Sport & loisirs", "Sport & leisure",
        [
            new("prop_weight_bench_02", "Banc de musculation", "Weight bench"),
            new("prop_exer_bike_01", "Vélo d'appartement", "Exercise bike"),
            new("prop_barbell_20kg", "Haltère", "Barbell"),
            new("prop_beach_punchbag", "Sac de frappe", "Punching bag"),
            new("prop_basketball_net", "Panier de basket", "Basketball hoop"),
            new("prop_tennis_net_01", "Filet de tennis", "Tennis net"),
            new("prop_pooltable_02", "Billard", "Pool table"),
            new("prop_skate_halfpipe", "Rampe de skate", "Skate half-pipe"),
            new("prop_skate_funbox", "Module de skate", "Skate funbox"),
            new("prop_yoga_mat_01", "Tapis de yoga", "Yoga mat"),
        ]),
    ];

    // The props of a category present in this game version, with their place in the archives.
    public static List<ViewerItem> Items(Category category, WorldLoader world)
    {
        var items = new List<ViewerItem>();
        if (world.Maps is not { } library || world.Models is not { } models)
            return items;
        foreach (var prop in category.Props)
        {
            if (library.GetArchetype(Joaat.Hash(prop.Model)) is not { IsShadowOrReflectionProxy: false } archetype
                || models.Locate(archetype) is not { } location)
                continue;
            items.Add(new ViewerItem(prop.Model, ViewerKind.Object, location) { Label = prop.Label });
        }
        return items;
    }
}
