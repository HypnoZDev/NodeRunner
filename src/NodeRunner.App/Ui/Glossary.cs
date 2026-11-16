namespace NodeRunner.App.Ui;

// One sentence for the technical words of the game's files, shown when a section title holding one is hovered.
internal static class Glossary
{
    private static readonly (string Word, string Meaning)[] Words =
    [
        ("YMAP", "Fichier de carte : la liste des objets posés dans une zone (position, rotation, modèle)."),
        ("YTYP", "Fichier de types : décrit les modèles (taille, distance d'affichage, textures) et les intérieurs."),
        ("MLO", "Intérieur du jeu : un bâtiment où l'on entre, fait de salles reliées par des portails."),
        ("Pathnodes", "Routes des PNJ : les points et les liens que suivent les voitures et les piétons du jeu."),
        ("Navmesh", "Sol où les piétons et les PNJ peuvent marcher : le jeu s'en sert pour leurs trajets."),
        ("Occlusion", "Boîtes cachées qui disent au jeu de ne pas dessiner ce qui est derrière (pour les FPS)."),
        ("Occluder", "Boîte cachée qui dit au jeu de ne pas dessiner ce qui est derrière (pour les FPS)."),
        ("Timecycle", "Réglages de lumière, de couleurs et de brouillard d'une zone (souvent un intérieur)."),
        ("LOD", "Version simplifiée d'un objet, affichée de loin à la place du modèle détaillé."),
        ("Collisions", "Les surfaces invisibles sur lesquelles on marche et contre lesquelles on bute (fichiers .ybn)."),
        ("Car generators", "Places où le jeu fait apparaître des véhicules garés."),
        ("Véhicules garés", "Places où le jeu fait apparaître des véhicules garés (car generators)."),
        ("Points de scénario", "Endroits où le jeu fait faire une action aux PNJ : s'asseoir, fumer, réparer une voiture..."),
        ("Salles et portails", "L'intérieur d'un MLO : ses salles, et les portails (portes, fenêtres) qui les relient."),
        ("Archétype", "Le type d'un objet : son modèle et ses réglages, partagés par toutes ses copies sur la carte."),
        ("Entity set", "Groupe d'objets d'un intérieur qu'un script peut afficher ou cacher (décor d'un appartement...)."),
    ];

    // The meaning of the first word of the glossary found in text, or null.
    public static string? Explain(string text)
    {
        foreach (var (word, meaning) in Words)
        {
            if (text.Contains(word, StringComparison.OrdinalIgnoreCase) || text.Contains(L(word), StringComparison.OrdinalIgnoreCase))
                return L($"{L(word)} : {L(meaning)}");
        }
        return null;
    }
}
