using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
    const string Fx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d}";
    const string Modes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b}";
    const string LfxName = Fx + ",1";     // PKEY_FX_PreMixEffectClsid: legacy local (per-stream) effect
    const string GfxName = Fx + ",2";     // PKEY_FX_PostMixEffectClsid: legacy global effect (Clownfish lives here)
    const string SfxName = Fx + ",5";     // PKEY_FX_StreamEffectClsid (modern SFX; v0.1.0 used this)
    const string SysFxOffName = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5"; // PKEY_AudioEndpoint_Disable_SysFx ("Audio enhancements: Off")
    const string BackupBase = @"SOFTWARE\CashVoice\Backup";
    const string None = "<none>";
    public const string ClownfishClsid = "{80E0C6D1-9465-43B2-9BD5-27A3A56CF1B3}";

    // Like Clownfish, we use the legacy LFX/GFX slots. While a mic has any modern SFX/MFX/EFX keys
    // (or the newer composite lists) Windows ignores the legacy slots, so we remove those after
    // backing them up. CashVoice takes the LFX slot and leaves GFX to Clownfish, so both can run
    // on the same mic (mic -> Clownfish -> CashVoice -> app).
    static readonly string[] RemovedForLegacy =
        { SfxName, Fx + ",6", Fx + ",7", Fx + ",13", Fx + ",14", Fx + ",15", Modes + ",5", Modes + ",6", Modes + ",7" };
    static readonly string[] Touched = RemovedForLegacy.Append(LfxName).Append(SysFxOffName).ToArray();

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
                if (fx != null && (Is(fx.GetValue(LfxName)) || Is(fx.GetValue(SfxName)))) set.Add(g);
            }
        }
        catch { }
        return set;
    }

    static bool Is(object? v) => v is string s && s.Equals(Clsid, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when our APO is on the mic the current (legacy LFX slot) way.</summary>
    static bool EnabledCurrentWay(string guid)
    {
        try
        {
            using var fx = Registry.LocalMachine.OpenSubKey($@"{CaptureBase}\{guid}\FxProperties");
            using var bk = Registry.LocalMachine.OpenSubKey($@"{BackupBase}\{guid}");
            return fx != null && Is(fx.GetValue(LfxName)) && bk?.GetValue("v") is int v && v == 2
                   && RemovedForLegacy.All(n => fx.GetValue(n) == null);
        }
        catch { return false; }
    }

    /// <summary>True if Clownfish's APO sits on this mic.</summary>
    public static bool HasClownfish(string guid)
    {
        try
        {
            using var fx = Registry.LocalMachine.OpenSubKey($@"{CaptureBase}\{guid}\FxProperties");
            return fx?.GetValue(GfxName) is string s && s.Equals(ClownfishClsid, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Runs in an elevated process: make exactly `desired` endpoints use the APO.</summary>
    public static void Apply(IEnumerable<string> desired)
    {
        EnsureDataFolder();
        var want = new HashSet<string>(desired, StringComparer.OrdinalIgnoreCase);
        var have = EnabledEndpoints();
        bool changed = false;
        foreach (var g in want.Where(g => !EnabledCurrentWay(g)))
        {
            // Hooked by v0.1.0 (SFX slot, older backup), or Windows/another app changed the keys
            // since: put the original back first, then hook the legacy way.
            if (have.Contains(g)) Disable(g);
            Enable(g);
            changed = true;
        }
        // With "Audio enhancements" off, Windows skips every effect on that mic, CashVoice included.
        foreach (var g in want) if (TurnEnhancementsOn(g)) changed = true;
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

    /// <summary>
    /// The APO keeps shared.bin and apo.log in C:\ProgramData\CashVoice. audiodg can't create that
    /// folder itself, so make sure it exists with the same rights the installer grants.
    /// </summary>
    static void EnsureDataFolder()
    {
        try
        {
            var di = Directory.CreateDirectory(SharedMem.Folder);
            var sec = di.GetAccessControl();
            foreach (var sid in new[] { "S-1-1-0", "S-1-5-19", "S-1-5-33", "S-1-15-2-1" })
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.Modify,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None,
                    AccessControlType.Allow));
            di.SetAccessControl(sec);
        }
        catch { }
    }

    static void Enable(string guid)
    {
        string path = $@"{CaptureBase}\{guid}\FxProperties";
        TakeOwnershipAndGrant(path);
        using var key = Registry.LocalMachine.OpenSubKey(path, true)
                        ?? throw new InvalidOperationException("Endpoint not found: " + guid);

        // Back up every value we touch, so Disable can put the mic back exactly as it was.
        using (var bk = Registry.LocalMachine.CreateSubKey($@"{BackupBase}\{guid}"))
        {
            if (bk.GetValue("v") is not int v || v != 2)
            {
                foreach (var n in bk.GetValueNames()) bk.DeleteValue(n, false);
                var absent = new List<string>();
                foreach (var n in Touched)
                {
                    var val = key.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (val == null) absent.Add(n);
                    else bk.SetValue(n, val, key.GetValueKind(n));
                }
                bk.SetValue("absent", absent.Count == 0 ? new[] { None } : absent.ToArray(), RegistryValueKind.MultiString);
                bk.SetValue("v", 2, RegistryValueKind.DWord);
            }
        }

        foreach (var n in RemovedForLegacy) key.DeleteValue(n, false);
        key.SetValue(LfxName, Clsid, RegistryValueKind.String);
    }

    /// <summary>True if the mic's "Audio enhancements" setting is Off (Windows then loads no effects).</summary>
    public static bool EnhancementsOff(string guid)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{CaptureBase}\{guid}\FxProperties");
            return key?.GetValue(SysFxOffName) is int v && v != 0;
        }
        catch { return false; }
    }

    static bool TurnEnhancementsOn(string guid)
    {
        if (!EnhancementsOff(guid)) return false;
        string path = $@"{CaptureBase}\{guid}\FxProperties";
        TakeOwnershipAndGrant(path);
        using var key = Registry.LocalMachine.OpenSubKey(path, true);
        if (key == null) return false;
        key.SetValue(SysFxOffName, 0, RegistryValueKind.DWord);
        return true;
    }

    static void Disable(string guid)
    {
        string path = $@"{CaptureBase}\{guid}\FxProperties";
        TakeOwnershipAndGrant(path);
        using var key = Registry.LocalMachine.OpenSubKey(path, true);
        if (key == null) return;
        using var bk = Registry.LocalMachine.OpenSubKey($@"{BackupBase}\{guid}");

        if (bk?.GetValue("v") is int v && v == 2)
        {
            var absent = new HashSet<string>((bk.GetValue("absent") as string[]) ?? Array.Empty<string>(),
                                             StringComparer.OrdinalIgnoreCase);
            foreach (var n in Touched)
            {
                if (absent.Contains(n)) { key.DeleteValue(n, false); continue; }
                var val = bk.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (val != null) key.SetValue(n, val, bk.GetValueKind(n));
            }
        }
        else
        {
            // v0.1.0 backup: only the SFX slot, its mode list and Disable_SysFx were changed.
            const string sfxModes = Modes + ",5";
            string? sfx = bk?.GetValue("sfx") as string;
            if (sfx == null || sfx == None) key.DeleteValue(SfxName, false);
            else key.SetValue(SfxName, sfx, RegistryValueKind.String);

            if (bk?.GetValue("sysfxoff") is int off)
            {
                if (off < 0) key.DeleteValue(SysFxOffName, false);
                else key.SetValue(SysFxOffName, off, RegistryValueKind.DWord);
            }

            var modes = bk?.GetValue("modes") as string[];
            if (modes == null || (modes.Length == 1 && modes[0] == None)) key.DeleteValue(sfxModes, false);
            else key.SetValue(sfxModes, modes, RegistryValueKind.MultiString);
            if (Is(key.GetValue(LfxName))) key.DeleteValue(LfxName, false);
        }

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
