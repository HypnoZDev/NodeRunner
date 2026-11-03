using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using NodeRunner.App.World;

namespace NodeRunner.App;

// Start-up screen, shown while the game data and the map load in the background: artwork drawn here (night-blue
// gradient, glow, a network of nodes crossed by a moving light, no text inside it), the name, a loading bar following
// the real loading steps, and the credits.
internal sealed partial class SplashForm : Form
{
    private static readonly Color Accent = Color.FromArgb(0x4C, 0x8D, 0xFF);
    private static readonly Color AccentLight = Color.FromArgb(0x5F, 0xD3, 0xFF);
    private const double Timeout = 180.0; // Seconds: never keep the user waiting on a stuck loading.
    private const double ModelsTimeout = 30.0; // Seconds for the 3D models: the rest loads in the main window.
    private const float MapShare = 0.85f; // Share of the bar for the map, the rest for the 3D models.
    private const float ModelsEnough = 0.90f; // The last few models arrive in the main window, a moment later.

    private static readonly (string Prefix, float From, float To)[] Steps =
    [
        ("Lecture des pathnodes", 0.12f, 0.25f),
        ("Lecture des scénarios", 0.25f, 0.32f),
        ("Index des navmesh", 0.32f, 0.36f),
        ("Lecture des packs DLC", 0.36f, 0.40f),
        ("Lecture des types d'objets", 0.40f, 0.62f),
        ("Lecture des objets de la carte", 0.62f, 0.86f),
        ("Index des modèles 3D", 0.86f, 0.90f),
        ("Index des textures", 0.90f, 0.94f),
        ("Index des collisions", 0.94f, 0.99f),
    ];

    private readonly WorldLoader _world;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly PrivateFontCollection _fonts = new();
    private readonly Font _brandFont;
    private readonly Font _taglineFont = new("Segoe UI", 11f, GraphicsUnit.Pixel);
    private readonly Font _statusFont = new("Segoe UI", 12f, GraphicsUnit.Pixel);
    private readonly Font _creditFont = new("Segoe UI Semibold", 12f, GraphicsUnit.Pixel);
    private readonly Font _smallFont = new("Segoe UI", 10f, GraphicsUnit.Pixel);
    private readonly Bitmap _artwork;
    // Drawn in points, then scaled to the display (Windows scale).
    private static readonly Size Design = new(720, 405);
    private readonly float _scale;
    private readonly PointF[] _route;
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
        BackColor = Color.FromArgb(0x0B, 0x0F, 0x1A);

