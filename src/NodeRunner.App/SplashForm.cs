using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using NodeRunner.App.World;

namespace NodeRunner.App;

// Start-up screen, shown while the game data and the map load in the background. Everything is drawn here: the NRS
// monogram traced in light, a flash, the monogram flies to its place above the name, the name is revealed; meanwhile a
// network of nodes weaves itself on the right, replaced as soon as the path nodes are read by the real road network of
// the map lighting up from the city outwards, with lights driving along the roads. Loading bar following the real
// loading steps, credits.
internal sealed partial class SplashForm : Form
{
    private static readonly Color Accent = Color.FromArgb(0x4C, 0x8D, 0xFF);
    private static readonly Color AccentLight = Color.FromArgb(0x5F, 0xD3, 0xFF);
    private static readonly Color Background = Color.FromArgb(0x0A, 0x0E, 0x18);
    private const double Timeout = 180.0; // Seconds: never keep the user waiting on a stuck loading.
    private const double ModelsTimeout = 30.0; // Seconds for the 3D models: the rest loads in the main window.
    private const float MapShare = 0.85f; // Share of the bar for the map, the rest for the 3D models.
    private const float ModelsEnough = 0.90f; // The last few models arrive in the main window, a moment later.

    private static readonly (string Prefix, float From, float To)[] Steps =
    [
        (L("Lecture des pathnodes"), 0.12f, 0.25f),
        (L("Lecture des scénarios"), 0.25f, 0.32f),
        (L("Index des navmesh"), 0.32f, 0.36f),
        (L("Lecture des packs DLC"), 0.36f, 0.40f),
        (L("Lecture des types d'objets"), 0.40f, 0.62f),
        (L("Lecture des objets de la carte"), 0.62f, 0.86f),
        (L("Index des modèles 3D"), 0.86f, 0.90f),
        (L("Index des textures"), 0.90f, 0.94f),
        (L("Index des collisions"), 0.94f, 0.99f),
    ];

    // Drawn in points, then scaled to the display (Windows scale).
    private static readonly Size Design = new(800, 450);
    private const float Gutter = 48f;
    private static readonly RectangleF MapArea = new(420f, 6f, 350f, 438f);

    // Timeline of the intro, in seconds.
    private const float TraceFrom = 0.15f, TraceTo = 1.0f;
    private const float FlashAt = 1.0f;
    private const float FlyFrom = 1.3f, FlyTo = 1.85f;
    private const float TitleFrom = 1.55f, StudioFrom = 1.7f, WipeTime = 0.55f;
    private const float TextFrom = 1.9f;
    private const float RevealTime = 1.9f;

    private readonly WorldLoader _world;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly PrivateFontCollection _fonts = new();
    private readonly PrivateFontCollection _lightFonts = new();
    private readonly FontFamily _brand;
    private readonly FontStyle _brandStyle;
    private readonly GraphicsPath _wordmark;
    private readonly RectangleF _wordmarkBounds;
    private static readonly Color WordmarkInk = Color.FromArgb(0xF2, 0xEF, 0xE8);
    private readonly Font _taglineFont = new("Segoe UI", 12f, GraphicsUnit.Pixel);
    private readonly Font _statusFont = new("Segoe UI", 12f, GraphicsUnit.Pixel);
    private readonly Font _smallFont = new("Segoe UI", 10f, GraphicsUnit.Pixel);
    private readonly float _scale;
    private readonly Bitmap _background;

    private readonly GraphicsPath _monoNr, _monoS;
    private readonly List<(PointF[] Points, float[] Distances)> _monoTraces;
    private readonly RectangleF _monoBounds;
    private readonly PointF _monoBadge; // Centre of the monogram once at its place above the name.
    private readonly float _monoBadgeScale;
    private readonly GraphicsPath _titleNode, _titleStudio;
    private readonly RectangleF _titleNodeBounds, _titleStudioBounds;

    private readonly (PointF A, PointF B, float Delay)[] _links;
    private readonly (PointF P, float R, float Delay)[] _nodes;
    private readonly (float X, float Y, float Speed, float Size, float Phase)[] _particles;

    private Task<SplashRoadMap?>? _mapTask;
    private SplashRoadMap? _map;
    private float _mapStart;
    private bool _disposed;

    private float _shownProgress;
    private Func<float>? _preloadModels;
    private double? _modelsStart;

