using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace AmbientBar;

/// <summary>
/// Checks GitHub Releases for a newer version and installs it in place:
/// download next to the exe, verify its SHA-256, rename the running exe aside, move the new one in, restart.
/// </summary>
static class Updates
{
    public const string Repo = "vikpek/AmbientBar";
    public const string ReleasesPage = $"https://github.com/{Repo}/releases/latest";
    const string ExeName = "AmbientBar.exe";
    const string OldExeName = "AmbientBar.old.exe";
    const string NewExeName = "AmbientBar.new.exe";

    public sealed record Asset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string Url);

    public sealed record Release(
        [property: JsonPropertyName("tag_name")] string Tag,
        [property: JsonPropertyName("assets")] List<Asset>? Assets);

    public static Version Current => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    static string ExePath => Environment.ProcessPath!;
    static string ExeDir => Path.GetDirectoryName(ExePath)!;

    /// <summary>Silent on any failure (offline, rate limit, ...).</summary>
    public static async Task<Release?> GetNewerReleaseAsync()
    {
        try
        {
            using var http = CreateClient(TimeSpan.FromSeconds(10));
            var latest = await http.GetFromJsonAsync<Release>($"https://api.github.com/repos/{Repo}/releases/latest");
            if (latest == null || !Version.TryParse(latest.Tag.TrimStart('v'), out var v)) return null;
            return v > Current ? latest : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Only the published single-file exe can replace itself, and only from a folder we can write to
    /// (not e.g. Program Files). Dev builds have AmbientBar.dll next to the exe.
    /// </summary>
    public static bool CanSelfUpdate
    {
        get
        {
            if (File.Exists(Path.Combine(ExeDir, "AmbientBar.dll"))) return false;
            try
            {
                var probe = Path.Combine(ExeDir, $".ambientbar-write-test-{Guid.NewGuid():N}");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static async Task InstallAsync(Release release, IProgress<int> progress)
    {
        var newPath = Path.Combine(ExeDir, NewExeName);
        var oldPath = Path.Combine(ExeDir, OldExeName);
        using var http = CreateClient(TimeSpan.FromMinutes(15));

        var checksum = await http.GetStringAsync(AssetUrl(release, ExeName + ".sha256")).ConfigureAwait(false);
        var expected = checksum.Trim().Split(' ', 2)[0].ToLowerInvariant();

        string actual;
        using (var resp = await http.GetAsync(AssetUrl(release, ExeName), HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? 0, read = 0;
            int lastPercent = -1;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await using var dst = File.Create(newPath);
            var buffer = new byte[81920];
            int n;
            while ((n = await src.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n)).ConfigureAwait(false);
                hash.AppendData(buffer, 0, n);
                read += n;
                int percent = total > 0 ? (int)(read * 100 / total) : 0;
                if (percent != lastPercent) progress.Report(lastPercent = percent);
            }
            actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        if (actual != expected)
        {
            File.Delete(newPath);
            throw new InvalidOperationException("the download didn't match its checksum.");
        }

        // Windows won't overwrite a running exe, but it will rename one.
        File.Move(ExePath, oldPath, overwrite: true);
        try
        {
            File.Move(newPath, ExePath);
        }
        catch
        {
            File.Move(oldPath, ExePath);
            throw;
        }
    }

    /// <summary>Starts the freshly installed exe; it waits for this instance to exit before taking over.</summary>
    public static void Restart(bool resumePlayback)
    {
        var psi = new ProcessStartInfo(ExePath) { UseShellExecute = false };
        psi.ArgumentList.Add("--after-update");
        if (resumePlayback) psi.ArgumentList.Add("--resume");
        Process.Start(psi);
    }

    /// <summary>Removes the previous exe left behind by an update. It may stay locked for a moment, so retry briefly.</summary>
    public static void CleanUpAfterUpdate()
    {
        var old = Path.Combine(ExeDir, OldExeName);
        if (!File.Exists(old)) return;
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 20 && File.Exists(old); i++)
            {
                try { File.Delete(old); }
                catch { await Task.Delay(500); }
            }
        });
    }

    public static void OpenDownloadPage() =>
        Process.Start(new ProcessStartInfo(ReleasesPage) { UseShellExecute = true });

    static string AssetUrl(Release release, string name) =>
        release.Assets?.FirstOrDefault(a => a.Name == name)?.Url
        ?? $"https://github.com/{Repo}/releases/download/{release.Tag}/{name}";

    static HttpClient CreateClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"AmbientBar/{Current}");
        return http;
    }
}
