namespace NodeRunner.Editor.Localization;

// Ped viewer tab.
public static partial class English
{
    private static (string, string)[] Peds =>
    [
        ("{0}  Visionneuse de personnages", "{0}  Ped viewer"),
        ("Ouvrir la visionneuse de personnages", "Open the ped viewer"),
        ("La visionneuse de personnages est réservée à GTA V.", "The ped viewer is for GTA V only."),
        ("{0}  Personnage : {1}###tab_ped", "{0}  Ped: {1}###tab_ped"),
        ("La visionneuse de personnages : la liste est dans le panneau Scène, la tenue dans Propriétés",
            "The ped viewer: the list is in the Scene panel, the outfit in Properties"),
        ("Choisis un personnage dans le panneau Scène.", "Pick a ped in the Scene panel."),
        ("{0}  Impossible d'afficher ce personnage : {1}", "{0}  Cannot show this ped: {1}"),
        ("Clic gauche : tourner · clic droit ou molette enfoncée : déplacer · molette : zoom · double-clic ou F : vue par défaut",
            "Left click: turn · right click or wheel pressed: move · wheel: zoom · double-click or F: default view"),
        ("Le personnage avec ses textures", "The ped with its textures"),
        ("Sol quadrillé sous le personnage (carreaux de 1 m)", "Grid under the ped (1 m squares)"),
        ("tourner", "turn"),
        ("déplacer", "move"),
        ("vue par défaut", "default view"),

        // List
        ("Tous", "All"),
        ("Humains", "Humans"),
        ("La carte se charge...", "The map is loading..."),
        ("{0}  Liste des personnages...", "{0}  Listing the peds..."),
        ("Rechercher (ex. player_zero, a_c_husky, cop)", "Search (e.g. player_zero, a_c_husky, cop)"),
        ("{0} personnages", "{0} peds"),

        // Families, from the start of the name
        ("Animal", "Animal"),
        ("Passant", "Passer-by"),
        ("Métier (police, médecin, ouvrier...)", "Job (police, medic, worker...)"),
        ("Membre de gang", "Gang member"),
        ("Personnage unique", "Unique character"),
        ("Personnage de l'histoire", "Story character"),
        ("Personnage des cinématiques", "Cutscene character"),
        ("Équipe de braquage", "Heist crew"),
        ("Personnage Online", "Online character"),
        ("Personnage principal", "Main character"),
        ("Silhouette lointaine (détail réduit)", "Distant silhouette (low detail)"),
        ("Personnage", "Ped"),
        (" · homme", " · male"),
        (" · femme", " · female"),

        // Properties
        ("Le nom du modèle, pour un script FiveM (CreatePed, SetPlayerModel...)",
            "The model name, for a FiveM script (CreatePed, SetPlayerModel...)"),
        ("Copié : {0}", "Copied: {0}"),
        ("{0}  Tenue", "{0}  Outfit"),
        ("Tenue précédente", "Previous outfit"),
        ("Tenue suivante (tête, cheveux, haut et bas mélangés)", "Next outfit (heads, hair, tops and legs mixed)"),
        ("Tenue de base", "Base outfit"),
        ("Variante {0}", "Variant {0}"),
        ("Les variantes mélangent les têtes, cheveux, hauts et bas du personnage, comme le jeu pour les passants.",
            "Variants mix the ped's heads, hair, tops and legs, as the game does for passers-by."),
        ("Ce personnage a une seule tenue : ses pièces sont des fichiers séparés (personnages de l'histoire, certains animaux).",
            "This ped has a single outfit: its parts are separate files (story characters, some animals)."),
        ("{0} triangles · {1} morceaux · {2} textures", "{0} triangles · {1} parts · {2} textures"),
        ("Taille : {0} m de haut", "Size: {0} m tall"),
        ("{0} textures introuvables", "{0} textures not found"),
    ];
}
