using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CashVoice.Core;

public static class AppPaths
{
    /// <summary>
    /// Screenshot mode (for the website): CASHVOICE_SCREENSHOT_DATA=&lt;folder&gt; runs a separate copy
    /// with its own settings that never touches the audio driver, hotkeys or updates.
    /// </summary>
    public static readonly string? ScreenshotData =
        Environment.GetEnvironmentVariable("CASHVOICE_SCREENSHOT_DATA") is { Length: > 0 } d ? d : null;
    public static bool ScreenshotMode => ScreenshotData != null;

    public static readonly string Root = ScreenshotData ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CashVoice");
    public static string Sounds => Path.Combine(Root, "Sounds");
    public static string Bin => Path.Combine(Root, "bin");
    public static string ConfigFile => Path.Combine(Root, "config.json");
    public static void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Sounds);
        Directory.CreateDirectory(Bin);
    }
}

public abstract class Notifier : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Touch() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

public sealed class VoicePreset : Notifier
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Custom";
    public double Pitch { get; set; }          // semitones
    public double Formant { get; set; }        // semitones: + smaller/brighter, - bigger/darker
    public double RingHz { get; set; } = 50;
    public double RingMix { get; set; }
    public double EchoMs { get; set; } = 250;
    public double EchoFb { get; set; } = 0.4;
    public double EchoMix { get; set; }
    public double HpHz { get; set; }           // 0 = off
    public double LpHz { get; set; }           // 0 = off
    public double Drive { get; set; }
    public string Hotkey { get; set; } = "";

    [JsonIgnore]
    public string Display => string.IsNullOrEmpty(Hotkey) ? Name : $"{Name}   [{HotkeyLabel}]";
    [JsonIgnore]
    public string HotkeyLabel => CashVoice.Core.Hotkey.Pretty(Hotkey);
}

public sealed class SoundItem : Notifier
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Sound";
    public string File { get; set; } = "";     // file name inside AppPaths.Sounds
    public double Volume { get; set; } = 1.0;
    public string Hotkey { get; set; } = "";

    [JsonIgnore]
    public string Display => string.IsNullOrEmpty(Hotkey) ? Name : $"{Name}   [{HotkeyLabel}]";
    [JsonIgnore]
    public string HotkeyLabel => CashVoice.Core.Hotkey.Pretty(Hotkey);
}

public sealed class AppConfig
{
    public bool VoiceEnabled { get; set; }
    public string SelectedVoiceId { get; set; } = "normal";
    public bool MicMuted { get; set; }
    public bool SoundsIntoMic { get; set; } = true;
    public bool MonitorLocally { get; set; } = true;
    public double MicGain { get; set; } = 1.0;
    public double SoundGain { get; set; } = 1.0;
    public double LocalVolume { get; set; } = 0.8;
    public bool Autostart { get; set; }
    public bool AutoUpdate { get; set; } = true;
    public bool FormantsMigrated { get; set; }
    public bool HearSelf { get; set; }                 // "hear yourself" monitor on
    public bool HearWet { get; set; } = true;          // ...with the voice effect
    public double HearVolume { get; set; } = 1.0;
    public ObservableCollection<VoicePreset> Voices { get; set; } = new(DefaultVoices());
    public ObservableCollection<SoundItem> Sounds { get; set; } = new();
    public Dictionary<string, string> GlobalHotkeys { get; set; } = DefaultHotkeys();

    public static Dictionary<string, string> DefaultHotkeys() => new()
    {
        // Ctrl+Alt+F-keys are often taken by OEM/graphics utilities, so default to Ctrl+Shift+F6..F10.
        ["toggle"] = "Ctrl+Shift+F6",
        ["next"]   = "Ctrl+Shift+F7",
        ["prev"]   = "Ctrl+Shift+F8",
        ["mute"]   = "Ctrl+Shift+F9",
        ["stop"]   = "Ctrl+Shift+F10",
    };

    public static List<VoicePreset> DefaultVoices() => new()
    {
        new() { Id = "normal",   Name = "Normal (no effect)" },
        new() { Id = "deep",     Name = "Deep",       Pitch = -5, Formant = -3 },
        new() { Id = "giant",    Name = "Giant",      Pitch = -9, Formant = -5, Drive = 0.1 },
        new() { Id = "chipmunk", Name = "Chipmunk",   Pitch = 7, Formant = 5 },
        new() { Id = "squeaky",  Name = "Squeaky",    Pitch = 11, Formant = 7 },
        new() { Id = "robot",    Name = "Robot",      RingHz = 55, RingMix = 0.85 },
        new() { Id = "alien",    Name = "Alien",      Pitch = 3, RingHz = 18, RingMix = 0.6 },
        new() { Id = "radio",    Name = "Radio / Megaphone", HpHz = 400, LpHz = 3200, Drive = 0.25 },
        new() { Id = "cave",     Name = "Cave Echo",  EchoMs = 280, EchoFb = 0.45, EchoMix = 0.5 },
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(AppPaths.ConfigFile))
            {
                var c = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(AppPaths.ConfigFile));
                if (c != null) { c.Normalize(); return c; }
            }
        }
        catch { }
        return new AppConfig();
    }

    void Normalize()
    {
        // "demon" was a built-in voice in early builds; it's been retired.
        foreach (var v in Voices.Where(v => v.Id == "demon").ToList()) Voices.Remove(v);
        if (Voices.Count == 0) Voices = new(DefaultVoices());
        // 0.2.4 added formant shift: give built-in voices that are still exactly as shipped their new
        // formant setting (configs from before 0.2.4 have no "Formant" values at all).
        if (!FormantsMigrated)
        {
            FormantsMigrated = true;
            foreach (var d in DefaultVoices().Where(d => d.Formant != 0))
            {
                var v = Voices.FirstOrDefault(x => x.Id == d.Id);
                if (v != null && v.Formant == 0 && v.Pitch == d.Pitch && v.Drive == d.Drive) v.Formant = d.Formant;
            }
        }
        // Move anyone still on the old defaults (which clash on many PCs) to the new ones.
        string[] oldDefaults = { "Ctrl+Alt+F1", "Ctrl+Alt+F2", "Ctrl+Alt+F3", "Ctrl+Alt+F4", "Ctrl+Alt+F5" };
        string[] keys = { "toggle", "next", "prev", "mute", "stop" };
        if (keys.Select((k, i) => GlobalHotkeys.TryGetValue(k, out var v) && v == oldDefaults[i]).All(x => x))
            GlobalHotkeys = DefaultHotkeys();
        foreach (var kv in DefaultHotkeys())
            if (!GlobalHotkeys.ContainsKey(kv.Key)) GlobalHotkeys[kv.Key] = kv.Value;
        if (!Voices.Any(v => v.Id == SelectedVoiceId)) SelectedVoiceId = Voices[0].Id;
    }

    public void Save()
    {
        try
        {
            AppPaths.Ensure();
            File.WriteAllText(AppPaths.ConfigFile,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