        string brand = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "unbounded-bold.ttf");
        if (File.Exists(brand))
            _fonts.AddFontFile(brand);
        _brandFont = _fonts.Families.Length > 0
            ? new Font(_fonts.Families[0], 44f, FontStyle.Regular, GraphicsUnit.Pixel)
            : new Font("Segoe UI", 44f, FontStyle.Bold, GraphicsUnit.Pixel);

        (_artwork, _route) = DrawArtwork(Design, _scale);
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

    private void Step()
    {
        double now = _clock.Elapsed.TotalSeconds;
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
            // Closed at once (no wait for the bar to reach its end): the main window opens on the loaded view.
            _timer.Stop();
            Close();
            return;
        }
        // The bar glides towards the real progress, never backwards.
        _shownProgress = Math.Max(_shownProgress, _shownProgress + (target - _shownProgress) * 0.12f);
        Invalidate();
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
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.DrawImage(_artwork, new Rectangle(0, 0, _artwork.Width, _artwork.Height));
        g.ScaleTransform(_scale, _scale);
        DrawRunner(g);

        // Name: "NodeRunner" white, "Studio" in the accent gradient, as on the website.
        const float left = 44f;
        g.DrawString("NodeRunner", _brandFont, Brushes.White, left - 4f, 118f);
        var studioSize = g.MeasureString("Studio", _brandFont);
        using (var studio = new LinearGradientBrush(new RectangleF(left, 170f, studioSize.Width, studioSize.Height), Accent, AccentLight, 0f))
            g.DrawString("Studio", _brandFont, studio, left - 4f, 170f);
        using (var tagline = new SolidBrush(Color.FromArgb(0xA8, 0xB2, 0xC8)))
            g.DrawString("Le studio 3D pour explorer, comprendre et modifier la carte de GTA V.", _taglineFont, tagline, left, 236f);
        using (var version = new SolidBrush(Color.FromArgb(0x70, 0x7A, 0x90)))
            g.DrawString($"v{Program.Version}", _smallFont, version, Design.Width - 70f, 22f);

        // Loading bar and current step.
        float barTop = Design.Height - 74f, barWidth = Design.Width - left * 2f;
        using (var status = new SolidBrush(Color.FromArgb(0x8A, 0x94, 0xAA)))
            g.DrawString(StatusText(), _statusFont, status, left, barTop - 22f);
        using (var track = new SolidBrush(Color.FromArgb(0x1E, 0x26, 0x38)))
            FillRounded(g, track, new RectangleF(left, barTop, barWidth, 4f));
        float filled = Math.Max(4f, barWidth * Math.Clamp(_shownProgress, 0f, 1f));
        using (var fill = new LinearGradientBrush(new RectangleF(left, barTop, barWidth, 4f), Accent, AccentLight, 0f))
            FillRounded(g, fill, new RectangleF(left, barTop, filled, 4f));

        // Credits, with the notice that always goes with them.
        float creditsTop = Design.Height - 50f;
        g.DrawString("Créé par Inside Rockstar / HypnoZ", _creditFont, Brushes.White, left, creditsTop);
        using (var notice = new SolidBrush(Color.FromArgb(0x6A, 0x74, 0x8A)))
            g.DrawString("Non affilié à Rockstar Games ni à Take-Two Interactive", _smallFont, notice, left, creditsTop + 18f);
    }

    private string StatusText()
    {
        if (_modelsStart is not null)
            return "Chargement des modèles 3D...";
        string status = _world.Status;
        int colon = status.IndexOf(" :", StringComparison.Ordinal);
        string step = colon > 0 ? status[..colon] : status.TrimEnd('.');
        return $"{step}...";
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

    private void DrawRunner(Graphics g)
    {
        float t = (float)(_clock.Elapsed.TotalSeconds * 0.22 % 1.0);
        for (int i = 0; i < 14; i++)
        {
            var p = PointOnRoute(t - i * 0.006f);
            int alpha = 220 - i * 15;
            float size = 6f - i * 0.3f;
            using var brush = new SolidBrush(Color.FromArgb(Math.Max(alpha, 0), AccentLight));
            g.FillEllipse(brush, p.X - size / 2f, p.Y - size / 2f, size, size);
        }
        var head = PointOnRoute(t);
        using var glow = new SolidBrush(Color.FromArgb(50, AccentLight));
        g.FillEllipse(glow, head.X - 12f, head.Y - 12f, 24f, 24f);
    }

    private PointF PointOnRoute(float t)
    {
        t = (t % 1f + 1f) % 1f;
        float position = t * (_route.Length - 1);
        int i = Math.Min((int)position, _route.Length - 2);
        float f = position - i;
        return new PointF(_route[i].X + (_route[i + 1].X - _route[i].X) * f, _route[i].Y + (_route[i + 1].Y - _route[i].Y) * f);
    }

    // The still part of the artwork, drawn once: gradient, glows, then a network of nodes (always the same) towards
    // the right, fading behind the text on the left.
    private static (Bitmap, PointF[]) DrawArtwork(Size size, float scale)
    {
        var bitmap = new Bitmap((int)(size.Width * scale), (int)(size.Height * scale));
        using var g = Graphics.FromImage(bitmap);
        g.ScaleTransform(scale, scale);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(Point.Empty, size);

        using (var background = new LinearGradientBrush(bounds, Color.FromArgb(0x0A, 0x0E, 0x18), Color.FromArgb(0x14, 0x1E, 0x3A), 35f))
            g.FillRectangle(background, bounds);
        Glow(g, new RectangleF(size.Width * 0.45f, -size.Height * 0.55f, size.Width * 0.9f, size.Height * 1.3f), Color.FromArgb(110, 0x2A, 0x5B, 0xD7));
        Glow(g, new RectangleF(size.Width * 0.62f, size.Height * 0.35f, size.Width * 0.6f, size.Height * 0.9f), Color.FromArgb(60, 0x6A, 0x3B, 0xC9));

        // Nodes scattered towards the right, each linked to its nearest neighbours.
        var random = new Random(7);
        var nodes = new List<PointF>();
        for (int i = 0; i < 90; i++)
        {
            float x = size.Width * (0.30f + 0.75f * MathF.Pow((float)random.NextDouble(), 0.7f));
            float y = size.Height * (-0.05f + 1.1f * (float)random.NextDouble());
            nodes.Add(new PointF(x, y));
        }
        static float Distance(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        static int Fade(float x, float width) => (int)(255 * Math.Clamp((x / width - 0.28f) / 0.35f, 0f, 1f));

        foreach (var a in nodes)
        {
            foreach (var b in nodes.Where(b => b != a).OrderBy(b => Distance(a, b)).Take(3))
            {
                int alpha = Math.Min(Fade(a.X, size.Width), Fade(b.X, size.Width)) * 70 / 255;
                using var pen = new Pen(Color.FromArgb(alpha, 0x4C, 0x8D, 0xFF), 1f);
                g.DrawLine(pen, a, b);
            }
        }
        foreach (var node in nodes)
        {
            int alpha = Fade(node.X, size.Width);
            float r = 1.5f + (float)random.NextDouble() * 1.8f;
            using var brush = new SolidBrush(Color.FromArgb(alpha * 200 / 255, 0x9C, 0xC4, 0xFF));
            g.FillEllipse(brush, node.X - r, node.Y - r, r * 2f, r * 2f);
        }

        // Route of the moving light: from the left edge of the network, hopping to the next node to the right.
        var route = new List<PointF>();
        var current = nodes.Where(n => n.X > size.Width * 0.45f).OrderBy(n => MathF.Abs(n.Y - size.Height * 0.7f) + n.X * 0.3f).First();
        route.Add(current);
        while (true)
        {
            var next = nodes.Where(n => n.X > current.X + 15f).OrderBy(n => Distance(current, n)).FirstOrDefault();
            if (next.IsEmpty || route.Count > 14)
                break;
            route.Add(next);
            current = next;
        }
        using (var trail = new Pen(Color.FromArgb(70, 0x5F, 0xD3, 0xFF), 1.5f))
            g.DrawLines(trail, route.ToArray());
        return (bitmap, route.ToArray());
    }

    private static void Glow(Graphics g, RectangleF area, Color color)
    {
        using var path = new GraphicsPath();
        path.AddEllipse(area);
        using var brush = new PathGradientBrush(path) { CenterColor = color, SurroundColors = [Color.FromArgb(0, color)] };
        g.FillPath(brush, path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _artwork.Dispose();
            _brandFont.Dispose();
            _taglineFont.Dispose();
            _statusFont.Dispose();
            _creditFont.Dispose();
            _smallFont.Dispose();
            _fonts.Dispose();
        }
        base.Dispose(disposing);
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
