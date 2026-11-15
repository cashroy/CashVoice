import SwiftUI
import AVFoundation
import UniformTypeIdentifiers
import ServiceManagement

extension Color {
    static let cvRed = Color(red: 225 / 255, green: 6 / 255, blue: 0)
    static let cvInk = Color(red: 11 / 255, green: 11 / 255, blue: 11 / 255)
}

// MARK: - Main window

enum Tab: String, CaseIterable { case voice = "Voice", soundboard = "Soundboard", settings = "Settings" }

struct MainView: View {
    @EnvironmentObject var store: Store
    @State private var tab: Tab = .voice

    var body: some View {
        VStack(spacing: 0) {
            Header()
            HStack(spacing: 28) {
                ForEach(Tab.allCases, id: \.self) { t in
                    Button { tab = t } label: {
                        VStack(spacing: 8) {
                            Text(t.rawValue).font(cvFont(15, .bold))
                                .foregroundColor(tab == t ? .cvInk : .secondary)
                            Rectangle().fill(tab == t ? Color.cvRed : .clear).frame(height: 3)
                        }.fixedSize()
                    }.buttonStyle(.plain)
                }
                Spacer()
            }
            .padding(.horizontal, 32).padding(.top, 16)
            Divider()
            Group {
                switch tab {
                case .voice: VoiceTab()
                case .soundboard: SoundTab()
                case .settings: SettingsTab()
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .frame(minWidth: 860, minHeight: 640)
        .font(cvFont(14))
        .background(Color(nsColor: .windowBackgroundColor))
    }
}

struct Header: View {
    @EnvironmentObject var store: Store
    var body: some View {
        HStack(alignment: .center) {
            VStack(alignment: .leading, spacing: 4) {
                (Text("CashVoice").foregroundColor(.white) + Text(".").foregroundColor(.cvInk))
                    .font(cvFont(36, .black)).kerning(-1.2)
                Text(store.statusText).font(cvFont(13, .medium)).foregroundColor(.white.opacity(0.9))
                    .lineLimit(2)
            }
            Spacer()
            Button { store.toggleVoice() } label: {
                HStack(spacing: 8) {
                    Circle().fill(store.cfg.voiceEnabled ? Color.cvRed : Color.secondary).frame(width: 9, height: 9)
                    Text(store.cfg.voiceEnabled ? "Voice on" : "Voice off").font(cvFont(15, .bold))
                        .foregroundColor(.cvRed)
                }
                .padding(.horizontal, 26).frame(height: 44)
                .background(Capsule().fill(Color.white))
            }.buttonStyle(.plain)
        }
        .padding(.horizontal, 32).padding(.vertical, 22)
        .background(Color.cvRed)
    }
}

struct Eyebrow: View {
    let text: String
    var body: some View {
        Text(text.uppercased()).font(cvFont(11, .bold)).foregroundColor(.cvRed).kerning(0.4)
    }
}

// MARK: - Voice tab

struct VoiceTab: View {
    @EnvironmentObject var store: Store
    @State private var dragging: String?

    var body: some View {
        HStack(alignment: .top, spacing: 28) {
            VStack(alignment: .leading, spacing: 8) {
                Eyebrow(text: "Voices")
                List {
                    ForEach(store.cfg.voices) { v in
                        HStack {
                            Text(v.name).fontWeight(v.id == store.cfg.selectedVoiceId ? .bold : .regular)
                            Spacer()
                            if let hk = v.hotkey { KeyTag(text: hk.label) }
                        }
                        .contentShape(Rectangle())
                        .onTapGesture { store.cfg.selectedVoiceId = v.id }
                        .listRowBackground(v.id == store.cfg.selectedVoiceId ? Color.cvRed.opacity(0.14) : Color.clear)
                    }
                    .onMove { from, to in store.cfg.voices.move(fromOffsets: from, toOffset: to) }   // drag to reorder
                }
                .listStyle(.plain)
                HStack {
                    Button("+ New") {
                        var v = store.cfg.voices[store.selectedIndex]
                        v.id = UUID().uuidString; v.name = "My voice \(store.cfg.voices.count + 1)"; v.hotkey = nil
                        store.cfg.voices.append(v); store.cfg.selectedVoiceId = v.id
                    }
                    Button("Delete") {
                        guard store.cfg.voices.count > 1 else { return }
                        store.cfg.voices.remove(at: store.selectedIndex)
                        store.cfg.selectedVoiceId = store.cfg.voices[0].id
                        store.onHotkeysChanged?()
                    }
                    Button("Reset") {
                        store.cfg.voices = VoicePreset.defaults(); store.cfg.selectedVoiceId = "normal"
                        store.onHotkeysChanged?()
                    }
                }
            }
            .frame(width: 240)

            ScrollView {
                VoiceEditor(index: store.selectedIndex).padding(.trailing, 8)
            }
        }
        .padding(.horizontal, 32).padding(.vertical, 20)
    }
}

struct VoiceEditor: View {
    @EnvironmentObject var store: Store
    let index: Int

    var body: some View {
        let v = Binding(get: { store.cfg.voices[min(index, store.cfg.voices.count - 1)] },
                        set: { store.cfg.voices[min(index, store.cfg.voices.count - 1)] = $0 })
        VStack(alignment: .leading, spacing: 10) {
            Eyebrow(text: "Name")
            TextField("Name", text: v.name).textFieldStyle(.roundedBorder).font(cvFont(14))
            Eyebrow(text: "Hotkey · picks this voice and turns it on").padding(.top, 8)
            HotkeyField(value: Binding(get: { v.wrappedValue.hotkey },
                                       set: { v.wrappedValue.hotkey = $0; store.onHotkeysChanged?() }))
                .frame(width: 200)
            Eyebrow(text: "Sound").padding(.top, 12)
            SliderRow(label: "Pitch (semitones)", value: v.pitch, range: -12...12, step: 0.5)
            SliderRow(label: "Formant (semitones)", value: v.formant, range: -12...12, step: 0.5)
            SliderRow(label: "Robot / ring-mod mix", value: v.ringMix, range: 0...1, step: 0.05)
            SliderRow(label: "Ring-mod frequency (Hz)", value: v.ringHz, range: 5...400, step: 1)
            SliderRow(label: "Echo mix", value: v.echoMix, range: 0...1, step: 0.05)
            SliderRow(label: "Echo delay (ms)", value: v.echoMs, range: 30...800, step: 10)
            SliderRow(label: "Echo feedback", value: v.echoFb, range: 0...0.85, step: 0.05)
            SliderRow(label: "Low cut (Hz, 0 = off)", value: v.hpHz, range: 0...1000, step: 10)
            SliderRow(label: "High cut (Hz, 0 = off)", value: v.lpHz, range: 0...12000, step: 100)
            SliderRow(label: "Distortion", value: v.drive, range: 0...1, step: 0.05)
        }
    }
}

struct SliderRow: View {
    let label: String
    @Binding var value: Double
    let range: ClosedRange<Double>
    let step: Double
    var format: (Double) -> String = { v in v == v.rounded() ? String(Int(v)) : String(format: "%.2g", v) }

    var body: some View {
        HStack {
            Text(label).frame(width: 200, alignment: .leading)
            Slider(value: $value, in: range, step: step).tint(.cvRed)
            Text(format(value)).font(cvFont(13, .bold)).frame(width: 64, alignment: .trailing)
        }
    }
}

struct KeyTag: View {
    let text: String
    var body: some View {
        Text(text).font(cvFont(11, .semibold))
            .padding(.horizontal, 7).padding(.vertical, 2)
            .overlay(Capsule().stroke(Color.primary.opacity(0.6), lineWidth: 1.2))
    }
}

/// Click, then press a key combo. Delete/Backspace/Esc clears it.
struct HotkeyField: View {
    @Binding var value: HotkeySpec?
    @State private var recording = false
    @State private var monitor: Any?

    var body: some View {
        Button {
            recording ? stop() : start()
        } label: {
            Text(recording ? "Press keys…" : (value?.label ?? "None"))
                .frame(maxWidth: .infinity, minHeight: 28)
                .font(cvFont(13, .semibold))
        }
        .buttonStyle(.bordered)
        .help("Click, then press a key combo. Delete clears it.")
    }

    private func start() {
        recording = true
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { e in
            let plain = e.modifierFlags.intersection(.deviceIndependentFlagsMask).isEmpty
            if plain && [51, 117, 53].contains(e.keyCode) { value = nil; stop(); return nil }
            if let s = KeyNames.spec(from: e) { value = s; stop() }
            return nil
        }
    }
    private func stop() {
        recording = false
        if let m = monitor { NSEvent.removeMonitor(m); monitor = nil }
    }
}

// MARK: - Soundboard tab

struct SoundTab: View {
    @EnvironmentObject var store: Store
    @State private var link = ""
    @State private var status = "Paste a YouTube link, import a file, or drop audio files here."
    @State private var busy = false
    @State private var selected: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Eyebrow(text: "Add a sound")
            HStack {
                TextField("YouTube link", text: $link).textFieldStyle(.roundedBorder)
                Button("Download") { download() }.disabled(busy || link.isEmpty)
                Button("Import file") { importFiles() }
            }
            Text(status).font(cvFont(12)).foregroundColor(.secondary)

            HStack(alignment: .top, spacing: 28) {
                VStack(alignment: .leading) {
                    Eyebrow(text: "Sounds")
                    List(selection: $selected) {
                        ForEach(store.cfg.sounds) { s in
                            HStack(spacing: 12) {
                                Button { store.play(s) } label: { Image(systemName: "play.fill") }.buttonStyle(.borderless)
                                Text(s.name)
                                Spacer()
                                if let hk = s.hotkey { KeyTag(text: hk.label) }
                            }.tag(s.id)
                        }
                        .onMove { from, to in store.cfg.sounds.move(fromOffsets: from, toOffset: to) }
                    }
                    .listStyle(.plain)
                    HStack {
                        Button("Stop all") { store.stopSounds() }
                    }
                }
                if let i = store.cfg.sounds.firstIndex(where: { $0.id == selected }) {
                    SoundDetails(index: i).frame(width: 260)
                }
            }
        }
        .padding(.horizontal, 32).padding(.vertical, 20)
        .onDrop(of: [.fileURL], isTargeted: nil) { providers in
            for p in providers {
                _ = p.loadObject(ofClass: URL.self) { url, _ in
                    if let url { DispatchQueue.main.async { add(url) } }
                }
            }
            return true
        }
    }

    private func add(_ url: URL) {
        do {
            let item = try SoundLibrary.importFile(url)
            store.cfg.sounds.append(item); selected = item.id
            status = "Added “\(item.name)”."
        } catch { status = error.localizedDescription }
    }

    private func importFiles() {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = true
        panel.allowedContentTypes = [.audio]
        if panel.runModal() == .OK { panel.urls.forEach(add) }
    }

    private func download() {
        busy = true
        let l = link
        Task {
            do {
                let item = try await SoundLibrary.importYouTube(l) { msg in DispatchQueue.main.async { status = msg } }
                await MainActor.run {
                    store.cfg.sounds.append(item); selected = item.id
                    status = "Added “\(item.name)”."; link = ""; busy = false
                }
            } catch {
                await MainActor.run { status = error.localizedDescription; busy = false }
            }
        }
    }
}

struct SoundDetails: View {
    @EnvironmentObject var store: Store
    let index: Int
    var body: some View {
        let s = Binding(get: { store.cfg.sounds[index] }, set: { store.cfg.sounds[index] = $0 })
        VStack(alignment: .leading, spacing: 10) {
            Eyebrow(text: "Name")
            TextField("Name", text: s.name).textFieldStyle(.roundedBorder)
            Eyebrow(text: "Volume").padding(.top, 8)
            DbSlider(gain: s.volume, min: -24, max: 12)
            Eyebrow(text: "Hotkey").padding(.top, 8)
            HotkeyField(value: Binding(get: { s.wrappedValue.hotkey },
                                       set: { s.wrappedValue.hotkey = $0; store.onHotkeysChanged?() }))
            Button("Delete sound") {
                let item = store.cfg.sounds.remove(at: index)
                try? FileManager.default.removeItem(at: Paths.sounds.appendingPathComponent(item.file))
                store.onHotkeysChanged?()
            }.padding(.top, 12)
        }
    }
}

/// A slider in decibels over a plain gain factor; the bottom means off.
struct DbSlider: View {
    @Binding var gain: Double
    let min: Double, max: Double
    var body: some View {
        let db = Binding<Double>(
            get: { gain <= 0 ? min : Swift.min(Swift.max(20 * log10(gain), min), max) },
            set: { gain = $0 <= min + 0.01 ? 0 : pow(10, $0 / 20) })
        HStack {
            Slider(value: db, in: min...max, step: 0.5).tint(.cvRed)
            Text(db.wrappedValue <= min + 0.01 ? "off" : String(format: "%+.1f dB", db.wrappedValue))
                .font(cvFont(13, .bold)).frame(width: 70, alignment: .trailing)
        }
    }
}

// MARK: - Settings tab

struct SettingsTab: View {
    @EnvironmentObject var store: Store
    @State private var loginItem = SMAppService.mainApp.status == .enabled
    @State private var loginError = ""

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 12) {
                Eyebrow(text: "Microphone")
                Text("Your real microphone").font(cvFont(22, .black))
                Text("CashVoice listens to this mic and sends your changed voice to **CashVoice Mic**. In Discord, OBS, Zoom, games etc. choose **CashVoice Mic** as the input.")
                    .foregroundColor(.secondary).fixedSize(horizontal: false, vertical: true)
                HStack {
                    Picker("", selection: Binding(get: { store.cfg.inputUID }, set: { store.setInput($0) })) {
                        Text("System default").tag("")
                        ForEach(store.inputs) { Text($0.name).tag($0.uid) }
                    }.labelsHidden().frame(width: 360)
                    Button("Refresh") { store.refreshInputs() }
                }
                ProgressView(value: Double(min(store.status.inputLevel, 1)))
                    .tint(.cvRed).frame(width: 360)
                    .opacity(store.status.running.boolValue ? 1 : 0.3)

                Divider().padding(.vertical, 14)
                Eyebrow(text: "Audio")
                Text("Levels").font(cvFont(22, .black))
                Toggle("Play soundboard sounds into CashVoice Mic", isOn: $store.cfg.soundsIntoMic)
                Toggle("Also play them on my speakers / headphones", isOn: $store.cfg.soundsOnSpeakers)
                LevelRow(label: "Mic volume", gain: $store.cfg.micGain, min: -30, max: 12)
                LevelRow(label: "Soundboard volume (in mic)", gain: $store.cfg.soundGain, min: -30, max: 12)
                LevelRow(label: "Soundboard volume (speakers)", gain: $store.cfg.localVolume, min: -40, max: 6)
                Text("0 dB is normal. Sounds are loudness-matched when they're loaded.").font(cvFont(12)).foregroundColor(.secondary)

                Divider().padding(.vertical, 14)
                Eyebrow(text: "Monitor")
                Text("Hear yourself").font(cvFont(22, .black))
                Toggle("Play my mic in my headphones", isOn: $store.cfg.hearSelf)
                Toggle("With the voice effect (untick to hear your normal voice)", isOn: $store.cfg.hearWet)
                LevelRow(label: "Monitor volume", gain: $store.cfg.hearVolume, min: -40, max: 6)
                Text("Use headphones: on speakers your mic hears itself and howls.").font(cvFont(12)).foregroundColor(.secondary)

                Divider().padding(.vertical, 14)
                Eyebrow(text: "Hotkeys")
                Text("Global shortcuts").font(cvFont(22, .black))
                ForEach(Config.actions) { a in
                    HStack {
                        Text(a.label).frame(width: 230, alignment: .leading)
                        HotkeyField(value: Binding(get: { store.cfg.globalHotkeys[a.key] },
                                                   set: { store.cfg.globalHotkeys[a.key] = $0; store.onHotkeysChanged?() }))
                            .frame(width: 200)
                    }
                }
                if !HotkeyCenter.shared.failed.isEmpty {
                    Text("Couldn't register (used by another app?): " + HotkeyCenter.shared.failed.joined(separator: ", "))
                        .foregroundColor(.cvRed).font(cvFont(12, .semibold))
                }

                Divider().padding(.vertical, 14)
                Eyebrow(text: "General")
                Toggle("Open at login", isOn: Binding(get: { loginItem }, set: { on in
                    do {
                        if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
                        loginItem = on; loginError = ""
                    } catch { loginError = error.localizedDescription }
                }))
                if !loginError.isEmpty { Text(loginError).font(cvFont(12)).foregroundColor(.cvRed) }
                Text("Version \(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "?")")
                    .font(cvFont(12)).foregroundColor(.secondary).padding(.top, 8)
            }
            .frame(maxWidth: 720, alignment: .leading)
            .padding(.horizontal, 32).padding(.vertical, 20)
        }
    }
}

