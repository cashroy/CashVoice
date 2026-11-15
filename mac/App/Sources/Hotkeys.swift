import AppKit
import Carbon.HIToolbox

/// System-wide hotkeys through Carbon's RegisterEventHotKey (works without Accessibility permission).
final class HotkeyCenter {
    static let shared = HotkeyCenter()
    private var refs: [EventHotKeyRef] = []
    private var actions: [UInt32: () -> Void] = [:]
    private var nextId: UInt32 = 1
    private var handlerInstalled = false
    private(set) var failed: [String] = []

    private init() {}

    private func installHandler() {
        guard !handlerInstalled else { return }
        handlerInstalled = true
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, event, _ in
            var hk = EventHotKeyID()
            GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                              nil, MemoryLayout<EventHotKeyID>.size, nil, &hk)
            if let action = HotkeyCenter.shared.actions[hk.id] { DispatchQueue.main.async(execute: action) }
            return noErr
        }, 1, &spec, nil, nil)
    }

    func clear() {
        for r in refs { UnregisterEventHotKey(r) }
        refs.removeAll(); actions.removeAll(); failed.removeAll()
    }

    func register(_ spec: HotkeySpec?, name: String, _ action: @escaping () -> Void) {
        guard let spec else { return }
        installHandler()
        let id = nextId; nextId += 1
        var ref: EventHotKeyRef?
        let hkID = EventHotKeyID(signature: OSType(0x43564345), id: id)   // 'CVCE'
        let st = RegisterEventHotKey(spec.keyCode, spec.mods, hkID, GetApplicationEventTarget(), 0, &ref)
        if st == noErr, let ref { refs.append(ref); actions[id] = action }
        else { failed.append("\(name) (\(spec.label))") }
    }
}

enum KeyNames {
    static let special: [UInt16: String] = [
        122: "F1", 120: "F2", 99: "F3", 118: "F4", 96: "F5", 97: "F6", 98: "F7", 100: "F8", 101: "F9",
        109: "F10", 103: "F11", 111: "F12", 105: "F13", 107: "F14", 113: "F15",
        36: "↩", 48: "⇥", 49: "Space", 51: "⌫", 53: "⎋", 117: "⌦", 115: "Home", 119: "End",
        116: "PgUp", 121: "PgDn", 123: "←", 124: "→", 125: "↓", 126: "↑",
    ]
    static func isFunctionKey(_ code: UInt16) -> Bool {
        [122, 120, 99, 118, 96, 97, 98, 100, 101, 109, 103, 111, 105, 107, 113].contains(code)
    }

    /// Build a HotkeySpec from a key-down event, or nil if it shouldn't be a global hotkey.
    static func spec(from e: NSEvent) -> HotkeySpec? {
        let f = e.modifierFlags.intersection(.deviceIndependentFlagsMask)
        var mods: UInt32 = 0, label = ""
        if f.contains(.control) { mods |= UInt32(controlKey); label += "⌃" }
        if f.contains(.option) { mods |= UInt32(optionKey); label += "⌥" }
        if f.contains(.shift) { mods |= UInt32(shiftKey); label += "⇧" }
        if f.contains(.command) { mods |= UInt32(cmdKey); label += "⌘" }
        let code = e.keyCode
        // Plain letters would block normal typing everywhere; only F-keys may go without modifiers.
        if mods == 0 && !isFunctionKey(code) { return nil }
        let key = special[code] ?? (e.charactersIgnoringModifiers ?? "?").uppercased()
        return HotkeySpec(keyCode: UInt32(code), mods: mods, label: label + key)
    }
}
