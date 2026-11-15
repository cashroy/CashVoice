using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace CashVoice.Core;

public readonly record struct Hotkey(uint Mods, uint Vk)
{
    public const uint Alt = 1, Ctrl = 2, Shift = 4, Win = 8;
    public bool IsEmpty => Vk == 0;

    public override string ToString()
    {
        if (IsEmpty) return "";
        var parts = new List<string>();
        if ((Mods & Ctrl) != 0) parts.Add("Ctrl");
        if ((Mods & Alt) != 0) parts.Add("Alt");
        if ((Mods & Shift) != 0) parts.Add("Shift");
        if ((Mods & Win) != 0) parts.Add("Win");
        var key = KeyInterop.KeyFromVirtualKey((int)Vk);
        parts.Add(key >= Key.D0 && key <= Key.D9 ? ((int)(key - Key.D0)).ToString() : key.ToString());
        return string.Join("+", parts);
    }

    public static Hotkey Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return default;
        uint mods = 0, vk = 0;
        foreach (var raw in s.Split('+'))
        {
            var p = raw.Trim();
            switch (p.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= Ctrl; break;
                case "alt": mods |= Alt; break;
                case "shift": mods |= Shift; break;
                case "win": case "windows": mods |= Win; break;
                default:
                    if (p.Length == 1 && char.IsDigit(p[0]))
                        vk = (uint)KeyInterop.VirtualKeyFromKey(Key.D0 + (p[0] - '0'));
                    else if (Enum.TryParse<Key>(p, true, out var k))
                        vk = (uint)KeyInterop.VirtualKeyFromKey(k);
                    break;
            }
        }
        return vk == 0 ? default : new Hotkey(mods, vk);
    }
}

/// <summary>Global hotkeys via RegisterHotKey on the main window's HWND.</summary>
public sealed class HotkeyManager : IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    readonly HwndSource _src;
    readonly IntPtr _hwnd;
    readonly Dictionary<int, Action> _actions = new();
    int _next = 1;

    public HotkeyManager(Window window)
    {
        var helper = new WindowInteropHelper(window);
        _hwnd = helper.EnsureHandle();
        _src = HwndSource.FromHwnd(_hwnd);
        _src.AddHook(Hook);
    }

    public bool Register(Hotkey hk, Action action)
    {
        int id = _next++;
        if (!RegisterHotKey(_hwnd, id, hk.Mods | MOD_NOREPEAT, hk.Vk)) return false;
        _actions[id] = action;
        return true;
    }

    public void Clear()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_hwnd, id);
        _actions.Clear();
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var a))
        {
            handled = true;
            try { a(); } catch { }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Clear();
        _src.RemoveHook(Hook);
    }
}
