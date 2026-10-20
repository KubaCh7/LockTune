import Foundation

/// Locks the Mac (same effect as ⌃⌘Q / Apple menu → Lock Screen).
enum ScreenLocker {
    static func lock() {
        // `pmset displaysleepnow` turns the display off; with "Require password
        // immediately" (System Settings → Lock Screen) that locks the Mac.
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/pmset")
        process.arguments = ["displaysleepnow"]
        try? process.run()
    }
}
