using System;
using System.Windows.Controls;
using System.Windows.Input;
using CashVoice.Core;

namespace CashVoice.Controls;

/// <summary>Click it, press a key combo. Backspace/Delete/Esc clears.</summary>
public sealed class HotkeyBox : TextBox
{
    public event EventHandler? ValueChanged;

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        MinWidth = 150;
        Height = 26;
        VerticalContentAlignment = System.Windows.VerticalAlignment.Center;
        ToolTip = "Click, then press a key combination. Backspace clears.";
    }

    public string Value
    {
        get => Text;
        set => Text = value ?? "";
    }

    static bool CanBeBare(Key k) =>
        (k >= Key.F1 && k <= Key.F24) || (k >= Key.NumPad0 && k <= Key.Divide) ||
        k is Key.Pause or Key.Scroll or Key.Insert or Key.Home or Key.End or Key.PageUp or Key.PageDown;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift
            or Key.RightShift or Key.LWin or Key.RWin or Key.None or Key.DeadCharProcessed) return;

        var mods = (uint)Keyboard.Modifiers;   // Alt=1, Ctrl=2, Shift=4, Windows=8 (same as MOD_*)
        if (mods == 0 && key is Key.Back or Key.Delete or Key.Escape)
        {
            Value = "";
            ValueChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (mods == 0 && !CanBeBare(key)) return;   // plain letters would block normal typing system-wide

        Value = new Hotkey(mods, (uint)KeyInterop.VirtualKeyFromKey(key)).ToString();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }
}
