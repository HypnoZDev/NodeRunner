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
                    _available = version;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                // No notice.
            }
        });
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
