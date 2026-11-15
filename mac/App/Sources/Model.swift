import Foundation
import Combine

// MARK: - Settings (saved as ~/Library/Application Support/CashVoice/config.json)

struct HotkeySpec: Codable, Equatable {
    var keyCode: UInt32
    var mods: UInt32          // Carbon modifier mask (cmdKey, optionKey, controlKey, shiftKey)
    var label: String         // e.g. "⌃⇧F6"
}

struct VoicePreset: Codable, Identifiable, Equatable {
    var id = UUID().uuidString
    var name = "Custom"
    var pitch = 0.0, formant = 0.0
    var ringHz = 50.0, ringMix = 0.0
    var echoMs = 250.0, echoFb = 0.4, echoMix = 0.0
    var hpHz = 0.0, lpHz = 0.0, drive = 0.0
    var hotkey: HotkeySpec? = nil

    static func defaults() -> [VoicePreset] {[
        VoicePreset(id: "normal", name: "Normal (no effect)"),
        VoicePreset(id: "deep", name: "Deep", pitch: -5, formant: -3),
        VoicePreset(id: "giant", name: "Giant", pitch: -9, formant: -5, drive: 0.1),
        VoicePreset(id: "chipmunk", name: "Chipmunk", pitch: 7, formant: 5),
        VoicePreset(id: "squeaky", name: "Squeaky", pitch: 11, formant: 7),
        VoicePreset(id: "robot", name: "Robot", ringHz: 55, ringMix: 0.85),
        VoicePreset(id: "alien", name: "Alien", pitch: 3, ringHz: 18, ringMix: 0.6),
        VoicePreset(id: "radio", name: "Radio / Megaphone", hpHz: 400, lpHz: 3200, drive: 0.25),
        VoicePreset(id: "cave", name: "Cave Echo", echoMs: 280, echoFb: 0.45, echoMix: 0.5),
    ]}
}

struct SoundItem: Codable, Identifiable, Equatable {
    var id = UUID().uuidString
    var name = "Sound"
    var file = ""                 // file name inside the Sounds folder
    var volume = 1.0              // gain factor
    var hotkey: HotkeySpec? = nil
}

struct Config: Codable, Equatable {
    var voiceEnabled = false
    var selectedVoiceId = "normal"
    var micMuted = false
    var soundsIntoMic = true
    var soundsOnSpeakers = true
    var micGain = 1.0
    var soundGain = 1.0
    var localVolume = 0.8
    var hearSelf = false
    var hearWet = true
    var hearVolume = 1.0
    var inputUID = ""                     // "" = system default
    var voices = VoicePreset.defaults()
    var sounds: [SoundItem] = []
    var globalHotkeys: [String: HotkeySpec] = Config.defaultHotkeys()

    static let actions: [HotkeyAction] = [
        HotkeyAction(key: "toggle", label: "Voice changer on / off"), HotkeyAction(key: "next", label: "Next voice"),
        HotkeyAction(key: "prev", label: "Previous voice"),
        HotkeyAction(key: "mute", label: "Mute / unmute microphone"), HotkeyAction(key: "stop", label: "Stop all sounds"),
    ]

    // ⌃⇧F6 … ⌃⇧F10, like Ctrl+Shift+F6 … F10 on Windows. (F-key codes: F6 97, F7 98, F8 100, F9 101, F10 109.)
    static func defaultHotkeys() -> [String: HotkeySpec] {
        let ctrlShift: UInt32 = 0x1000 | 0x0200   // controlKey | shiftKey
        return [
            "toggle": HotkeySpec(keyCode: 97, mods: ctrlShift, label: "⌃⇧F6"),
            "next": HotkeySpec(keyCode: 98, mods: ctrlShift, label: "⌃⇧F7"),
            "prev": HotkeySpec(keyCode: 100, mods: ctrlShift, label: "⌃⇧F8"),
            "mute": HotkeySpec(keyCode: 101, mods: ctrlShift, label: "⌃⇧F9"),
            "stop": HotkeySpec(keyCode: 109, mods: ctrlShift, label: "⌃⇧F10"),
        ]
    }

    // Tolerate older/newer files: missing keys fall back to defaults.
    init() {}
    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self)
        let def = Config()
        func v<T: Decodable>(_ k: CodingKeys, _ fallback: T) -> T { (try? c.decodeIfPresent(T.self, forKey: k)) ?? fallback }
        voiceEnabled = v(.voiceEnabled, def.voiceEnabled); selectedVoiceId = v(.selectedVoiceId, def.selectedVoiceId)
        micMuted = v(.micMuted, def.micMuted); soundsIntoMic = v(.soundsIntoMic, def.soundsIntoMic)
        soundsOnSpeakers = v(.soundsOnSpeakers, def.soundsOnSpeakers); micGain = v(.micGain, def.micGain)
        soundGain = v(.soundGain, def.soundGain); localVolume = v(.localVolume, def.localVolume)
        hearSelf = v(.hearSelf, def.hearSelf); hearWet = v(.hearWet, def.hearWet); hearVolume = v(.hearVolume, def.hearVolume)
        inputUID = v(.inputUID, def.inputUID); voices = v(.voices, def.voices); sounds = v(.sounds, def.sounds)
        globalHotkeys = v(.globalHotkeys, def.globalHotkeys)
        if voices.isEmpty { voices = VoicePreset.defaults() }
    }
}

enum Paths {
    static let root: URL = {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent("CashVoice", isDirectory: true)
    }()
    static var sounds: URL { root.appendingPathComponent("Sounds", isDirectory: true) }
    static var bin: URL { root.appendingPathComponent("bin", isDirectory: true) }
    static var config: URL { root.appendingPathComponent("config.json") }
    static func ensure() {
        for u in [root, sounds, bin] { try? FileManager.default.createDirectory(at: u, withIntermediateDirectories: true) }
    }
}

