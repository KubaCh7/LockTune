import AppKit
import SwiftUI

/// Click, then press a shortcut (must include ⌘ or ⌃). Esc cancels.
struct HotkeyRecorderView: View {
    @Binding var hotkey: Hotkey
    var onRecordingChanged: (Bool) -> Void = { _ in }

    @State private var isRecording = false
    @State private var monitor: Any?

    var body: some View {
        Button {
            isRecording ? stop() : start()
        } label: {
            Text(isRecording ? "Press shortcut…" : hotkey.displayString)
                .font(.system(.body, design: .rounded).weight(.medium))
                .frame(minWidth: 110)
        }
        .onDisappear { stop() }
    }

    private func start() {
        isRecording = true
        onRecordingChanged(true)
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            if event.keyCode == 53 { // Esc
                stop()
            } else if let recorded = Hotkey(event: event) {
                hotkey = recorded
                stop()
            } else {
                NSSound.beep()
            }
            return nil
        }
    }

    private func stop() {
        guard isRecording else { return }
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
        isRecording = false
        onRecordingChanged(false)
    }
}
