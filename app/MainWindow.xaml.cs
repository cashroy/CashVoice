using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using CashVoice.Controls;
using CashVoice.Core;

namespace CashVoice;

public partial class MainWindow : Window
{
    readonly AppConfig _cfg;
    readonly SharedMem _shm = new();
    readonly SoundEngine _snd;
    readonly HotkeyManager _hk;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly List<Action> _syncers = new();
    readonly Dictionary<string, HotkeyBox> _globalBoxes = new();

    System.Windows.Forms.NotifyIcon? _tray;
    System.Windows.Forms.ToolStripMenuItem? _trayToggle;
    TextBox? _voiceName;
    HotkeyBox? _voiceHotkey;
    VoicePreset? _editing;
    bool _loading, _dirty, _exiting, _shownTrayTip;
    int _preloadedRate;

    static readonly (string key, string label)[] GlobalActions =
    {
        ("toggle", "Voice changer on / off"),
        ("next",   "Next voice"),
        ("prev",   "Previous voice"),
        ("mute",   "Mute / unmute microphone"),
        ("stop",   "Stop all sounds"),
    };

    public MainWindow()
    {
        _loading = true;                 // XAML init fires ValueChanged/Click handlers before fields exist
        InitializeComponent();
        _loading = false;
        AppPaths.Ensure();
        _cfg = AppConfig.Load();
        _snd = new SoundEngine(_shm);
        _hk = new HotkeyManager(this);

        BuildVoiceEditor();
        BuildSettings();

        VoiceList.ItemsSource = _cfg.Voices;
        VoiceList.SelectedItem = _cfg.Voices.FirstOrDefault(v => v.Id == _cfg.SelectedVoiceId) ?? _cfg.Voices[0];
        SoundList.ItemsSource = _cfg.Sounds;

        SetupTray();
        RebuildHotkeys();
        RefreshToggle();

        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    // =====================================================================================
    //  Voice tab
    // =====================================================================================
    void BuildVoiceEditor()
    {
        VoiceEditor.Children.Clear();

        VoiceEditor.Children.Add(new TextBlock { Text = "Name", FontWeight = FontWeights.SemiBold });
        _voiceName = new TextBox { Margin = new Thickness(0, 2, 0, 10) };
        _voiceName.LostFocus += (_, _) =>
        {
            if (_editing == null || _loading) return;
            _editing.Name = _voiceName.Text; _editing.Touch(); MarkDirty();
        };
        VoiceEditor.Children.Add(_voiceName);

        VoiceEditor.Children.Add(new TextBlock { Text = "Hotkey (selects this voice and turns the changer on)", FontWeight = FontWeights.SemiBold });
        _voiceHotkey = new HotkeyBox { Margin = new Thickness(0, 2, 0, 14), HorizontalAlignment = HorizontalAlignment.Left };
        _voiceHotkey.ValueChanged += (_, _) =>
        {
            if (_editing == null || _loading) return;
            _editing.Hotkey = _voiceHotkey.Value; _editing.Touch(); RebuildHotkeys(); MarkDirty();
        };
        VoiceEditor.Children.Add(_voiceHotkey);

        AddSlider("Pitch (semitones)", -24, 24, 0.5, v => v.Pitch, (v, x) => v.Pitch = x);
        AddSlider("Robot / ring-mod mix", 0, 1, 0.05, v => v.RingMix, (v, x) => v.RingMix = x);
        AddSlider("Ring-mod frequency (Hz)", 5, 400, 1, v => v.RingHz, (v, x) => v.RingHz = x);
        AddSlider("Echo mix", 0, 1, 0.05, v => v.EchoMix, (v, x) => v.EchoMix = x);
        AddSlider("Echo delay (ms)", 30, 800, 10, v => v.EchoMs, (v, x) => v.EchoMs = x);
        AddSlider("Echo feedback", 0, 0.85, 0.05, v => v.EchoFb, (v, x) => v.EchoFb = x);
        AddSlider("Low cut / high-pass (Hz, 0 = off)", 0, 1000, 10, v => v.HpHz, (v, x) => v.HpHz = x);
        AddSlider("High cut / low-pass (Hz, 0 = off)", 0, 12000, 100, v => v.LpHz, (v, x) => v.LpHz = x);
        AddSlider("Distortion", 0, 1, 0.05, v => v.Drive, (v, x) => v.Drive = x);
    }

    void AddSlider(string label, double min, double max, double step,
                   Func<VoicePreset, double> get, Action<VoicePreset, double> set)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });

        var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        var sl = new Slider
        {
            Minimum = min, Maximum = max, SmallChange = step, LargeChange = step * 5,
            TickFrequency = step, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center,
        };
        var val = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(sl, 1); Grid.SetColumn(val, 2);
        grid.Children.Add(lbl); grid.Children.Add(sl); grid.Children.Add(val);

        sl.ValueChanged += (_, _) =>
        {
            val.Text = sl.Value.ToString("0.##");
            if (_loading || _editing == null) return;
            set(_editing, sl.Value);
            PushParams(); MarkDirty();
        };
        _syncers.Add(() => { sl.Value = get(_editing!); val.Text = sl.Value.ToString("0.##"); });
        VoiceEditor.Children.Add(grid);
    }

    void VoiceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VoiceList.SelectedItem is not VoicePreset v) return;
        _editing = v;
        _cfg.SelectedVoiceId = v.Id;
        _loading = true;
        try
        {
            _voiceName!.Text = v.Name;
            _voiceHotkey!.Value = v.Hotkey;
            foreach (var s in _syncers) s();
        }
        finally { _loading = false; }
        PushParams(); MarkDirty();
    }

    void AddVoice_Click(object sender, RoutedEventArgs e)
    {
        var copy = _editing ?? _cfg.Voices[0];
        var v = new VoicePreset
        {
            Name = "My voice " + (_cfg.Voices.Count + 1),
            Pitch = copy.Pitch, RingHz = copy.RingHz, RingMix = copy.RingMix, EchoMs = copy.EchoMs,
            EchoFb = copy.EchoFb, EchoMix = copy.EchoMix, HpHz = copy.HpHz, LpHz = copy.LpHz, Drive = copy.Drive,
        };
        _cfg.Voices.Add(v);
        VoiceList.SelectedItem = v;
        MarkDirty();
    }

    void DeleteVoice_Click(object sender, RoutedEventArgs e)
    {
        if (_editing == null || _cfg.Voices.Count <= 1) return;
        var idx = _cfg.Voices.IndexOf(_editing);
        _cfg.Voices.Remove(_editing);
        VoiceList.SelectedItem = _cfg.Voices[Math.Max(0, idx - 1)];
        RebuildHotkeys(); MarkDirty();
    }

    void ResetVoices_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Reset all voices to the built-in defaults? Your custom voices will be removed.",
                "CashVoice", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _cfg.Voices.Clear();
        foreach (var v in AppConfig.DefaultVoices()) _cfg.Voices.Add(v);
        VoiceList.SelectedItem = _cfg.Voices[0];
        RebuildHotkeys(); MarkDirty();
    }

    // =====================================================================================
    //  Soundboard tab
    // =====================================================================================
    void SoundList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var s = SoundList.SelectedItem as SoundItem;
        SoundDetails.IsEnabled = s != null;
        if (s == null) return;
        _loading = true;
        try
        {
            SoundName.Text = s.Name;
            SoundVolume.Value = s.Volume;
            SoundHotkey.Value = s.Hotkey;
        }
        finally { _loading = false; }
        SoundHotkey.ValueChanged -= SoundHotkey_Changed;
        SoundHotkey.ValueChanged += SoundHotkey_Changed;
    }

    void SoundHotkey_Changed(object? sender, EventArgs e)
    {
        if (_loading || SoundList.SelectedItem is not SoundItem s) return;
        s.Hotkey = SoundHotkey.Value; s.Touch(); RebuildHotkeys(); MarkDirty();
    }

    void SoundName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || SoundList.SelectedItem is not SoundItem s) return;
        s.Name = SoundName.Text; s.Touch(); MarkDirty();
    }

    void SoundVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || SoundList.SelectedItem is not SoundItem s) return;
        s.Volume = SoundVolume.Value; MarkDirty();
    }

    void PlaySound_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SoundItem s) PlaySound(s);
    }

    void DeleteSound_Click(object sender, RoutedEventArgs e)
    {
        if (SoundList.SelectedItem is not SoundItem s) return;
        if (MessageBox.Show($"Delete \"{s.Name}\"?", "CashVoice", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _cfg.Sounds.Remove(s);
        try { File.Delete(Path.Combine(AppPaths.Sounds, s.File)); } catch { }
        RebuildHotkeys(); MarkDirty();
    }

    void PlaySound(SoundItem s)
    {
        string path = Path.Combine(AppPaths.Sounds, s.File);
        if (!File.Exists(path)) { ImportStatus.Text = $"File missing for \"{s.Name}\"."; return; }

        if (_cfg.MonitorLocally) _snd.PlayLocal(path, (float)(_cfg.LocalVolume * s.Volume));
        if (_cfg.SoundsIntoMic && _shm.Connected)
        {
            int sr = _snd.SampleRate; float gain = (float)s.Volume;
            Task.Run(() =>
            {
                try { _snd.Play(SoundEngine.GetOrDecode(path, sr), gain); } catch { }
            });
        }
    }

    void ImportFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Audio files|*.mp3;*.wav;*.m4a;*.aac;*.wma;*.flac;*.aiff;*.aif|All files|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog() == true) foreach (var f in dlg.FileNames) AddSoundFromFile(f);
    }

    void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            Tabs.SelectedIndex = 1;
            foreach (var f in files) AddSoundFromFile(f);
        }
    }

    SoundItem? AddSoundFromFile(string src)
    {
        try
        {
            AppPaths.Ensure();
            string ext = Path.GetExtension(src);
            string baseName = Path.GetFileNameWithoutExtension(src);
            string dest = Path.Combine(AppPaths.Sounds, baseName + ext);
            int n = 1;
            while (File.Exists(dest)) dest = Path.Combine(AppPaths.Sounds, $"{baseName} ({n++}){ext}");
            File.Copy(src, dest);
            var item = new SoundItem { Name = baseName, File = Path.GetFileName(dest) };
            _cfg.Sounds.Add(item);
            SoundList.SelectedItem = item;
            MarkDirty();
            ImportStatus.Text = $"Added \"{item.Name}\".";
            return item;
        }
        catch (Exception ex) { ImportStatus.Text = "Couldn't import: " + ex.Message; return null; }
    }

    async void Download_Click(object sender, RoutedEventArgs e)
    {
        string url = UrlBox.Text.Trim();
        if (url.Length == 0) return;
        DownloadBtn.IsEnabled = false;
        var log = new Progress<string>(m => ImportStatus.Text = m);
        try
        {
            string file = await Tools.DownloadAudioAsync(url, AppPaths.Sounds, log);
            var item = new SoundItem { Name = Tools.CleanTitle(file), File = Path.GetFileName(file) };
            _cfg.Sounds.Add(item);
            SoundList.SelectedItem = item;
            UrlBox.Clear();
            MarkDirty();
            ImportStatus.Text = $"Added \"{item.Name}\".";
        }
        catch (Exception ex) { ImportStatus.Text = ex.Message; }
        finally { DownloadBtn.IsEnabled = true; }
    }

    // =====================================================================================
    //  Settings tab
    // =====================================================================================
    void BuildSettings()
    {
        _loading = true;
        ChkSoundsIntoMic.IsChecked = _cfg.SoundsIntoMic;
        ChkMonitor.IsChecked = _cfg.MonitorLocally;
        ChkAutostart.IsChecked = _cfg.Autostart;
        SldMic.Value = _cfg.MicGain;
        SldSound.Value = _cfg.SoundGain;
        SldLocal.Value = _cfg.LocalVolume;
        _loading = false;

        foreach (var (key, label) in GlobalActions)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tb = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            var box = new HotkeyBox { Value = _cfg.GlobalHotkeys.TryGetValue(key, out var hk) ? hk : "" };
            string k = key;
            box.ValueChanged += (_, _) => { _cfg.GlobalHotkeys[k] = box.Value; RebuildHotkeys(); MarkDirty(); };
            Grid.SetColumn(box, 1);
            row.Children.Add(tb); row.Children.Add(box);
            GlobalHotkeys.Children.Add(row);
            _globalBoxes[key] = box;
        }
        RefreshMics();
    }

    void RefreshMics()
    {
        MicList.Children.Clear();
        try
        {
            var enabled = ApoInstaller.EnabledEndpoints();
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                string guid = ApoInstaller.EndpointGuid(d.ID);
                MicList.Children.Add(new CheckBox
                {
                    Content = d.FriendlyName, Tag = guid, Margin = new Thickness(0, 2, 0, 2),
                    IsChecked = enabled.Contains(guid, StringComparer.OrdinalIgnoreCase),
                });
            }
            if (MicList.Children.Count == 0)
                MicList.Children.Add(new TextBlock { Text = "No active microphones found." });
        }
        catch (Exception ex) { MicList.Children.Add(new TextBlock { Text = "Couldn't list microphones: " + ex.Message }); }
    }

    void RefreshMics_Click(object sender, RoutedEventArgs e) => RefreshMics();

    void ApplyMics_Click(object sender, RoutedEventArgs e)
    {
        var chosen = MicList.Children.OfType<CheckBox>()
            .Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
        bool ok = ApoInstaller.ApplyElevated(chosen);
        RefreshMics();
        MessageBox.Show(ok
            ? "Done. Windows audio was restarted. Restart apps that were already using the mic (Discord, OBS, games…) so they pick up the effect."
            : "That didn't complete (admin permission was declined, or Windows blocked the change).",
            "CashVoice", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    void Settings_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _cfg.SoundsIntoMic = ChkSoundsIntoMic.IsChecked == true;
        _cfg.MonitorLocally = ChkMonitor.IsChecked == true;
        bool auto = ChkAutostart.IsChecked == true;
        if (auto != _cfg.Autostart) { _cfg.Autostart = auto; SetAutostart(auto); }
        PushParams(); MarkDirty();
    }

    void Settings_Slider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _cfg.MicGain = SldMic.Value;
        _cfg.SoundGain = SldSound.Value;
        _cfg.LocalVolume = SldLocal.Value;
        PushParams(); MarkDirty();
    }

    static void SetAutostart(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (k == null) return;
            if (on) k.SetValue("CashVoice", $"\"{Environment.ProcessPath}\" --minimized");
            else k.DeleteValue("CashVoice", false);
        }
        catch { }
    }

    async void UpdateYtDlp_Click(object sender, RoutedEventArgs e)
    {
        try { await Tools.UpdateYtDlpAsync(); MessageBox.Show("yt-dlp update finished."); }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
    }

    // =====================================================================================
    //  Hotkeys + actions
    // =====================================================================================
    void RebuildHotkeys()
    {
        _hk.Clear();
        var failed = new List<string>();

        void Add(string? text, string label, Action action)
        {
            var hk = Hotkey.Parse(text);
            if (hk.IsEmpty) return;
            if (!_hk.Register(hk, action)) failed.Add($"{label} ({text})");
        }

        string G(string k) => _cfg.GlobalHotkeys.TryGetValue(k, out var v) ? v : "";
        Add(G("toggle"), "Voice on/off", ToggleVoice);
        Add(G("next"), "Next voice", () => CycleVoice(+1));
        Add(G("prev"), "Previous voice", () => CycleVoice(-1));
        Add(G("mute"), "Mute mic", ToggleMute);
        Add(G("stop"), "Stop sounds", _snd.StopAll);
        foreach (var v in _cfg.Voices.ToList()) { var vv = v; Add(vv.Hotkey, vv.Name, () => UseVoice(vv)); }
        foreach (var s in _cfg.Sounds.ToList()) { var ss = s; Add(ss.Hotkey, ss.Name, () => PlaySound(ss)); }

        HotkeyStatus.Text = failed.Count == 0
            ? ""
            : "These hotkeys couldn't be registered (probably used by another app or duplicated): " + string.Join(", ", failed);
    }

    void ToggleVoice()
    {
        _cfg.VoiceEnabled = !_cfg.VoiceEnabled;
        PushParams(); RefreshToggle(); MarkDirty();
    }

    void ToggleMute()
    {
        _cfg.MicMuted = !_cfg.MicMuted;
        PushParams(); RefreshToggle(); MarkDirty();
    }

    void UseVoice(VoicePreset v)
    {
        _cfg.VoiceEnabled = true;
        VoiceList.SelectedItem = v;
        PushParams(); RefreshToggle(); MarkDirty();
    }

    void CycleVoice(int dir)
    {
        int n = _cfg.Voices.Count;
        if (n == 0) return;
        int i = _editing == null ? 0 : _cfg.Voices.IndexOf(_editing);
        UseVoice(_cfg.Voices[(i + dir + n) % n]);
    }

    void VoiceToggle_Click(object sender, RoutedEventArgs e)
    {
        _cfg.VoiceEnabled = VoiceToggle.IsChecked == true;
        PushParams(); RefreshToggle(); MarkDirty();
    }

    void RefreshToggle()
    {
        VoiceToggle.IsChecked = _cfg.VoiceEnabled;
        VoiceToggle.Content = _cfg.VoiceEnabled ? "Voice: ON" : "Voice: OFF";
        if (_trayToggle != null) _trayToggle.Checked = _cfg.VoiceEnabled;
        if (_tray != null)
            _tray.Text = "CashVoice — " + (_cfg.VoiceEnabled ? "voice ON" : "voice off") + (_cfg.MicMuted ? " (muted)" : "");
    }

    // =====================================================================================
    //  Plumbing
    // =====================================================================================
    void PushParams()
    {
        if (_cfg == null) return;
        var v = _editing ?? _cfg.Voices.FirstOrDefault();
        if (v != null) _shm.Push(_cfg, v);
    }

    void MarkDirty() => _dirty = true;

    void Tick()
    {
        if (!_shm.Connected && _shm.TryConnect()) PushParams();
        else if (_shm.Connected) PushParams();     // heartbeat: keeps the APO in sync if audio service restarted

        string status;
        if (!_shm.Connected)
            status = "Driver not running yet. Go to Settings → tick your microphone → Apply, then restart apps that use the mic.";
        else if (_shm.ApoInstances == 0)
            status = "Driver loaded — waiting for an app to start using the microphone.";
        else
            status = $"Mic effect active in {_shm.ApoInstances} stream(s) at {_shm.ApoSampleRate} Hz.";
        if (_cfg.MicMuted) status += "  ·  MIC MUTED";
        StatusText.Text = status;

        if (_shm.Connected && _preloadedRate != _snd.SampleRate)
        {
            _preloadedRate = _snd.SampleRate;
            int sr = _preloadedRate;
            var files = _cfg.Sounds.Select(s => Path.Combine(AppPaths.Sounds, s.File)).Where(File.Exists).ToList();
            Task.Run(() => { foreach (var f in files) { try { SoundEngine.GetOrDecode(f, sr); } catch { } } });
        }

        if (_dirty) { _dirty = false; _cfg.Save(); }
    }

    void SetupTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open CashVoice", null, (_, _) => ShowMain());
        _trayToggle = new System.Windows.Forms.ToolStripMenuItem("Voice changer on") { CheckOnClick = false };
        _trayToggle.Click += (_, _) => Dispatcher.Invoke(ToggleVoice);
        menu.Items.Add(_trayToggle);
        menu.Items.Add("Stop all sounds", null, (_, _) => Dispatcher.Invoke(_snd.StopAll));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitApp));

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "CashVoice",
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowMain();
    }

    void ShowMain()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            Hide();
            if (!_shownTrayTip)
            {
                _shownTrayTip = true;
                _tray?.ShowBalloonTip(2000, "CashVoice", "Still running in the tray — hotkeys stay active.",
                    System.Windows.Forms.ToolTipIcon.Info);
            }
            return;
        }
        base.OnClosing(e);
    }

    void ExitApp()
    {
        _exiting = true;
        _cfg.Save();
        _hk.Dispose();
        _snd.Dispose();
        _shm.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        Application.Current.Shutdown();
    }
}
