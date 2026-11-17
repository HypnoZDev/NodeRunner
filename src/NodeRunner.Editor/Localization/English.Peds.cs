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

        // Animations
        ("{0}  Personnages", "{0}  Peds"),
        ("{0}  Liste des animations...", "{0}  Listing the animations..."),
        ("Rechercher (ex. smoking, sit, dance, cop)", "Search (e.g. smoking, sit, dance, cop)"),
        ("{0} fichiers d'animation", "{0} animation files"),
        ("{0}  Lecture du fichier...", "{0}  Reading the file..."),
        ("Aucune animation lisible dans ce fichier.", "No readable animation in this file."),
        ("{0} clips : un clic pour le jouer", "{0} clips: click one to play it"),
        ("Aucune : pose de base. Choisis une animation dans Scène > Animations.",
            "None: default pose. Pick an animation in Scene > Animations."),
        ("{0} s · {1} images", "{0} s · {1} frames"),
        ("{0}  Copier pour FiveM", "{0}  Copy for FiveM"),
        ("Copie ces lignes (Lua) pour un script FiveM :\n\n{0}", "Copies these lines (Lua) for a FiveM script:\n\n{0}"),
        ("Copié : {0} / {1}", "Copied: {0} / {1}"),
        ("Pose de base", "Default pose"),
        ("Arrête l'animation : le personnage revient dans sa pose de base", "Stops the animation: the ped goes back to its default pose"),
        ("Récentes", "Recent"),
    ];
}
