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
    public static readonly string Root =
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
    public string Display => string.IsNullOrEmpty(Hotkey) ? Name : $"{Name}   [{Hotkey}]";
}

public sealed class SoundItem : Notifier
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Sound";
    public string File { get; set; } = "";     // file name inside AppPaths.Sounds
    public double Volume { get; set; } = 1.0;
    public string Hotkey { get; set; } = "";

    [JsonIgnore]
    public string Display => string.IsNullOrEmpty(Hotkey) ? Name : $"{Name}   [{Hotkey}]";
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
    public ObservableCollection<VoicePreset> Voices { get; set; } = new(DefaultVoices());
    public ObservableCollection<SoundItem> Sounds { get; set; } = new();
    public Dictionary<string, string> GlobalHotkeys { get; set; } = DefaultHotkeys();

    public static Dictionary<string, string> DefaultHotkeys() => new()
    {
        ["toggle"] = "Ctrl+Alt+F1",
        ["next"]   = "Ctrl+Alt+F2",
        ["prev"]   = "Ctrl+Alt+F3",
        ["mute"]   = "Ctrl+Alt+F4",
        ["stop"]   = "Ctrl+Alt+F5",
    };

    public static List<VoicePreset> DefaultVoices() => new()
    {
        new() { Id = "normal",   Name = "Normal (no effect)" },
        new() { Id = "deep",     Name = "Deep",       Pitch = -5 },
        new() { Id = "giant",    Name = "Giant",      Pitch = -9, Drive = 0.1 },
        new() { Id = "chipmunk", Name = "Chipmunk",   Pitch = 7 },
        new() { Id = "squeaky",  Name = "Squeaky",    Pitch = 11 },
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
