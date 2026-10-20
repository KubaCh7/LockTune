import Foundation

/// Locks the Mac (same effect as ⌃⌘Q / Apple menu → Lock Screen).
enum ScreenLocker {
    private typealias LockFunction = @convention(c) () -> Int32

    /// `SACLockScreenImmediate` from the login framework is what the system's
    /// own "Lock Screen" command calls. Resolved at runtime.
    private static let lockFunction: LockFunction? = {
        let path = "/System/Library/PrivateFrameworks/login.framework/Versions/Current/login"
        guard let handle = dlopen(path, RTLD_NOW),
              let symbol = dlsym(handle, "SACLockScreenImmediate") else { return nil }
        return unsafeBitCast(symbol, to: LockFunction.self)
    }()

    static var canLockDirectly: Bool { lockFunction != nil }

    static func lock() {
        if let lockFunction {
            _ = lockFunction()
            return
        }
        // Fallback: turn the display off (locks only when "Require password"
        // is set to Immediately in System Settings → Lock Screen).
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/pmset")
        process.arguments = ["displaysleepnow"]
        try? process.run()
    }
}