    public SplashForm(WorldLoader world, Func<float>? preloadModels)
    {
        _world = world;
        _preloadModels = preloadModels;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        _scale = DeviceDpi / 96f;
        ClientSize = new Size((int)(Design.Width * _scale), (int)(Design.Height * _scale));
        ShowInTaskbar = true;
        Text = Program.Name;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        DoubleBuffered = true;
        BackColor = Background;
        Opacity = 0;

        // One collection per file: the family names Windows gives them vary ("Unbounded Light Light").
        FontFamily? Load(PrivateFontCollection fonts, string file)
        {
            string font = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", file);
            if (File.Exists(font))
                fonts.AddFontFile(font);
            return fonts.Families.FirstOrDefault();
        }
        var bold = Load(_fonts, "unbounded-bold.ttf");
        var light = Load(_lightFonts, "unbounded-light.ttf");
        (_brand, _brandStyle) = bold is not null ? (bold, FontStyle.Regular) : (new FontFamily("Segoe UI"), FontStyle.Bold);
        _wordmark = Wordmark(light ?? new FontFamily("Segoe UI"), Gutter, Design.Height - 52f);
        _wordmarkBounds = _wordmark.GetBounds();

        // Monogram, centred on (0, 0): traced, then filled ("NR" white, "S" in the accent colour).
        _monoNr = TextPath("NR", 104f);
        var nrBounds = _monoNr.GetBounds();
        _monoS = TextPath("S", 104f);
        var sBounds = _monoS.GetBounds();
        using (var shift = new Matrix())
        {
            shift.Translate(nrBounds.Right + 6f - sBounds.X, nrBounds.Y - sBounds.Y);
            _monoS.Transform(shift);
        }
        using (var both = new GraphicsPath())
        {
            both.AddPath(_monoNr, false);
            both.AddPath(_monoS, false);
            var b = both.GetBounds();
            using var centre = new Matrix();
            centre.Translate(-b.X - b.Width / 2f, -b.Y - b.Height / 2f);
            _monoNr.Transform(centre);
            _monoS.Transform(centre);
            both.Transform(centre);
            _monoBounds = both.GetBounds();
            both.Flatten(null, 0.3f);
            _monoTraces = Traces(both);
        }
        _monoBadgeScale = 22f / _monoBounds.Height;
        _monoBadge = new PointF(Gutter + _monoBounds.Width * _monoBadgeScale / 2f, 96f + 11f);

        (_titleNode, _titleNodeBounds) = PlacedText("NodeRunner", 46f, Gutter, 134f);
        (_titleStudio, _titleStudioBounds) = PlacedText("Studio", 46f, Gutter, 186f);

        _background = DrawBackground(Design, _scale);
        (_links, _nodes) = Network();
        var random = new Random();
        _particles = Enumerable.Range(0, 46).Select(_ => ((float)random.NextDouble() * Design.Width, (float)random.NextDouble() * Design.Height,
            4f + (float)random.NextDouble() * 10f, 0.8f + (float)random.NextDouble() * 1.4f, (float)random.NextDouble() * 6.28f)).ToArray();

        _timer.Tick += (_, _) => Step();
        _timer.Start();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Rounded corners on Windows 11 (ignored by older versions).
        int round = 2;
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }

    private float Now => (float)_clock.Elapsed.TotalSeconds;

    private void Step()
    {
        double now = _clock.Elapsed.TotalSeconds;
        if (Opacity < 1.0)
            Opacity = Math.Min(1.0, EaseOut(Phase((float)now, 0f, 0.3f)));
        StartMap();

        bool mapDone = !_world.IsBusy && (_world.Maps is not null || _world.Error is not null);
        bool done = now > Timeout;
        float target;
        if (!mapDone && !done)
            target = Progress(_world.Status) * MapShare;
        else
        {
            // Then the 3D models around the camera, read and sent to the GPU by the main window, still hidden.
            _modelsStart ??= now;
            float models = done || _world.Maps is null ? 1f : PreloadModels();
            done |= models >= ModelsEnough || now - _modelsStart > ModelsTimeout;
            target = MapShare + (1f - MapShare) * models;
        }
        if (done)
        {
            if (now > Timeout && !mapDone)
                Diagnostics.ErrorLog.Add(L("Chargement"), L($"Chargement anormalement long (plus de {Timeout / 60:0} min), arrêté à l'étape : {_world.Status}"));
            // Closed at once (no wait for the bar to reach its end): the main window opens on the loaded view.
            _timer.Stop();
            Close();
            return;
        }
        // The bar glides towards the real progress, never backwards.
        _shownProgress = Math.Max(_shownProgress, _shownProgress + (target - _shownProgress) * 0.12f);
        Invalidate();
    }

    // The road network is drawn on a worker thread as soon as the path nodes are read; until then (or without them),
    // the drawn network stays.
    private void StartMap()
    {
        if (_mapTask is null && _world.PathNetwork is { } network)
        {
            float scale = _scale;
            _mapTask = Task.Run(() =>
            {
                try
                {
                    return SplashRoadMap.Build(network, MapArea, scale);
                }
                catch (Exception)
                {
                    return null;
                }
            });
        }
        if (_map is null && _mapTask is { IsCompletedSuccessfully: true, Result: { } map })
        {
            _map = map;
            _mapStart = Math.Max(Now, TitleFrom);
        }
    }

