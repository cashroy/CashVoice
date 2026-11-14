using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace CashVoice.Core;

/// <summary>
/// Hooks/unhooks the CashVoice APO on capture endpoints (the same technique Equalizer APO and Clownfish use).
/// Needs administrator rights; the UI runs this through a UAC-elevated copy of the exe.
/// </summary>
public static class ApoInstaller
{
    public const string Clsid = "{8F3A7C21-5B6D-4E90-A1C4-7D2E9B0F6A38}";

    const string CaptureBase = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture";
    const string SfxName = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},5";   // PKEY_FX_StreamEffectClsid
    const string ModesName = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},5"; // SFX processing modes (streaming)
    const string BackupBase = @"SOFTWARE\CashVoice\Backup";
    const string None = "<none>";
    static readonly string[] Modes =
    {
        "{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}",   // Default
        "{98951333-B9CD-48B1-A0A3-FF40682D73F7}",   // Communications (Discord/Teams/etc.)
    };

    // ---- public API ------------------------------------------------------------------
    public static string EndpointGuid(string deviceId)
    {
        int i = deviceId.LastIndexOf("}.", StringComparison.Ordinal);
        return i >= 0 ? deviceId.Substring(i + 2) : deviceId;
    }

    public static HashSet<string> EnabledEndpoints()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cap = Registry.LocalMachine.OpenSubKey(CaptureBase);
            if (cap == null) return set;
            foreach (var g in cap.GetSubKeyNames())
            {
                using var fx = cap.OpenSubKey(g + @"\FxProperties");
                if (fx?.GetValue(SfxName) is string s && s.Equals(Clsid, StringComparison.OrdinalIgnoreCase))
                    set.Add(g);
            }
        }
        catch { }
        return set;
    }

    /// <summary>Runs in an elevated process: make exactly `desired` endpoints use the APO.</summary>
    public static void Apply(IEnumerable<string> desired)
    {
        var want = new HashSet<string>(desired, StringComparer.OrdinalIgnoreCase);
        var have = EnabledEndpoints();
        bool changed = false;
        foreach (var g in want.Where(g => !have.Contains(g))) { Enable(g); changed = true; }
        foreach (var g in have.Where(g => !want.Contains(g))) { Disable(g); changed = true; }
        if (changed) RestartAudio();
    }

    public static void RestoreAll()
    {
        foreach (var g in EnabledEndpoints()) { try { Disable(g); } catch { } }
        try { Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\CashVoice", false); } catch { }
        RestartAudio();
    }

    public static bool ApplyElevated(IEnumerable<string> guids)
    {
        var list = guids.ToList();
        var arg = list.Count == 0 ? "none" : string.Join(",", list);
        var psi = new ProcessStartInfo(Environment.ProcessPath!, $"--apply-endpoints {arg}")
        { UseShellExecute = true, Verb = "runas" };
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(90_000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch (Win32Exception) { return false; }   // user declined UAC
    }

    // ---- internals --------------------------------------------------------------------
    static void Enable(string guid)
    {
        string path = $@"{CaptureBase}\{guid}\FxProperties";
        TakeOwnershipAndGrant(path);
        using var key = Registry.LocalMachine.OpenSubKey(path, true)
                        ?? throw new InvalidOperationException("Endpoint not found: " + guid);

        // Back up whatever effect the audio driver had so we can restore it later.
        using (var bk = Registry.LocalMachine.CreateSubKey($@"{BackupBase}\{guid}"))
        {
            if (bk.GetValue("saved") == null)
            {
                bk.SetValue("sfx", key.GetValue(SfxName) as string ?? None, RegistryValueKind.String);
                var modes = key.GetValue(ModesName) as string[];
                bk.SetValue("modes", modes ?? new[] { None }, RegistryValueKind.MultiString);
                bk.SetValue("saved", 1, RegistryValueKind.DWord);
            }
        }

        var existing = (key.GetValue(ModesName) as string[]) ?? Array.Empty<string>();
        var merged = existing.Concat(Modes).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        key.SetValue(SfxName, Clsid, RegistryValueKind.String);
        key.SetValue(ModesName, merged, RegistryValueKind.MultiString);
    }

    static void Disable(string guid)
    {
        string path = $@"{CaptureBase}\{guid}\FxProperties";
        TakeOwnershipAndGrant(path);
        using var key = Registry.LocalMachine.OpenSubKey(path, true);
        if (key == null) return;
        using var bk = Registry.LocalMachine.OpenSubKey($@"{BackupBase}\{guid}");

        string? sfx = bk?.GetValue("sfx") as string;
        if (sfx == null || sfx == None) key.DeleteValue(SfxName, false);
        else key.SetValue(SfxName, sfx, RegistryValueKind.String);

        var modes = bk?.GetValue("modes") as string[];
        if (modes == null || (modes.Length == 1 && modes[0] == None)) key.DeleteValue(ModesName, false);
        else key.SetValue(ModesName, modes, RegistryValueKind.MultiString);

        try { Registry.LocalMachine.DeleteSubKeyTree($@"{BackupBase}\{guid}", false); } catch { }
    }

    static void TakeOwnershipAndGrant(string subPath)
    {
        EnablePrivilege("SeTakeOwnershipPrivilege");
        EnablePrivilege("SeRestorePrivilege");
        EnablePrivilege("SeBackupPrivilege");
        var admins = new NTAccount("BUILTIN", "Administrators");

        using (var k = Registry.LocalMachine.OpenSubKey(subPath, RegistryKeyPermissionCheck.ReadWriteSubTree,
                                                        RegistryRights.TakeOwnership))
        {
            if (k == null) throw new InvalidOperationException("Cannot open " + subPath);
            var rs = new RegistrySecurity();
            rs.SetOwner(admins);
            k.SetAccessControl(rs);
        }
        using (var k = Registry.LocalMachine.OpenSubKey(subPath, RegistryKeyPermissionCheck.ReadWriteSubTree,
                   RegistryRights.ChangePermissions | RegistryRights.ReadPermissions))
        {
            if (k == null) throw new InvalidOperationException("Cannot open " + subPath);
            var rs = k.GetAccessControl();
            rs.AddAccessRule(new RegistryAccessRule(admins, RegistryRights.FullControl,
                InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
            k.SetAccessControl(rs);
        }
    }

    public static void RestartAudio()
    {
        Run("net.exe", "stop audiosrv /y");
        Run("net.exe", "start audiosrv");
    }

    static void Run(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            p?.WaitForExit(30_000);
        }
        catch { }
    }

    // ---- privilege helper -------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct TOKEN_PRIVILEGES { public int Count; public long Luid; public int Attr; }

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool LookupPrivilegeValue(string? system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state,
                                             int len, IntPtr prev, IntPtr retLen);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x20 | 0x08, out var tok)) return;
        try
        {
            if (!LookupPrivilegeValue(null, name, out long luid)) return;
            var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attr = 2 /*SE_PRIVILEGE_ENABLED*/ };
            AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally { CloseHandle(tok); }
    }
}
