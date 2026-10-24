using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace AmbientBar;

/// <summary>Checks GitHub Releases for a newer version. Silent on any failure (offline, private repo, rate limit).</summary>
static class Updates
{
    public const string Repo = "vikpek/AmbientBar";
    public const string ReleasesPage = $"https://github.com/{Repo}/releases/latest";

    public sealed record Release([property: JsonPropertyName("tag_name")] string Tag);

    public static Version Current => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    public static async Task<Release?> GetNewerReleaseAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"AmbientBar/{Current}");
            var latest = await http.GetFromJsonAsync<Release>($"https://api.github.com/repos/{Repo}/releases/latest");
            if (latest == null || !Version.TryParse(latest.Tag.TrimStart('v'), out var v)) return null;
            return v > Current ? latest : null;
        }
        catch
        {
            return null;
        }
    }

    public static void OpenDownloadPage() =>
        Process.Start(new ProcessStartInfo(ReleasesPage) { UseShellExecute = true });
}
