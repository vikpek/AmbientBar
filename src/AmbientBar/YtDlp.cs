using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AmbientBar;

/// <summary>Thin wrapper around the yt-dlp executable for searching and resolving audio streams.</summary>
static class YtDlp
{
    static readonly string LocalDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AmbientBar");
    static readonly SemaphoreSlim Lock = new(1, 1);
    static string? _exe;

    public static async Task<List<Track>> SearchAsync(string query, CancellationToken ct)
    {
        query = query.Trim();
        bool isUrl = Uri.TryCreate(query, UriKind.Absolute, out var uri) && uri.Scheme.StartsWith("http");
        var target = isUrl ? query : $"ytsearch15:{query}";

        var output = await RunAsync(ct, "--flat-playlist", "--no-playlist", "--playlist-end", "25",
            "--print", "%(id)s\t%(duration)s\t%(channel)s\t%(title)s", target);

        var tracks = new List<Track>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\t', 4);
            if (parts.Length < 4) continue;
            tracks.Add(new Track
            {
                Id = parts[0],
                Duration = double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
                Channel = parts[2] == "NA" ? null : parts[2],
                Title = parts[3],
            });
        }
        return tracks;
    }

    /// <summary>Returns a direct audio-only stream URL. These expire after a few hours.</summary>
    public static async Task<string> ResolveAudioUrlAsync(string videoId, CancellationToken ct)
    {
        var output = await RunAsync(ct, "-f", "bestaudio[ext=m4a]/bestaudio", "-g", "--no-playlist",
            $"https://www.youtube.com/watch?v={videoId}");
        var url = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return url ?? throw new InvalidOperationException("yt-dlp returned no stream URL.");
    }

    static async Task<string> RunAsync(CancellationToken ct, params string[] args)
    {
        var exe = await GetExeAsync(ct);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--encoding");
        psi.ArgumentList.Add("utf-8");
        psi.ArgumentList.Add("--no-warnings");
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start yt-dlp.");
        using var reg = ct.Register(() => { try { p.Kill(); } catch { } });
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();

        if (p.ExitCode != 0)
        {
            var err = (await stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault() ?? $"yt-dlp exited with code {p.ExitCode}";
            throw new InvalidOperationException(err);
        }
        return await stdout;
    }

    /// <summary>Finds yt-dlp next to the app or on PATH; otherwise downloads it once into LocalAppData.</summary>
    static async Task<string> GetExeAsync(CancellationToken ct)
    {
        if (_exe != null) return _exe;
        await Lock.WaitAsync(ct);
        try
        {
            if (_exe != null) return _exe;

            var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe") };
            candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(d => Path.Combine(d.Trim(), "yt-dlp.exe")));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Links", "yt-dlp.exe"));

            var found = candidates.FirstOrDefault(File.Exists);
            if (found != null) return _exe = found;

            var local = Path.Combine(LocalDir, "yt-dlp.exe");
            if (!File.Exists(local))
            {
                Directory.CreateDirectory(LocalDir);
                using var http = new HttpClient();
                var bytes = await http.GetByteArrayAsync(
                    "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe", ct);
                await File.WriteAllBytesAsync(local + ".tmp", bytes, ct);
                File.Move(local + ".tmp", local, true);
            }
            else if (File.GetLastWriteTimeUtc(local) < DateTime.UtcNow.AddDays(-7))
            {
                // YouTube changes often; keep our private copy fresh. Fire and forget.
                File.SetLastWriteTimeUtc(local, DateTime.UtcNow);
                _ = Task.Run(() =>
                {
                    try { Process.Start(new ProcessStartInfo(local, "-U") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(); }
                    catch { }
                });
            }
            return _exe = local;
        }
        finally
        {
            Lock.Release();
        }
    }
}