struct LevelRow: View {
    let label: String
    @Binding var gain: Double
    let min: Double, max: Double
    var body: some View {
        HStack {
            Text(label).frame(width: 230, alignment: .leading)
            DbSlider(gain: $gain, min: min, max: max)
        }
    }
}

// MARK: - Menu bar

struct MenuContent: View {
    @EnvironmentObject var store: Store
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Toggle("Voice changer", isOn: $store.cfg.voiceEnabled)
        Toggle("Mute mic", isOn: $store.cfg.micMuted)
        Divider()
        Menu("Voice") {
            ForEach(store.cfg.voices) { v in
                Button((v.id == store.cfg.selectedVoiceId ? "✓ " : "") + v.name) { store.useVoice(v.id) }
            }
        }
        if !store.cfg.sounds.isEmpty {
            Menu("Play sound") {
                ForEach(store.cfg.sounds) { s in Button(s.name) { store.play(s) } }
            }
        }
        Divider()
        Button("Open CashVoice…") {
            NSApp.activate(ignoringOtherApps: true)
            openWindow(id: "main")
        }
        Button("Quit CashVoice") { NSApp.terminate(nil) }
    }
}

// MARK: - Type

/// Futura (ships with macOS) like the website's headings; heavier weights use Futura Bold.
func cvFont(_ size: CGFloat, _ weight: Font.Weight = .regular) -> Font {
    switch weight {
    case .black, .heavy, .bold: return .custom("Futura-Bold", size: size)
    default: return .custom("Futura-Medium", size: size)
    }
}
