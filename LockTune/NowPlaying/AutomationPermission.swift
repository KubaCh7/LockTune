import AppKit
import Carbon

/// Automation (Apple Events) consent for one player, via the public
/// `AEDeterminePermissionToAutomateTarget` API.
enum AutomationStatus: Equatable {
    case notRunning
    case allowed
    case denied
    case notDetermined
    case unknown(OSStatus)

    var title: String {
        switch self {
        case .notRunning: return "not running"
        case .allowed: return "allowed"
        case .denied: return "denied"
        case .notDetermined: return "not asked yet"
        case .unknown(let code): return "error \(code)"
        }
    }
}

enum AutomationPermission {
    /// Checks (and, with `ask`, prompts for) permission to control `bundleID`.
    /// Blocks while the system prompt is up, so never call it on the main thread.
    static func check(bundleID: String, ask: Bool) -> AutomationStatus {
        guard !NSRunningApplication.runningApplications(withBundleIdentifier: bundleID).isEmpty else {
            return .notRunning
        }
        let target = NSAppleEventDescriptor(bundleIdentifier: bundleID)
        let status = AEDeterminePermissionToAutomateTarget(target.aeDesc, AEEventClass(typeWildCard), AEEventID(typeWildCard), ask)
        switch Int(status) {
        case Int(noErr): return .allowed
        case -1743: return .denied        // errAEEventNotPermitted
        case -1744: return .notDetermined // errAEEventWouldRequireUserConsent
        case -600: return .notRunning     // procNotFound
        default: return .unknown(status)
        }
    }

    static func checkAsync(bundleID: String, ask: Bool) async -> AutomationStatus {
        await Task.detached(priority: .userInitiated) {
            check(bundleID: bundleID, ask: ask)
        }.value
    }

    /// Forgets an earlier "Don't Allow" so macOS asks again.
    static func resetDecision() {
        guard let bundleID = Bundle.main.bundleIdentifier else { return }
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/tccutil")
        process.arguments = ["reset", "AppleEvents", bundleID]
        try? process.run()
        process.waitUntilExit()
    }

    static func openSystemSettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Automation") {
            NSWorkspace.shared.open(url)
        }
    }
}
