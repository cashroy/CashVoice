using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CashVoice.Core;

/// <summary>Downloads yt-dlp + ffmpeg on first use and imports audio from YouTube links.</summary>
public static class Tools
{
    public static string YtDlp => Path.Combine(AppPaths.Bin, "yt-dlp.exe");
    public static string Ffmpeg => Path.Combine(AppPaths.Bin, "ffmpeg.exe");

    static readonly HttpClient Http = CreateClient();
    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("CashVoice/0.1");
        return c;
    }

    static async Task DownloadFile(string url, string dest, CancellationToken ct)
    {
        string tmp = dest + ".part";
        using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(tmp);
            await src.CopyToAsync(dst, ct);
        }
        File.Move(tmp, dest, true);
    }

    public static async Task EnsureAsync(IProgress<string> log, CancellationToken ct = default)
    {
        AppPaths.Ensure();
        if (!File.Exists(YtDlp))
        {
            log.Report("Downloading yt-dlp…");
            await DownloadFile("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe", YtDlp, ct);
        }
        if (!File.Exists(Ffmpeg))
        {
            log.Report("Downloading ffmpeg (one-time, ~100 MB)…");
            string zip = Path.Combine(AppPaths.Bin, "ffmpeg.zip");
            await DownloadFile(
                "https://github.com/yt-dlp/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip",
                zip, ct);
            using (var za = ZipFile.OpenRead(zip))
            {
                var entry = za.Entries.First(e => e.FullName.Replace('\\', '/').EndsWith("/bin/ffmpeg.exe",
                    StringComparison.OrdinalIgnoreCase));
                entry.ExtractToFile(Ffmpeg, true);
            }
            File.Delete(zip);
        }
    }

    public static async Task UpdateYtDlpAsync()
    {
        if (!File.Exists(YtDlp)) return;
        var psi = new ProcessStartInfo(YtDlp, "-U") { CreateNoWindow = true, UseShellExecute = false };
        using var p = Process.Start(psi);
        if (p != null) await p.WaitForExitAsync();
    }

    /// <summary>Downloads the audio of a video as mp3 into outDir and returns the file path.</summary>
    public static async Task<string> DownloadAudioAsync(string url, string outDir, IProgress<string> log,
                                                        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("That doesn't look like a valid link.");

        await EnsureAsync(log, ct);
        log.Report("Downloading audio…");

        var psi = new ProcessStartInfo(YtDlp)
        {
            CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[]
        {
            "--encoding", "utf-8", "-x", "--audio-format", "mp3", "--audio-quality", "0",
            "--no-playlist", "--windows-filenames", "--no-warnings",
            "--ffmpeg-location", AppPaths.Bin,
            "--no-simulate", "--print", "after_move:filepath",
            "-o", Path.Combine(outDir, "%(title).80s [%(id)s].%(ext)s"),
            "--", uri.ToString(),
        }) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start yt-dlp.");
        var stdout = new List<string>();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) stdout.Add(e.Data!.Trim()); };
        proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) stderr.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync(ct);

        string? file = stdout.LastOrDefault(l => File.Exists(l));
        if (proc.ExitCode != 0 || file == null)
            throw new InvalidOperationException("Download failed: " + stderr.ToString().Trim());
        return file;
    }

    public static string CleanTitle(string filePath)
    {
        string name = Path.GetFileNameWithoutExtension(filePath);
        return Regex.Replace(name, @"\s*\[[A-Za-z0-9_-]{6,}\]$", "").Trim();
    }
}
