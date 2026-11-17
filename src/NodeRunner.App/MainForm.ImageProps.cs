using NodeRunner.App.World;
using NodeRunner.Editor;

namespace NodeRunner.App;

internal sealed partial class MainForm
{
    // Makes the plaque's files in a temporary folder, then adds them to the prop library as a pack of its name (which
    // reloads the map so that it can be placed).
    private void CreateImageProp(string image, ImageProp.Options options)
    {
        string folder = Path.Combine(Path.GetTempPath(), "NodeRunner", "props", options.Name);
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
            ImagePropMaker.Write(image, options, folder, CollisionMaterial("METAL_SOLID_MEDIUM"), MetaSchema());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(this, L($"Le prop n'a pas pu être créé :\n{ex.Message}"), L("Prop depuis une image"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        AddLibraryPack(folder);
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only a temporary folder left behind.
        }
    }

    // Index of a collision material in materials.dat, concrete when it is missing.
    private int CollisionMaterial(string name)
    {
        var materials = _state.World.Collisions?.Materials;
        int found = Enumerable.Range(0, 256).FirstOrDefault(i => materials?.Name((byte)i) == name, -1);
        return found >= 0 ? found : Enumerable.Range(0, 256).FirstOrDefault(i => materials?.Name((byte)i) == "CONCRETE", 1);
    }
}
