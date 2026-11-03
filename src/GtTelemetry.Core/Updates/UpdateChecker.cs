using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace GtTelemetry.Core.Updates;

/// <summary>A published release: its version, the installer to download and what changed.</summary>
public sealed record Release(Version Version, string Tag, string? InstallerUrl, string? InstallerName, string Notes, string PageUrl);

/// <summary>What the releases page says compared with this copy: the newest release, and how many releases are newer.</summary>
public sealed record UpdateInfo(Version Current, Release? Latest, int Behind)
{
    public bool Available => Latest != null && Latest.Version > Current && Latest.InstallerUrl != null;
}

/// <summary>
/// Checks the GitHub releases of SimEffects Telemetry for a newer version and installs it. A release is a tag like
/// <c>v0.3.0</c> with the installer (<c>SimEffectsTelemetry-Setup-0.3.0.exe</c>) attached; drafts and pre-releases
/// are skipped. Installing runs the installer silently; it closes nothing itself, so the app exits right after starting
/// it and the installer starts the new version when it's done.
/// </summary>
public static class UpdateChecker
{
    public const string Repo = "rosscarlson/SimEffects-Telemetry";

    public static string ReleasesPage => $"https://github.com/{Repo}/releases";

    private static readonly HttpClient http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("SimEffectsTelemetry-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>"v0.3.0", "0.3" and so on as a three-part version; null if it isn't one.</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        string t = tag.Trim().TrimStart('v', 'V');
        int dash = t.IndexOfAny(['-', '+']);
        if (dash >= 0) t = t[..dash];
        if (!Version.TryParse(t.Contains('.') ? t : t + ".0", out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }

    public static async Task<UpdateInfo> CheckAsync(Version current, CancellationToken ct = default)
    {
        current = new Version(current.Major, current.Minor, Math.Max(current.Build, 0));
        using var resp = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases?per_page=100", ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var releases = new List<Release>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.GetProperty("draft").GetBoolean() || r.GetProperty("prerelease").GetBoolean()) continue;
            string tag = r.GetProperty("tag_name").GetString() ?? "";
            if (ParseVersion(tag) is not { } v) continue;
            string? url = null, name = null;
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                string n = a.GetProperty("name").GetString() ?? "";
                if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && n.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                {
                    url = a.GetProperty("browser_download_url").GetString();
                    name = n;
                }
            }
            string notes = r.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";
            releases.Add(new Release(v, tag, url, name, notes, r.GetProperty("html_url").GetString() ?? ReleasesPage));
        }
        var latest = releases.OrderByDescending(r => r.Version).FirstOrDefault();
        return new UpdateInfo(current, latest, releases.Count(r => r.Version > current));
    }

    /// <summary>Downloads the release's installer to %TEMP%, reporting 0-1 as it goes. Returns the file's path.</summary>
    public static async Task<string> DownloadAsync(Release release, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (release.InstallerUrl == null) throw new InvalidOperationException("That release has no installer attached.");
        string dir = Path.Combine(Path.GetTempPath(), "SimEffectsTelemetry-Update");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, release.InstallerName ?? $"SimEffectsTelemetry-Setup-{release.Version}.exe");
        using var resp = await http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long? total = resp.Content.Headers.ContentLength;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(path))
        {
            var buffer = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report((double)done / total.Value);
            }
        }
        return path;
    }

    /// <summary>Starts the installer without its wizard (Windows still asks to allow it); it puts the new version in
    /// place and starts it. The caller should exit straight after.</summary>
    public static void RunInstaller(string path) =>
        Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
}
