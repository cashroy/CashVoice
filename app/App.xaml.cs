using System;
using System.Linq;
using System.Threading;
using System.Windows;
using CashVoice.Core;

namespace CashVoice;

public partial class App : Application
{
    static Mutex? _mutex;

    /// <summary>Brand red as it should be sent to this display (matches the website in a browser).</summary>
    public static System.Windows.Media.Color Accent { get; private set; } = System.Windows.Media.Color.FromRgb(0xE1, 0x06, 0x00);

    void ApplyDisplayColors()
    {
        System.Windows.Media.Color Fix(string key, byte r, byte g, byte b)
        {
            var (R, G, B) = DisplayColor.ForDisplay(r, g, b);
            var c = System.Windows.Media.Color.FromRgb(R, G, B);
            if (Resources[key] is System.Windows.Media.SolidColorBrush br && !br.IsFrozen) br.Color = c;
            else Resources[key] = new System.Windows.Media.SolidColorBrush(c);
            return c;
        }
        Accent = Fix("Red", 0xE1, 0x06, 0x00);
        Fix("Tint", 0xFF, 0xF4, 0xF3);
        Resources["RedColor"] = Accent;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Software rendering: the UI is simple, and skipping the GPU path saves ~100 MB of RAM
        // and avoids flaky graphics drivers on older PCs. CASHVOICE_GPU=1 turns it back on.
        if (Environment.GetEnvironmentVariable("CASHVOICE_GPU") != "1")
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var args = e.Args;

        // Headless admin modes (used by the installer / the "Apply" button).
        if (args.Length >= 1 && args[0] == "--uninstall-apo")
        {
            int rc = 0;
            try { ApoInstaller.RestoreAll(); } catch { rc = 1; }
            Shutdown(rc);
            return;
        }
        if (args.Length >= 2 && args[0] == "--apply-endpoints")
        {
            int rc = 0;
            try
            {
                var guids = args[1] == "none"
                    ? Array.Empty<string>()
                    : args[1].Split(',', StringSplitOptions.RemoveEmptyEntries);
                ApoInstaller.Apply(guids);
            }
            catch { rc = 1; }
            Shutdown(rc);
            return;
        }

        if (args.Length >= 2 && args[0] == "--apply-update")
        {
            int rc;
            try { rc = Updater.ApplyUpdate(args[1]); } catch { rc = 1; }
            Shutdown(rc);
            return;
        }

        _mutex = new Mutex(true, AppPaths.ScreenshotMode ? "CashVoice.Screenshot" : "CashVoice.SingleInstance", out bool created);
        if (!created && args.Contains("--updated"))
        {
            // Relaunched by the updater: give the old copy a few seconds to exit.
            try { created = _mutex.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { created = true; }
        }
        if (!created) { Shutdown(); return; }

        ApplyDisplayColors();

        bool hidden = args.Contains("--minimized");
        var w = new MainWindow();
        MainWindow = w;
        if (!hidden) w.Show();
        else w.Dispatcher.BeginInvoke(async () => { await System.Threading.Tasks.Task.Delay(12000); if (!w.IsVisible) CashVoice.MainWindow.TrimMemory(); });
    }
}