    private float PreloadModels()
    {
        if (_preloadModels is null)
            return 1f;
        try
        {
            return _preloadModels();
        }
        catch (Exception)
        {
            // GPU not ready, for instance: the main window opens anyway and reports the error itself.
            _preloadModels = null;
            return 1f;
        }
    }

    // Share of the loading done, from the status text of the loader ("Lecture des objets de la carte : 120 / 5080
    // fichiers").
    private static float Progress(string status)
    {
        foreach (var (prefix, from, to) in Steps)
        {
            if (!status.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            var match = CountPattern().Match(status);
            float share = match.Success && float.TryParse(match.Groups[2].Value, out float total) && total > 0
                ? Math.Clamp(float.Parse(match.Groups[1].Value) / total, 0f, 1f)
                : 0f;
            return from + (to - from) * share;
        }
        return 0.06f; // Opening the archives, reading the game's keys.
    }

    [GeneratedRegex(@"(\d+) / (\d+)")]
    private static partial Regex CountPattern();

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float t = Now;
        g.DrawImage(_background, new Rectangle(0, 0, _background.Width, _background.Height));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        float reveal = _map is null ? 0f : Phase(t, _mapStart, _mapStart + RevealTime);
        if (_map is not null)
            DrawMap(g, _map, t);
        g.ScaleTransform(_scale, _scale);
        DrawNetwork(g, t, 1f - Phase(t, _mapStart, _mapStart + 0.8f) * (_map is null ? 0f : 1f));
        if (_map is not null)
            DrawRunners(g, _map, t);
        DrawParticles(g, t);

        // Fade at the bottom, under the loading bar and the credits.
        using (var fade = new LinearGradientBrush(new RectangleF(0, Design.Height - 140f, Design.Width, 141f), Color.FromArgb(0, Background), Color.FromArgb(235, Background), 90f))
            g.FillRectangle(fade, 0, Design.Height - 140f, Design.Width, 140f);

        DrawMonogram(g, t);
        DrawTitle(g, t);
        DrawFlash(g, t);

        float text = Phase(t, TextFrom, TextFrom + 0.45f);
        if (text > 0f)
            DrawTexts(g, text, reveal);
    }

    private void DrawMap(Graphics g, SplashRoadMap map, float t)
    {
        var b = map.Bounds;
        var pixels = new Rectangle((int)(b.X * _scale), (int)(b.Y * _scale), map.Roads.Width, map.Roads.Height);
        float reveal = EaseInOut(Phase(t, _mapStart, _mapStart + RevealTime));
        float radius = map.Radius * reveal;
        var origin = new PointF(map.Origin.X * _scale, map.Origin.Y * _scale);
        if (reveal >= 1f)
        {
            g.DrawImage(map.Glow, pixels);
            g.DrawImage(map.Roads, pixels);
        }
        else if (radius > 0.5f)
        {
            using var inside = Circle(origin, radius * _scale);
            g.SetClip(inside);
            g.DrawImage(map.Glow, pixels);
            g.DrawImage(map.Roads, pixels);
            g.ResetClip();
        }

        // Bright front of the light while it spreads, then a soft wave from the city every few seconds.
        float front = reveal < 1f ? radius : map.Radius * EaseOut(((t - _mapStart - RevealTime) % 4.5f) / 2.6f);
        bool wave = reveal >= 1f && (t - _mapStart - RevealTime) % 4.5f < 2.6f;
        if ((reveal > 0f && reveal < 1f) || wave)
        {
            float width = (reveal < 1f ? 22f : 12f) * _scale;
            using var ring = new GraphicsPath(FillMode.Alternate);
            float outer = front * _scale, inner = Math.Max(0f, outer - width);
            ring.AddEllipse(origin.X - outer, origin.Y - outer, outer * 2f, outer * 2f);
            if (inner > 0f)
                ring.AddEllipse(origin.X - inner, origin.Y - inner, inner * 2f, inner * 2f);
            g.SetClip(ring);
            g.DrawImage(map.Glow, pixels);
            if (reveal < 1f)
                g.DrawImage(map.Glow, pixels);
            g.DrawImage(map.Roads, pixels);
            g.ResetClip();
            int alpha = reveal < 1f ? 60 : (int)(40 * (1f - front / map.Radius));
            using var pen = new Pen(Color.FromArgb(Math.Clamp(alpha, 0, 255), AccentLight), 1f * _scale);
            g.DrawEllipse(pen, origin.X - outer, origin.Y - outer, outer * 2f, outer * 2f);
        }

        // Viewfinder corners around the map.
        int corners = (int)(110 * Phase(t, _mapStart + 0.3f, _mapStart + 1.2f));
        if (corners > 0)
        {
            using var pen = new Pen(Color.FromArgb(corners, AccentLight), 1.2f * _scale);
            var r = new RectangleF(pixels.X - 8f * _scale, pixels.Y - 2f * _scale, pixels.Width + 16f * _scale, pixels.Height + 4f * _scale);
            r.Intersect(new RectangleF(4f * _scale, 4f * _scale, ClientSize.Width - 8f * _scale, ClientSize.Height - 8f * _scale));
            float c = 14f * _scale;
            g.DrawLines(pen, [new PointF(r.Left, r.Top + c), new PointF(r.Left, r.Top), new PointF(r.Left + c, r.Top)]);
            g.DrawLines(pen, [new PointF(r.Right - c, r.Top), new PointF(r.Right, r.Top), new PointF(r.Right, r.Top + c)]);
            g.DrawLines(pen, [new PointF(r.Left, r.Bottom - c), new PointF(r.Left, r.Bottom), new PointF(r.Left + c, r.Bottom)]);
            g.DrawLines(pen, [new PointF(r.Right - c, r.Bottom), new PointF(r.Right, r.Bottom), new PointF(r.Right, r.Bottom - c)]);
        }
    }

