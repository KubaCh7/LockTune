import AppKit
import Carbon.HIToolbox

/// A global keyboard shortcut (Carbon key code + Carbon modifier mask).
struct Hotkey: Codable, Equatable {
    var keyCode: UInt32
    var carbonModifiers: UInt32
    /// Human-readable key, e.g. "L" or "Space".
    var keyLabel: String

    /// ⌃⌘L
    static let `default` = Hotkey(keyCode: UInt32(kVK_ANSI_L),
                                  carbonModifiers: UInt32(controlKey | cmdKey),
                                  keyLabel: "L")

    var displayString: String {
        var s = ""
        if carbonModifiers & UInt32(controlKey) != 0 { s += "⌃" }
        if carbonModifiers & UInt32(optionKey) != 0 { s += "⌥" }
        if carbonModifiers & UInt32(shiftKey) != 0 { s += "⇧" }
        if carbonModifiers & UInt32(cmdKey) != 0 { s += "⌘" }
        return s + keyLabel
    }

    /// Builds a hotkey from a key-down event. Requires ⌘ or ⌃ (macOS 15 rejects
    /// global hotkeys that only use ⌥/⇧).
    init?(event: NSEvent) {
        let flags = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
        guard flags.contains(.command) || flags.contains(.control) else { return nil }
        var mods: UInt32 = 0
        if flags.contains(.command) { mods |= UInt32(cmdKey) }
        if flags.contains(.control) { mods |= UInt32(controlKey) }
        if flags.contains(.option) { mods |= UInt32(optionKey) }
        if flags.contains(.shift) { mods |= UInt32(shiftKey) }
        keyCode = UInt32(event.keyCode)
        carbonModifiers = mods
        keyLabel = Hotkey.label(for: event)
    }

    init(keyCode: UInt32, carbonModifiers: UInt32, keyLabel: String) {
        self.keyCode = keyCode
        self.carbonModifiers = carbonModifiers
        self.keyLabel = keyLabel
    }

    private static func label(for event: NSEvent) -> String {
        let special: [Int: String] = [
            kVK_Space: "Space", kVK_Return: "↩", kVK_Tab: "⇥", kVK_Delete: "⌫",
            kVK_Escape: "⎋", kVK_LeftArrow: "←", kVK_RightArrow: "→",
            kVK_UpArrow: "↑", kVK_DownArrow: "↓",
            kVK_F1: "F1", kVK_F2: "F2", kVK_F3: "F3", kVK_F4: "F4", kVK_F5: "F5", kVK_F6: "F6",
            kVK_F7: "F7", kVK_F8: "F8", kVK_F9: "F9", kVK_F10: "F10", kVK_F11: "F11", kVK_F12: "F12",
        ]
        if let name = special[Int(event.keyCode)] { return name }
        return (event.charactersIgnoringModifiers ?? "?").uppercased()
    }
}

/// Registers one system-wide hotkey through the Carbon Event Manager.
///
/// `RegisterEventHotKey` does not need the Accessibility permission (unlike a
/// global `NSEvent` key monitor), which is why it is used here.
@MainActor
final class HotkeyManager {
    var onTrigger: (() -> Void)?

    private var hotKeyRef: EventHotKeyRef?
    private var handlerRef: EventHandlerRef?
    private static let signature: OSType = 0x4C4B_544E // 'LKTN'

    /// Returns false if the combination is already taken by another app.
    @discardableResult
    func register(_ hotkey: Hotkey) -> Bool {
        unregister()
        installHandlerIfNeeded()
        let id = EventHotKeyID(signature: Self.signature, id: 1)
        let status = RegisterEventHotKey(hotkey.keyCode, hotkey.carbonModifiers, id,
                                         GetApplicationEventTarget(), 0, &hotKeyRef)
        return status == OSStatus(noErr)
    }

    func unregister() {
        if let hotKeyRef {
            UnregisterEventHotKey(hotKeyRef)
        }
        hotKeyRef = nil
    }

    private func installHandlerIfNeeded() {
        guard handlerRef == nil else { return }
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard),
                                 eventKind: UInt32(kEventHotKeyPressed))
        let context = Unmanaged.passUnretained(self).toOpaque()
        InstallEventHandler(GetApplicationEventTarget(), { _, _, userData in
            guard let userData else { return OSStatus(eventNotHandledErr) }
            let manager = Unmanaged<HotkeyManager>.fromOpaque(userData).takeUnretainedValue()
            Task { @MainActor in manager.onTrigger?() }
            return OSStatus(noErr)
        }, 1, &spec, context, &handlerRef)
    }
}
