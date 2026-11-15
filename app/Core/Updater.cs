using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CashVoice.Core;

/// <summary>
/// In-place updates from GitHub Releases. No reinstall: the new CashVoice.exe (and the APO DLL, if it
/// changed) is downloaded, swapped in next to the running copy by a short elevated helper, and the app
/// relaunches. Running files can't be overwritten on Windows but they can be renamed, so the old ones
/// are renamed to *.old and deleted on the next reboot.
/// </summary>
public static class Updater
{
    const string Repo = "cashroy/CashVoice";
    const string LatestApi = $"https://api.github.com/repos/{Repo}/releases/latest";

    public sealed record Release(Version Version, string Tag, string PageUrl,
                                 Asset App, Asset? Apo);
    public sealed record Asset(string Name, string Url, string? Sha256);

    public static Version Current
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public static string Arch => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    /// <summary>Folder the app is installed in (works for single-file builds).</summary>
    public static readonly string InstallDir =
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
    public static string ExePath => Path.Combine(InstallDir, "CashVoice.exe");
    public static string ApoPath => Path.Combine(InstallDir, "CashVoiceAPO.dll");

    static readonly HttpClient Http = CreateClient();
    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("CashVoice-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    static Version? ParseVersion(string tag)
    {
        var s = tag.Trim().TrimStart('v', 'V');
        int dash = s.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) s = s[..dash];
        if (!Version.TryParse(s, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    /// <summary>Returns the latest release if it is newer than this build and has files for this PC.</summary>
    public static async Task<Release?> CheckAsync(CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync(LatestApi, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;   // no releases yet
        if (resp.StatusCode is System.Net.HttpStatusCode.Forbidden or (System.Net.HttpStatusCode)429)
            throw new HttpRequestException("GitHub is limiting update checks from this network; try again in an hour");
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"GitHub answered {(int)resp.StatusCode}");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        var ver = ParseVersion(tag);
        if (ver == null || ver <= Current) return null;

        Asset? Find(string name)
        {
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                if (!string.Equals(a.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase)) continue;
                string? digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                string? sha = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null;
                return new Asset(name, a.GetProperty("browser_download_url").GetString()!, sha);
            }
            return null;
        }

        var app = Find($"CashVoice-{Arch}.exe");
        if (app == null) return null;
        return new Release(ver, tag, root.GetProperty("html_url").GetString() ?? "", app, Find($"CashVoiceAPO-{Arch}.dll"));
    }

    /// <summary>Downloads the release files into a temp folder and verifies their SHA-256.</summary>
    public static async Task<string> DownloadAsync(Release r, IProgress<string> log, CancellationToken ct = default)
    {
        string dir = Path.Combine(Path.GetTempPath(), "CashVoiceUpdate", r.Tag);
        Directory.CreateDirectory(dir);
        await Fetch(r.App, Path.Combine(dir, "CashVoice.exe"), log, ct);
        if (r.Apo != null) await Fetch(r.Apo, Path.Combine(dir, "CashVoiceAPO.dll"), log, ct);
        return dir;
    }

    static async Task Fetch(Asset a, string dest, IProgress<string> log, CancellationToken ct)
    {
        string tmp = dest + ".part";
        using (var resp = await Http.GetAsync(a.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(tmp);
            var buf = new byte[1 << 16];
            long got = 0; int n, lastPct = -1;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                got += n;
                int pct = total > 0 ? (int)(got * 100 / total) : -1;
                if (pct != lastPct && pct >= 0) { lastPct = pct; log.Report($"Downloading update… {pct}%"); }
            }
        }
        if (a.Sha256 != null)
        {
            await using var fs = File.OpenRead(tmp);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!hash.Equals(a.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tmp);
                throw new InvalidOperationException("The download was corrupted (checksum mismatch). Try again.");
            }
        }
        File.Move(tmp, dest, true);
    }

    /// <summary>Runs the swap in an elevated copy of the current exe (Program Files needs admin).</summary>
    public static bool ApplyElevated(string folder)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!, $"--apply-update \"{folder}\"")
        { UseShellExecute = true, Verb = "runas" };
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(120_000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch (Win32Exception) { return false; }   // UAC declined
    }

    /// <summary>Starts the freshly installed exe. The caller exits right after.</summary>
    public static void Relaunch()
    {
        Process.Start(new ProcessStartInfo(ExePath, "--updated") { UseShellExecute = true });
    }

    // ---- elevated helper -------------------------------------------------------------------
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool MoveFileEx(string existing, string? newName, int flags);
    const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

    /// <summary>Elevated: swap in the new files. Returns a process exit code (0 = success).</summary>
    public static int ApplyUpdate(string folder)
    {
        string newExe = Path.Combine(folder, "CashVoice.exe");
        string newApo = Path.Combine(folder, "CashVoiceAPO.dll");
        if (!File.Exists(newExe)) return 2;

        CleanupOld();
        if (!Swap(newExe, ExePath)) return 3;

        if (File.Exists(newApo) && File.Exists(ApoPath) && !SameFile(newApo, ApoPath))
        {
            // The audio engine has the DLL loaded: rename it out of the way, drop the new one in,
            // then restart audio so audiodg picks it up. Same path + CLSID, so no re-registration.
            if (Swap(newApo, ApoPath)) ApoInstaller.RestartAudio();
        }
        try { Directory.Delete(folder, true); } catch { }
        return 0;
    }

    static bool Swap(string src, string dest)
    {
        string old = $"{dest}.{DateTime.UtcNow.Ticks}.old";
        try
        {
            File.Move(dest, old);                       // allowed even while the file is in use
            try { File.Copy(src, dest, true); }
            catch { File.Move(old, dest); throw; }      // roll back
            if (!TryDelete(old)) MoveFileEx(old, null, MOVEFILE_DELAY_UNTIL_REBOOT);
            return true;
        }
        catch { return false; }
    }

    static bool SameFile(string a, string b)
    {
        using var fa = File.OpenRead(a);
        using var fb = File.Open(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return SHA256.HashData(fa).AsSpan().SequenceEqual(SHA256.HashData(fb));
    }

    static bool TryDelete(string path) { try { File.Delete(path); return true; } catch { return false; } }

    static void CleanupOld()
    {
        try { foreach (var f in Directory.GetFiles(InstallDir, "*.old")) TryDelete(f); } catch { }
    }
}
