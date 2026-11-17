using System.Numerics;
using ImGuiNET;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Models;
using NodeRunner.Data.Native.Models;

namespace NodeRunner.App.Ui;

// The ped viewer's animations: the game's clip dictionaries (.ycd) to search, the clips of one, the clip played.
internal sealed partial class PedStudio
{
    private const int MaxRecent = 20;

    private sealed record AnimationFile(string Name, string Path);

    private GameContent? _animationsFor;
    private Task<List<AnimationFile>>? _files;
    private string _animationSearch = "";
    private string? _rowsFor;
    private List<AnimationFile> _rows = [];

    // The file whose clips are listed.
    private AnimationFile? _file;
    private Task<List<AnimationClip>>? _fileClips;

    // The clip played, its file and name.
    private AnimationClip? _clip;
    private string? _clipFile;
    private string? _clipName;
    private float _time;
    private Matrix4x4[]? _pose;

    private readonly List<(string File, string Clip)> _recent = [];
    private (string File, string Clip)? _pendingRecent; // A recent one asked for, its file being read.

    // The game's clip dictionaries, listed in the background (null meanwhile).
    private List<AnimationFile>? Files()
    {
        if (state.World.Content is not { } content)
            return null;
        if (!ReferenceEquals(content, _animationsFor))
        {
            _animationsFor = content;
            _files = Task.Run(() => content.EnumerateFiles(".ycd")
                .Select(p => new AnimationFile(Path.GetFileNameWithoutExtension(p), p))
                .DistinctBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList());
            _rowsFor = null;
            _file = null;
            _fileClips = null;
        }
        return _files is { IsCompletedSuccessfully: true } listed ? listed.Result : null;
    }

    private void OpenFile(AnimationFile file)
    {
        if (_file == file || state.World.Content is not { } content)
            return;
        _file = file;
        _fileClips = Task.Run(() => ReadClips(content, file.Path));
    }