// MARK: - App state

final class Store: ObservableObject {
    @Published var cfg: Config { didSet { if cfg != oldValue { changed() } } }
    @Published var status = CVStatus()
    @Published var inputs: [InputDevice] = []
    @Published var micPermission = true

    private var saveWork: DispatchWorkItem?
    private var timer: Timer?
    var onHotkeysChanged: (() -> Void)?

    init() {
        Paths.ensure()
        if let data = try? Data(contentsOf: Paths.config), let c = try? JSONDecoder().decode(Config.self, from: data) {
            cfg = c
        } else {
            cfg = Config()
        }
        if !cfg.voices.contains(where: { $0.id == cfg.selectedVoiceId }) { cfg.selectedVoiceId = cfg.voices[0].id }
        cv_init()
        push()
        cv_set_input(cfg.inputUID)
        refreshInputs()
        timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in self?.tick() }
    }

    var selectedIndex: Int {
        get { cfg.voices.firstIndex(where: { $0.id == cfg.selectedVoiceId }) ?? 0 }
        set { if cfg.voices.indices.contains(newValue) { cfg.selectedVoiceId = cfg.voices[newValue].id } }
    }

    private func changed() {
        push()
        saveWork?.cancel()
        let w = DispatchWorkItem { [weak self] in self?.save() }
        saveWork = w
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.6, execute: w)
    }

    func save() {
        Paths.ensure()
        let enc = JSONEncoder(); enc.outputFormatting = [.prettyPrinted, .sortedKeys]
        if let data = try? enc.encode(cfg) { try? data.write(to: Paths.config, options: .atomic) }
    }

    /// Send the current settings to the audio core.
    func push() {
        let v = cfg.voices.indices.contains(selectedIndex) ? cfg.voices[selectedIndex] : VoicePreset()
        var p = CVParams()
        p.voiceOn = cfg.voiceEnabled ? 1 : 0
        p.micMute = cfg.micMuted ? 1 : 0
        p.soundOn = cfg.soundsIntoMic ? 1 : 0
        p.pitch = Float(v.pitch); p.formant = Float(v.formant)
        p.ringHz = Float(v.ringHz); p.ringMix = Float(v.ringMix)
        p.echoMs = Float(v.echoMs); p.echoFb = Float(v.echoFb); p.echoMix = Float(v.echoMix)
        p.hpHz = Float(v.hpHz); p.lpHz = Float(v.lpHz); p.drive = Float(v.drive)
        p.micGain = Float(cfg.micGain); p.soundGain = Float(cfg.soundGain)
        cv_set_params(&p)
        cv_set_local_volume(Float(cfg.localVolume))
        cv_set_monitor(cfg.hearSelf && micPermission ? 1 : 0, cfg.hearWet ? 1 : 0, Float(cfg.hearVolume))
    }

    func tick() {
        if micPermission { cv_tick() }
        var s = CVStatus()
        cv_get_status(&s)
        status = s
    }

    func refreshInputs() {
        var buf = [CChar](repeating: 0, count: 16384)
        cv_list_inputs(&buf, Int32(buf.count))
        let text = String(cString: buf)
        inputs = text.split(separator: "\n").compactMap { line in
            let parts = line.split(separator: "\t", maxSplits: 1).map(String.init)
            return parts.count == 2 ? InputDevice(uid: parts[0], name: parts[1]) : nil
        }
    }

    func setInput(_ uid: String) {
        cfg.inputUID = uid
        cv_set_input(uid)
    }

    // MARK: actions (menu, hotkeys)
    func toggleVoice() { cfg.voiceEnabled.toggle() }
    func toggleMute() { cfg.micMuted.toggle() }
    func useVoice(_ id: String) { cfg.selectedVoiceId = id; cfg.voiceEnabled = true }
    func cycleVoice(_ dir: Int) {
        let n = cfg.voices.count
        guard n > 0 else { return }
        useVoice(cfg.voices[(selectedIndex + dir + n) % n].id)
    }

    func play(_ s: SoundItem) {
        let url = Paths.sounds.appendingPathComponent(s.file)
        let mic = cfg.soundsIntoMic ? Float(s.volume) : 0
        let local = cfg.soundsOnSpeakers ? Float(s.volume) : 0
        DispatchQueue.global(qos: .userInitiated).async {
            guard let data = SoundLibrary.decode(url) else { return }
            data.withUnsafeBufferPointer { cv_play($0.baseAddress, Int32($0.count), mic, local) }
        }
    }
    func stopSounds() { cv_stop_sounds() }

    // Status line under the title.
    var statusText: String {
        if !status.driverInstalled.boolValue { return "The CashVoice Mic driver isn't installed. Reinstall CashVoice." }
        if !micPermission { return "CashVoice needs microphone access: System Settings › Privacy & Security › Microphone." }
        let err = withUnsafeBytes(of: status.error) { String(cString: $0.bindMemory(to: CChar.self).baseAddress!) }
        if !err.isEmpty { return err }
        if status.running.boolValue {
            let name = withUnsafeBytes(of: status.inputName) { String(cString: $0.bindMemory(to: CChar.self).baseAddress!) }
            return "Live on CashVoice Mic · from \(name)" + (cfg.micMuted ? "  ·  MIC MUTED" : "")
        }
        return "Ready. In Discord, OBS, Zoom etc. pick “CashVoice Mic” as the microphone."
    }
}

extension Int32 { var boolValue: Bool { self != 0 } }

struct InputDevice: Identifiable, Hashable { let uid: String; let name: String; var id: String { uid } }
struct HotkeyAction: Identifiable { let key: String; let label: String; var id: String { key } }
