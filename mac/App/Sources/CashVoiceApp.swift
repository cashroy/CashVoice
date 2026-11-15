import SwiftUI
import AVFoundation

@main
struct CashVoiceApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var delegate
    @StateObject private var store = AppDelegate.store

    var body: some Scene {
        Window("CashVoice", id: "main") {
            MainView().environmentObject(store)
        }
        .defaultSize(width: 980, height: 720)

        MenuBarExtra {
            MenuContent().environmentObject(store)
        } label: {
            Image(systemName: store.cfg.voiceEnabled ? "waveform.circle.fill" : "waveform.circle")
        }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    static let store = Store()

    func applicationDidFinishLaunching(_ notification: Notification) {
        let store = AppDelegate.store
        store.onHotkeysChanged = { [weak self] in self?.registerHotkeys() }
        registerHotkeys()

        // Microphone access (macOS asks once; CashVoice only opens the mic while an app uses CashVoice Mic).
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized: store.micPermission = true
        case .notDetermined:
            store.micPermission = false
            AVCaptureDevice.requestAccess(for: .audio) { ok in
                DispatchQueue.main.async { store.micPermission = ok; store.push() }
            }
        default: store.micPermission = false
        }
        store.push()
    }

    func registerHotkeys() {
        let store = AppDelegate.store
        let hk = HotkeyCenter.shared
        hk.clear()
        let g = store.cfg.globalHotkeys
        hk.register(g["toggle"], name: "Voice on/off") { store.toggleVoice() }
        hk.register(g["next"], name: "Next voice") { store.cycleVoice(1) }
        hk.register(g["prev"], name: "Previous voice") { store.cycleVoice(-1) }
        hk.register(g["mute"], name: "Mute mic") { store.toggleMute() }
        hk.register(g["stop"], name: "Stop sounds") { store.stopSounds() }
        for v in store.cfg.voices { let id = v.id; hk.register(v.hotkey, name: v.name) { store.useVoice(id) } }
        for s in store.cfg.sounds { let item = s; hk.register(s.hotkey, name: s.name) { store.play(item) } }
    }

    // Closing the window keeps CashVoice running in the menu bar.
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    func applicationWillTerminate(_ notification: Notification) {
        AppDelegate.store.save()
        cv_shutdown()
    }
}
