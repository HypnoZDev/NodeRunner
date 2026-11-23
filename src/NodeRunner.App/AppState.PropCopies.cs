using NodeRunner.App.World;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;

namespace NodeRunner.App;

internal sealed partial class AppState
{
    // A name for a new object type: valid, no type or .ytyp has it, and no model of that name in the stream folder.
    public bool IsFreeCopyName(string name) =>
        IsFreeInteriorName(name)
        && (SaveFolder is not { } stream || !new[] { ".ydr", ".yft", ".ytd" }.Any(e => File.Exists(Path.Combine(stream, name + e))));

    public string SuggestPropName(uint sourceHash) =>
        PropCopy.SuggestName(World.Names?.TryGet(sourceHash) ?? "prop", IsFreeCopyName);

    // Saves the workshop's model as a new prop (the original type keeps its model everywhere); replace: the map object
    // the workshop was opened from takes the new type, and the project is saved. Then the map is reloaded. Returns
    // whether it was saved.
    public bool SaveAsNewProp(ModelPartTool tool, uint sourceHash, string name, bool replace)
    {
        if (Online.IsActive)
        {
            Notify(L("Pas pendant une session Online : les fichiers du projet sont écrits et la carte rechargée."), warning: true);
            return false;
        }
        if (World.Maps?.GetArchetype(sourceHash) is not { } source)
        {
            Notify(L("Type d'objet du modèle introuvable."), warning: true);
            return false;
        }
        if (!IsFreeCopyName(name))
        {
            Notify(L($"Nom « {name} » invalide ou déjà pris (lettres minuscules, chiffres et _)."), warning: true);
            return false;
        }
        if (tool.SaveAsNew(this, source, name) is { } error)
        {
            Notify(error, warning: true);
            return false;
        }
        string old = World.Names?.Describe(sourceHash) ?? $"0x{sourceHash:X8}";
        string message = L($"Nouveau prop « {name} » enregistré dans le projet ({name}.ytyp). {old} reste inchangé partout sur la carte.");
        if (replace && WorkshopEntity is { } entity && ArchetypeEdit.CanChange(entity) && entity.ArchetypeHash == sourceHash)
        {
            History.Add(ArchetypeEdit.Apply([entity], Data.Hashing.Joaat.Hash(name), L($"Remplacer {old} par {name}")));
            var before = LastSave;
            SaveMaps();
            message += LastSave != before && LastSave is { Success: false } failed
                ? L($" L'objet ouvert n'a pas pu être remplacé : {failed.Text}")
                : L(" L'objet ouvert est remplacé par le nouveau prop (projet enregistré).");
        }
        World.RequestMapReload();
        Notify(message + L(" Rechargement de la carte..."));
        return true;
    }
}
