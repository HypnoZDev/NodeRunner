namespace NodeRunner.Editor.Localization;

// Prop library: viewer tab, File menu, settings, packs, export.
public static partial class English
{
    private static (string, string)[] Library =>
    [
        ("(racine de la bibliothèque)", "(root of the library)"),
        (".zip...##library_zip", ".zip...##library_zip"),
        ("Ajoute un pack livré en archive .zip (un .rar ou un .7z est à décompresser d'abord).", "Adds a pack delivered as a .zip archive (extract a .rar or .7z first)."),
        ("Ajouter un pack (.zip)...", "Add a pack (.zip)..."),
        ("Ajouter un pack (dossier)...", "Add a pack (folder)..."),
        ("Ajouter un pack de props (.zip) à la bibliothèque", "Add a prop pack (.zip) to the library"),
        ("Après avoir copié ou retiré un pack à la main dans le dossier : recharge la carte.", "After copying or removing a pack by hand in the folder: reloads the map."),
        ("Archives .zip (*.zip)|*.zip", ".zip archives (*.zip)|*.zip"),
        ("Bibliothèque", "Library"),
        ("Bibliothèque de props", "Prop library"),
        ("Bibliothèque vide. Ajoute un pack de props addons (dossier d'une ressource FiveM ou .zip avec ses .ytyp, .ydr et .ytd) : ses props seront disponibles dans tous les projets.",
            "Empty library. Add a pack of addon props (a FiveM resource folder or a .zip with its .ytyp, .ydr and .ytd): its props will be available in every project."),
        ("Choisis le pack de props à ajouter à la bibliothèque (dossier d'une ressource FiveM ou dossier stream).", "Choose the prop pack to add to the library (FiveM resource folder or stream folder)."),
        ("Copie un pack (dossier d'une ressource FiveM, dossier stream) dans la bibliothèque, puis recharge la carte.", "Copies a pack (FiveM resource folder, stream folder) into the library, then reloads the map."),
        ("Copie un pack de props addons (ressource FiveM, dossier stream) dans la bibliothèque :\nses props sont disponibles dans tous les projets (Visionneuse > Bibliothèque > Placer).",
            "Copies a pack of addon props (FiveM resource, stream folder) into the library:\nits props are available in every project (Viewer > Library > Place)."),
        ("Documents\\NodeRunner\\Bibliothèque", "Documents\\NodeRunner\\Bibliothèque"),
        ("Dossier de la bibliothèque de props (un sous-dossier par pack).", "Prop library folder (one subfolder per pack)."),
        ("Dossier de la bibliothèque inaccessible : {0}", "Library folder not accessible: {0}"),
        ("La bibliothèque a déjà un pack « {0} ». Le remplacer ?", "The library already has a pack \"{0}\". Replace it?"),
        ("Le pack n'a pas pu être ajouté :\n{0}", "The pack could not be added:\n{0}"),
        ("Ouvrir le dossier de la bibliothèque", "Open the library folder"),
        ("Ouvrir le dossier de la bibliothèque (après y avoir copié ou retiré un pack à la main : bouton Recharger)", "Open the library folder (after copying or removing a pack by hand: Reload button)"),
        ("Pack « {0} » ajouté à la bibliothèque, carte rechargée (Visionneuse > Bibliothèque).", "Pack \"{0}\" added to the library, map reloaded (Viewer > Library)."),
        ("Packs de props addons partagés par tous les projets (un sous-dossier par pack). Changer de dossier recharge la carte.", "Addon prop packs shared by every project (one subfolder per pack). Changing the folder reloads the map."),
        ("Prop ou pack", "Prop or pack"),
        ("Props addons de ta bibliothèque, utilisables dans tous les projets :\n{0}\nÀ l'export FiveM, seuls les fichiers des props placés sont copiés dans la ressource.",
            "Addon props of your library, usable in every project:\n{0}\nOn FiveM export, only the files of the placed props are copied into the resource."),
        ("Relire la bibliothèque", "Reload the library"),
        ("Relire la bibliothèque (recharge la carte)", "Reload the library (reloads the map)"),
        ("aucun fichier .ytyp, .ydr, .ydd, .yft, .ytd ou .ybn dans ce pack", "no .ytyp, .ydr, .ydd, .yft, .ytd or .ybn file in this pack"),
        ("ce dossier fait déjà partie de la bibliothèque", "this folder is already part of the library"),
        ("seuls les dossiers et les archives .zip sont pris en charge (décompresse d'abord un .rar ou un .7z)", "only folders and .zip archives are supported (extract a .rar or .7z first)"),
        ("{0}  Ajouter un pack...##library_add", "{0}  Add a pack...##library_add"),
        ("{0}  Bibliothèque de props", "{0}  Prop library"),
        ("{0} fichier(s) de la bibliothèque", "{0} library file(s)"),
        ("Pack : {0}\n{1}", "Pack: {0}\n{1}"),
    ];
}
