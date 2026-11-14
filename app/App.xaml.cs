using System;
using System.Linq;
using System.Threading;
using System.Windows;
using CashVoice.Core;

namespace CashVoice;

public partial class App : Application
{
    static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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

        _mutex = new Mutex(true, "CashVoice.SingleInstance", out bool created);
        if (!created) { Shutdown(); return; }

        bool hidden = args.Contains("--minimized");
        var w = new MainWindow();
        MainWindow = w;
        if (!hidden) w.Show();
    }
}