    private void DrawRunners(Graphics g, SplashRoadMap map, float t)
    {
        float visible = Phase(t, _mapStart + 0.9f, _mapStart + 1.7f);
        if (visible <= 0f)
            return;
        foreach (var runner in map.Runners)
        {
            const float Pause = 40f;
            float d = ((t - _mapStart) * runner.Speed + runner.Offset) % (runner.Length + Pause);
            if (d > runner.Length)
                continue;
            // Fades in and out at both ends of the drive.
            float ends = Math.Min(1f, Math.Min(d, runner.Length - d) / 20f) * visible;
            for (int i = 11; i >= 0; i--)
            {
                var p = runner.At(d - i * 2.4f);
                float size = 3.6f - i * 0.22f;
                int alpha = (int)((230 - i * 18) * ends);
                using var brush = new SolidBrush(Color.FromArgb(Math.Clamp(alpha, 0, 255), i == 0 ? Color.White : AccentLight));
                g.FillEllipse(brush, p.X - size / 2f, p.Y - size / 2f, size, size);
            }
            var head = runner.At(d);
            using var glow = new SolidBrush(Color.FromArgb((int)(55 * ends), AccentLight));
            g.FillEllipse(glow, head.X - 8f, head.Y - 8f, 16f, 16f);
        }
    }

    // Network of nodes weaving itself from left to right during the intro, faded out when the road map comes.
    private void DrawNetwork(Graphics g, float t, float visible)
    {
        if (visible <= 0f)
            return;
        foreach (var (a, b, delay) in _links)
        {
            float grow = EaseOut(Phase(t, delay, delay + 0.5f));
            if (grow <= 0f)
                continue;
            int alpha = (int)(Fade(a.X) * 75f * visible);
            using var pen = new Pen(Color.FromArgb(Math.Clamp(alpha, 0, 255), Accent), 1f);
            g.DrawLine(pen, a, new PointF(a.X + (b.X - a.X) * grow, a.Y + (b.Y - a.Y) * grow));
        }
        foreach (var (p, r, delay) in _nodes)
        {
            float pop = Phase(t, delay, delay + 0.25f);
            if (pop <= 0f)
                continue;
            float size = r * (pop < 1f ? 1f + (1f - pop) * 1.5f : 1f);
            int alpha = (int)(Fade(p.X) * 200f * visible * pop);
            using var brush = new SolidBrush(Color.FromArgb(Math.Clamp(alpha, 0, 255), 0x9C, 0xC4, 0xFF));
            g.FillEllipse(brush, p.X - size, p.Y - size, size * 2f, size * 2f);
        }
    }

    private void DrawParticles(Graphics g, float t)
    {
        float visible = Phase(t, 0.4f, 1.4f);
        if (visible <= 0f)
            return;
        foreach (var (x, y, speed, size, phase) in _particles)
        {
            float py = ((y - t * speed) % Design.Height + Design.Height) % Design.Height;
            float px = x + MathF.Sin(t * 0.6f + phase) * 6f;
            int alpha = (int)((40f + 50f * (0.5f + 0.5f * MathF.Sin(t * 1.7f + phase))) * visible);
            using var brush = new SolidBrush(Color.FromArgb(alpha, AccentLight));
            g.FillEllipse(brush, px - size / 2f, py - size / 2f, size, size);
        }
    }