    private static List<AnimationClip> ReadClips(GameContent content, string path)
    {
        try
        {
            if (content.ReadFile(path) is { } data && content.GetResourceHeader(path) is { } header)
                return ClipDictionaryReader.Read(data, header).Values.OrderBy(ClipName, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Listed as holding no clip.
        }
        return [];
    }

    // "pack:/amb@world_human_smoking@male@male_a@base/base.clip" -> "base".
    private static string ClipName(AnimationClip clip) =>
        clip.Name is { } full ? Path.GetFileNameWithoutExtension(full.Replace('\\', '/').Split('/')[^1]) : $"0x{clip.NameHash:X8}";

    private void Play(string file, AnimationClip clip)
    {
        _clip = clip;
        _clipFile = file;
        _clipName = ClipName(clip);
        _time = 0f;
        _playing = true;
        _recent.RemoveAll(r => r.File == file && r.Clip == _clipName);
        _recent.Insert(0, (file, _clipName));
        if (_recent.Count > MaxRecent)
            _recent.RemoveAt(_recent.Count - 1);
    }

    private void StopAnimation()
    {
        _clip = null;
        _clipFile = _clipName = null;
        _pendingRecent = null;
    }

    private void PlayRecent((string File, string Clip) recent)
    {
        if (Files()?.FirstOrDefault(f => string.Equals(f.Name, recent.File, StringComparison.OrdinalIgnoreCase)) is not { } file)
            return;
        OpenFile(file);
        _pendingRecent = recent;
    }

    // Once a frame while the tab is shown: the bones of the ped at the clip's time (the clip loops).
    private void Animate()
    {
        if (_pendingRecent is { } pending && _file?.Name == pending.File && _fileClips is { IsCompleted: true } read)
        {
            _pendingRecent = null;
            if (read.Result.FirstOrDefault(c => ClipName(c) == pending.Clip) is { } clip)
                Play(pending.File, clip);
        }
        if (_clip is not { } playing || Viewer.Model.Data?.Skeleton is not { } skeleton)
        {
            Viewer.BonePose = null;
            return;
        }
        Advance(playing);
        if (_pose is null || _pose.Length != skeleton.Count)
            _pose = new Matrix4x4[skeleton.Count];
        SkeletonAnimator.Pose(skeleton, playing, PoseTime(playing), _pose);
        Viewer.BonePose = _pose;
    }

    // ---- Scene panel, Animations tab ----

    private unsafe void DrawAnimations()
    {
        if (Files() is not { } files)
        {
            Widgets.Hint(state.World.Content is null ? L("La carte se charge...") : L($"{Icons.LoaderCircle}  Liste des animations..."));
            return;
        }
        Widgets.SearchField("##animation_search", L("Rechercher (ex. smoking, sit, dance, cop)"), ref _animationSearch);
        if (_rowsFor != _animationSearch)
        {
            // Every word typed must be in the name, in any order.
            var words = _animationSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            _rows = files.Where(f => words.All(w => f.Name.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
            _rowsFor = _animationSearch;
        }
        Widgets.Hint(L($"{_rows.Count} fichiers d'animation"));

        float height = ImGui.GetTextLineHeightWithSpacing();
        float listHeight = _file is null ? 0f : MathF.Floor(ImGui.GetContentRegionAvail().Y * 0.5f);
        if (ImGui.BeginChild("##animation_files", new Vector2(0, listHeight), ImGuiChildFlags.None))
        {
            var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
            clipper.Begin(_rows.Count, height);
            while (clipper.Step())
            {
                for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                {
                    var file = _rows[i];
                    if (ImGui.Selectable($"{Icons.Film}  {file.Name}##animation_{i}", file == _file))
                        OpenFile(file);
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
                        ImGui.SetTooltip(file.Path);
                }
            }
            clipper.End();
            clipper.Destroy();
        }
        ImGui.EndChild();

        if (_file is not { } open)
            return;
        ImGui.Separator();
        ImGui.TextUnformatted(open.Name);
        if (_fileClips is not { IsCompleted: true } read)
        {
            Widgets.Hint(L($"{Icons.LoaderCircle}  Lecture du fichier..."));
            return;
        }
        if (read.Result.Count == 0)
        {
            Widgets.Hint(L("Aucune animation lisible dans ce fichier."), wrap: true);
            return;
        }
        Widgets.Hint(L($"{read.Result.Count} clips : un clic pour le jouer"));
        if (ImGui.BeginChild("##animation_clips", Vector2.Zero, ImGuiChildFlags.None))
        {
            for (int i = 0; i < read.Result.Count; i++)
            {
                var clip = read.Result[i];
                if (ImGui.Selectable($"{Icons.Play}  {ClipName(clip)}##clip_{i}", clip == _clip))
                    Play(open.Name, clip);
                ImGui.SameLine();
                Widgets.Hint(L($"{clip.Duration:0.0} s"));
            }
        }
        ImGui.EndChild();
    }

    // ---- Properties panel: the animation played ----

    private void DrawAnimationProperties()
    {
        if (!Widgets.Section(L($"{Icons.Film}  Animation"), "ped_animation"))
            return;
        if (_clip is not { } clip)
            Widgets.Hint(L("Aucune : pose de base. Choisis une animation dans Scène > Animations."), wrap: true);
        else
        {
            ImGui.TextWrapped($"{_clipFile} / {_clipName}");
            int frames = clip.Parts.Count > 0 ? clip.Parts.Max(p => p.Animation.FrameCount) : 0;
            Widgets.Hint(L($"{clip.Duration:0.00} s · {frames} images"));
            string script = FiveMScript(_clipFile!, _clipName!);
            if (Widgets.Button(L($"{Icons.Copy}  Copier pour FiveM"), L($"Copie ces lignes (Lua) pour un script FiveM :\n\n{script}")))
            {
                ImGui.SetClipboardText(script);
                state.Notify(L($"Copié : {_clipFile} / {_clipName}"));
            }
            ImGui.SameLine();
            if (Widgets.Button(L("Pose de base"), L("Arrête l'animation : le personnage revient dans sa pose de base")))
                StopAnimation();
        }

        if (_recent.Count == 0)
            return;
        ImGui.Spacing();
        Widgets.Hint(L("Récentes"));
        for (int i = 0; i < _recent.Count; i++)
        {
            var recent = _recent[i];
            if (ImGui.Selectable($"{Icons.Play}  {recent.File} / {recent.Clip}##recent_{i}", recent.File == _clipFile && recent.Clip == _clipName))
                PlayRecent(recent);
        }
    }

    // Loads the dictionary, then plays the clip in a loop on the player's ped.
    private static string FiveMScript(string file, string clip) =>
        $"RequestAnimDict(\"{file}\")\n" +
        $"while not HasAnimDictLoaded(\"{file}\") do Wait(0) end\n" +
        $"TaskPlayAnim(PlayerPedId(), \"{file}\", \"{clip}\", 8.0, -8.0, -1, 1, 0, false, false, false)";
}
