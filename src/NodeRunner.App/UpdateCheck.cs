using System.Net.Http;
using System.Text.Json;

namespace NodeRunner.App;

// Asks GitHub once, at start-up, for the latest published version. Nothing is sent but the request itself; any failure
// (offline, GitHub unreachable, unexpected answer) is ignored.
internal sealed class UpdateCheck
{
    private const string LatestRelease = "https://api.github.com/repos/HypnoZDev/NodeRunner/releases/latest";

    // Page opened by the download button: built here, never taken from the answer.
    public const string ReleasePage = "https://github.com/HypnoZDev/NodeRunner/releases/latest";

    private volatile string? _available;
    private int _started;

    // The newer version published (e.g. "1.6.0"), null while unknown or when this one is the latest.
    public string? Available => _available;

    // The release's ZIP and its SHA-256 (hex), null when GitHub did not give both or the address is not the expected one.
    public (string Url, string Sha256)? Package { get; private set; }

    public void Start(string current)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("NodeRunner-Studio");
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                using var document = JsonDocument.Parse(await http.GetStringAsync(LatestRelease).ConfigureAwait(false));
                if (document.RootElement.TryGetProperty("tag_name", out var tag) && tag.GetString() is { } name
                    && Newer(name, current) is { } version)
                {
                    Package = FindPackage(document.RootElement, version);
                    _available = version;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                // No notice.
            }
        });
    }

    public static (string Url, string Sha256)? FindPackage(JsonElement release, string version)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;
        string expected = $"NodeRunner-{version}-win-x64.zip";
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.TryGetProperty("name", out var name) && name.GetString() == expected
                && asset.TryGetProperty("browser_download_url", out var url) && url.GetString() is { } address
                && Updater.IsTrustedUrl(address)
                && asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } text
                && text.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && text.Length == 7 + 64)
                return (address, text[7..]);
        }
        return null;
    }

    // The version of tag ("v1.6.0") if it is above current, else null.
    public static string? Newer(string tag, string current)
    {
        string text = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(text, out var latest) && Version.TryParse(current, out var mine) && latest > mine
            ? latest.ToString(latest.Build >= 0 ? 3 : 2)
            : null;
    }
}