    private void DrawMonogram(Graphics g, float t)
    {
        if (t < TraceFrom)
            return;
        float fly = EaseInOut(Phase(t, FlyFrom, FlyTo));
        var centre = new PointF(Design.Width / 2f + (_monoBadge.X - Design.Width / 2f) * fly, Design.Height / 2f - 8f + (_monoBadge.Y - Design.Height / 2f + 8f) * fly);
        float scale = 1f + (_monoBadgeScale - 1f) * fly;
        var saved = g.Save();
        g.TranslateTransform(centre.X, centre.Y);
        g.ScaleTransform(scale, scale);

        // Outline traced in light, all strokes at once, a bright point at the head of each.
        float outline = 1f - Phase(t, FlashAt + 0.1f, FlashAt + 0.5f);
        if (outline > 0f)
        {
            float traced = EaseInOut(Phase(t, TraceFrom, TraceTo));
            using var glowPen = new Pen(Color.FromArgb((int)(50 * outline), AccentLight), 7f) { LineJoin = LineJoin.Round };
            using var pen = new Pen(Color.FromArgb((int)(255 * outline), AccentLight), 1.6f) { LineJoin = LineJoin.Round };
            using var head = new SolidBrush(Color.FromArgb((int)(255 * outline), Color.White));
            foreach (var (points, distances) in _monoTraces)
            {
                var part = Partial(points, distances, distances[^1] * traced);
                if (part.Length < 2)
                    continue;
                g.DrawLines(glowPen, part);
                g.DrawLines(pen, part);
                if (traced < 1f)
                    g.FillEllipse(head, part[^1].X - 2.5f, part[^1].Y - 2.5f, 5f, 5f);
            }
        }

        float fill = Phase(t, FlashAt - 0.05f, FlashAt + 0.2f);
        if (fill > 0f)
        {
            using var white = new SolidBrush(Color.FromArgb((int)(255 * fill), Color.White));
            g.FillPath(white, _monoNr);
            var sb = _monoS.GetBounds();
            using var accent = new LinearGradientBrush(new RectangleF(sb.X - 1f, sb.Y, sb.Width + 2f, sb.Height), Color.FromArgb((int)(255 * fill), Accent), Color.FromArgb((int)(255 * fill), AccentLight), 0f);
            g.FillPath(accent, _monoS);
        }
        g.Restore(saved);
    }

    private void DrawFlash(Graphics g, float t)
    {
        float flash = Phase(t, FlashAt, FlashAt + 0.7f);
        if (flash <= 0f || flash >= 1f)
            return;
        var centre = new PointF(Design.Width / 2f, Design.Height / 2f - 8f);
        float fade = 1f - flash;
        float glow = 40f + 320f * EaseOut(flash);
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(centre.X - glow, centre.Y - glow * 0.75f, glow * 2f, glow * 1.5f);
            using var brush = new PathGradientBrush(path) { CenterColor = Color.FromArgb((int)(150 * fade * fade), AccentLight), SurroundColors = [Color.Transparent] };
            g.FillPath(brush, path);
        }
        float ring = 30f + 460f * EaseOut(flash);
        using (var pen = new Pen(Color.FromArgb((int)(170 * fade), AccentLight), 2f))
            g.DrawEllipse(pen, centre.X - ring, centre.Y - ring, ring * 2f, ring * 2f);
        float white = 1f - Phase(t, FlashAt, FlashAt + 0.18f);
        if (white > 0f)
        {
            using var brush = new SolidBrush(Color.FromArgb((int)(70 * white), Color.White));
            g.FillRectangle(brush, 0, 0, Design.Width, Design.Height);
        }
    }

    // Name revealed by a light wiping from left to right, then a shine passing over it every few seconds.
    private void DrawTitle(Graphics g, float t)
    {
        DrawWiped(g, t, TitleFrom, _titleNode, _titleNodeBounds, null);
        DrawWiped(g, t, StudioFrom, _titleStudio, _titleStudioBounds, _titleStudioBounds);

        float shine = (t - TitleFrom - 1.1f) % 6f / 1.0f;
        if (t > TitleFrom + 1.1f && shine < 1f)
        {
            var all = RectangleF.Union(_titleNodeBounds, _titleStudioBounds);
            float x = all.X - 80f + (all.Width + 160f) * EaseInOut(shine);
            using var clip = new GraphicsPath(FillMode.Winding);
            clip.AddPath(_titleNode, false);
            clip.AddPath(_titleStudio, false);
            g.SetClip(clip);
            using var band = new LinearGradientBrush(new RectangleF(x - 40f, 0, 80f, 1f), Color.Transparent, Color.Transparent, 0f)
            {
                InterpolationColors = new ColorBlend
                {
                    Colors = [Color.FromArgb(0, Color.White), Color.FromArgb(170, Color.White), Color.FromArgb(0, Color.White)],
                    Positions = [0f, 0.5f, 1f],
                },
            };
            g.FillRectangle(band, x - 40f, all.Y - 4f, 80f, all.Height + 8f);
            g.ResetClip();
        }
    }

    private static void DrawWiped(Graphics g, float t, float from, GraphicsPath path, RectangleF bounds, RectangleF? gradient)
    {
        float wipe = EaseOut(Phase(t, from, from + WipeTime));
        if (wipe <= 0f)
            return;
        float edge = bounds.X - 6f + (bounds.Width + 12f) * wipe;
        var saved = g.Save();
        g.SetClip(new RectangleF(bounds.X - 6f, bounds.Y - 20f, edge - bounds.X + 6f, bounds.Height + 40f));
        g.TranslateTransform(0f, (1f - wipe) * 10f);
        if (gradient is { } r)
        {
            using var brush = new LinearGradientBrush(new RectangleF(r.X - 1f, r.Y, r.Width + 2f, r.Height), Accent, AccentLight, 0f);
            g.FillPath(brush, path);
        }
        else
            g.FillPath(Brushes.White, path);
        g.Restore(saved);
        if (wipe < 1f)
        {
            using var pen = new Pen(Color.FromArgb((int)(255 * (1f - wipe)), AccentLight), 2f);
            g.DrawLine(pen, edge, bounds.Y - 6f, edge, bounds.Bottom + 6f);
        }
    }

    private void DrawTexts(Graphics g, float visible, float reveal)
    {
        int Alpha(int a) => (int)(a * visible);
        using (var tagline = new SolidBrush(Color.FromArgb(Alpha(0xFF), 0xA8, 0xB2, 0xC8)))
            g.DrawString(L("Le studio 3D pour explorer, comprendre et modifier la carte de GTA V."), _taglineFont, tagline, Gutter, 240f);
        using (var version = new SolidBrush(Color.FromArgb(Alpha(0xFF), 0x70, 0x7A, 0x90)))
            g.DrawString($"v{Program.Version}", _smallFont, version, Design.Width - 66f, 20f);

        // Loading bar: current step and share on top, a light gliding along the filled part.
        float barTop = Design.Height - 74f, barWidth = Design.Width - Gutter * 2f;
        using (var status = new SolidBrush(Color.FromArgb(Alpha(0xFF), 0x8A, 0x94, 0xAA)))
        {
            g.DrawString(StatusText(), _statusFont, status, Gutter, barTop - 22f);
            string share = $"{Math.Clamp(_shownProgress, 0f, 1f) * 100f:0} %";
            g.DrawString(share, _statusFont, status, Gutter + barWidth - g.MeasureString(share, _statusFont).Width + 4f, barTop - 22f);
        }
        using (var track = new SolidBrush(Color.FromArgb(Alpha(0xFF), 0x1E, 0x26, 0x38)))
            FillRounded(g, track, new RectangleF(Gutter, barTop, barWidth, 4f));
        float filled = Math.Max(4f, barWidth * Math.Clamp(_shownProgress, 0f, 1f));
        var fillRect = new RectangleF(Gutter, barTop, filled, 4f);
        using (var fill = new LinearGradientBrush(new RectangleF(Gutter, barTop, barWidth, 4f), Color.FromArgb(Alpha(0xFF), Accent), Color.FromArgb(Alpha(0xFF), AccentLight), 0f))
            FillRounded(g, fill, fillRect);
        float shine = Gutter + (Now * 240f) % (barWidth + 120f) - 60f;
        if (shine < Gutter + filled)
        {
            g.SetClip(fillRect);
            using var band = new LinearGradientBrush(new RectangleF(shine - 30f, barTop, 60f, 4f), Color.Transparent, Color.Transparent, 0f)
            {
                InterpolationColors = new ColorBlend
                {
                    Colors = [Color.FromArgb(0, Color.White), Color.FromArgb(Alpha(200), Color.White), Color.FromArgb(0, Color.White)],
                    Positions = [0f, 0.5f, 1f],
                },
            };
            g.FillRectangle(band, shine - 30f, barTop, 60f, 4f);
            g.ResetClip();
        }
        using (var head = new SolidBrush(Color.FromArgb(Alpha(70), AccentLight)))
            g.FillEllipse(head, Gutter + filled - 7f, barTop - 5f, 14f, 14f);

        // Author's wordmark (a shine passes over it after the name's), with the notice that always goes with it.
        float creditsTop = Design.Height - 50f;
        using (var ink = new SolidBrush(Color.FromArgb(Alpha(0xFF), WordmarkInk)))
            g.FillPath(ink, _wordmark);
        float sweep = (Now - TitleFrom - 1.6f) % 6f / 0.7f;
        if (Now > TitleFrom + 1.6f && sweep < 1f)
        {
            var w = _wordmarkBounds;
            float x = w.X - 30f + (w.Width + 60f) * EaseInOut(sweep);
            g.SetClip(_wordmark);
            using var band = new LinearGradientBrush(new RectangleF(x - 20f, 0, 40f, 1f), Color.Transparent, Color.Transparent, 0f)
            {
                InterpolationColors = new ColorBlend
                {
                    Colors = [Color.FromArgb(0, AccentLight), Color.FromArgb(Alpha(255), AccentLight), Color.FromArgb(0, AccentLight)],
                    Positions = [0f, 0.5f, 1f],
                },
            };
            g.FillRectangle(band, x - 20f, w.Y - 2f, 40f, w.Height + 4f);
            g.ResetClip();
        }
        using (var notice = new SolidBrush(Color.FromArgb(Alpha(0xFF), 0x6A, 0x74, 0x8A)))
            g.DrawString(L("Non affilié à Rockstar Games ni à Take-Two Interactive"), _smallFont, notice, Gutter, creditsTop + 18f);

        // Size of the road network, counted up while it lights.
        if (_map is { } map && reveal > 0f)
        {
            // Plain no-break space between thousands: the narrow one of fr-FR is missing from Segoe UI at this size.
            var french = (CultureInfo)CultureInfo.GetCultureInfo("fr-FR").Clone();
            french.NumberFormat.NumberGroupSeparator = " ";
            float count = EaseOut(reveal);
            string figures = string.Format(NodeRunner.Editor.Localization.Loc.IsFrench ? french : CultureInfo.InvariantCulture, L("{0:N0} nœuds  ·  {1:N0} km de routes"), (int)(map.NodeCount * count), map.Kilometres * count);
            using var brush = new SolidBrush(Color.FromArgb(Alpha((int)(255 * Math.Min(1f, reveal * 3f))), 0x8A, 0x94, 0xAA));
            float width = g.MeasureString(figures, _smallFont).Width;
            g.DrawString(figures, _smallFont, brush, Design.Width - Gutter - width + 4f, creditsTop + 18f);
        }
    }

    private string StatusText()
    {
        if (_modelsStart is not null)
            return L("Chargement des modèles 3D...");
        string status = _world.Status;
        int colon = status.IndexOf(" :", StringComparison.Ordinal);
        string step = colon > 0 ? status[..colon] : status.TrimEnd('.');
        return $"{step}...";
    }

    private GraphicsPath TextPath(string text, float size)
    {
        // Winding: the brand font's letters are made of overlapping shapes, which the default fill would cut out.
        var path = new GraphicsPath(FillMode.Winding);
        path.AddString(text, _brand, (int)_brandStyle, size, PointF.Empty, StringFormat.GenericTypographic);
        return path;
    }

    // "inside" light then "rockstar" bold on the same baseline, a third of the size apart, ink placed at (left, top).
    private GraphicsPath Wordmark(FontFamily light, float left, float top)
    {
        const float Size = 17f;
        var path = new GraphicsPath(FillMode.Winding);
        path.AddString("inside", light, (int)FontStyle.Regular, Size, PointF.Empty, StringFormat.GenericTypographic);
        float right = path.GetBounds().Right;
        using (var second = TextPath("rockstar", Size))
        {
            using var move = new Matrix();
            move.Translate(right + Size / 3f - second.GetBounds().X, 0f);
            second.Transform(move);
            path.AddPath(second, false);
        }
        var bounds = path.GetBounds();
        using var place = new Matrix();
        place.Translate(left - bounds.X, top - bounds.Y);
        path.Transform(place);
        return path;
    }

    // Text as a shape, its ink placed at (left, top).
    private (GraphicsPath, RectangleF) PlacedText(string text, float size, float left, float top)
    {
        var path = TextPath(text, size);
        var bounds = path.GetBounds();
        using var move = new Matrix();
        move.Translate(left - bounds.X, top - bounds.Y);
        path.Transform(move);
        return (path, path.GetBounds());
    }

    // Each closed figure of a flattened path as a line, with the distance along it at each point.
    private static List<(PointF[], float[])> Traces(GraphicsPath flat)
    {
        var result = new List<(PointF[], float[])>();
        var points = flat.PathPoints;
        var types = flat.PathTypes;
        var figure = new List<PointF>();
        void Close()
        {
            if (figure.Count < 2)
                return;
            figure.Add(figure[0]);
            var distances = new float[figure.Count];
            for (int i = 1; i < figure.Count; i++)
                distances[i] = distances[i - 1] + MathF.Sqrt((figure[i].X - figure[i - 1].X) * (figure[i].X - figure[i - 1].X) + (figure[i].Y - figure[i - 1].Y) * (figure[i].Y - figure[i - 1].Y));
            result.Add((figure.ToArray(), distances));
        }
        for (int i = 0; i < points.Length; i++)
        {
            if ((types[i] & 0x07) == 0)
            {
                Close();
                figure = [];
            }
            figure.Add(points[i]);
        }
        Close();
        return result;
    }

    private static PointF[] Partial(PointF[] points, float[] distances, float length)
    {
        var part = new List<PointF> { points[0] };
        for (int i = 1; i < points.Length; i++)
        {
            if (distances[i] <= length)
            {
                part.Add(points[i]);
                continue;
            }
            float span = distances[i] - distances[i - 1];
            float f = span > 0f ? (length - distances[i - 1]) / span : 0f;
            part.Add(new PointF(points[i - 1].X + (points[i].X - points[i - 1].X) * f, points[i - 1].Y + (points[i].Y - points[i - 1].Y) * f));
            break;
        }
        return part.ToArray();
    }

    // Nodes scattered towards the right, each linked to its nearest neighbours; links and nodes appear from left to
    // right during the intro.
    private static ((PointF, PointF, float)[], (PointF, float, float)[]) Network()
    {
        var random = new Random(7);
        var nodes = new List<PointF>();
        for (int i = 0; i < 80; i++)
        {
            float x = Design.Width * (0.42f + 0.62f * MathF.Pow((float)random.NextDouble(), 0.7f));
            float y = Design.Height * (-0.05f + 1.1f * (float)random.NextDouble());
            nodes.Add(new PointF(x, y));
        }
        static float Distance(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        static float Delay(PointF p, Random random) => 0.45f + (p.X / Design.Width - 0.4f) * 1.6f + (float)random.NextDouble() * 0.2f;
        var links = new List<(PointF, PointF, float)>();
        var seen = new HashSet<(int, int)>();
        for (int a = 0; a < nodes.Count; a++)
        {
            foreach (int b in Enumerable.Range(0, nodes.Count).Where(b => b != a).OrderBy(b => Distance(nodes[a], nodes[b])).Take(3))
            {
                if (seen.Add((Math.Min(a, b), Math.Max(a, b))))
                {
                    var (from, to) = nodes[a].X <= nodes[b].X ? (nodes[a], nodes[b]) : (nodes[b], nodes[a]);
                    links.Add((from, to, Delay(from, random)));
                }
            }
        }
        var dots = nodes.Select(n => (n, 1.4f + (float)random.NextDouble() * 1.6f, Delay(n, random) + 0.1f)).ToArray();
        return (links.ToArray(), dots);
    }

    private static float Fade(float x) => Math.Clamp((x / Design.Width - 0.42f) / 0.25f, 0f, 1f);

    // Still part of the background, drawn once: gradient, glows and a faint grid of dots.
    private static Bitmap DrawBackground(Size size, float scale)
    {
        var bitmap = new Bitmap((int)(size.Width * scale), (int)(size.Height * scale), PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bitmap);
        g.ScaleTransform(scale, scale);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(Point.Empty, size);
        using (var background = new LinearGradientBrush(bounds, Background, Color.FromArgb(0x14, 0x1E, 0x3A), 35f))
            g.FillRectangle(background, bounds);
        Glow(g, new RectangleF(size.Width * 0.45f, -size.Height * 0.55f, size.Width * 0.9f, size.Height * 1.3f), Color.FromArgb(110, 0x2A, 0x5B, 0xD7));
        Glow(g, new RectangleF(size.Width * 0.62f, size.Height * 0.35f, size.Width * 0.6f, size.Height * 0.9f), Color.FromArgb(60, 0x6A, 0x3B, 0xC9));
        Glow(g, new RectangleF(-size.Width * 0.2f, size.Height * 0.1f, size.Width * 0.7f, size.Height * 0.8f), Color.FromArgb(40, 0x2A, 0x5B, 0xD7));
        using var dot = new SolidBrush(Color.FromArgb(16, 0x9C, 0xC4, 0xFF));
        for (float y = 12f; y < size.Height; y += 24f)
            for (float x = 12f; x < size.Width; x += 24f)
                g.FillRectangle(dot, x, y, 1f, 1f);
        return bitmap;
    }

    private static void Glow(Graphics g, RectangleF area, Color color)
    {
        using var path = new GraphicsPath();
        path.AddEllipse(area);
        using var brush = new PathGradientBrush(path) { CenterColor = color, SurroundColors = [Color.FromArgb(0, color)] };
        g.FillPath(brush, path);
    }

    private static GraphicsPath Circle(PointF centre, float radius)
    {
        var path = new GraphicsPath();
        path.AddEllipse(centre.X - radius, centre.Y - radius, radius * 2f, radius * 2f);
        return path;
    }

    private static void FillRounded(Graphics g, Brush brush, RectangleF r)
    {
        using var path = new GraphicsPath();
        float d = r.Height;
        path.AddArc(r.X, r.Y, d, d, 90, 180);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    private static float Phase(float t, float from, float to) => Math.Clamp((t - from) / (to - from), 0f, 1f);
    private static float EaseOut(float x) => 1f - (1f - x) * (1f - x) * (1f - x);
    private static float EaseInOut(float x) => x < 0.5f ? 4f * x * x * x : 1f - MathF.Pow(-2f * x + 2f, 3f) / 2f;

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _timer.Dispose();
            _background.Dispose();
            _monoNr.Dispose();
            _monoS.Dispose();
            _titleNode.Dispose();
            _titleStudio.Dispose();
            _taglineFont.Dispose();
            _statusFont.Dispose();
            _wordmark.Dispose();
            _smallFont.Dispose();
            _fonts.Dispose();
            _lightFonts.Dispose();
            // The road map may still be drawing: freed when it is done.
            _mapTask?.ContinueWith(task => task.Result?.Dispose(), TaskScheduler.Default);
        }
        base.Dispose(disposing);
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
