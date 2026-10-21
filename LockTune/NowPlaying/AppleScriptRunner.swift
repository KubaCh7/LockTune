import Foundation

/// Compiles and runs AppleScript source, caching compiled scripts.
///
/// Not thread-safe: each runner is only ever used from NowPlayingController's
/// single serial script queue (never the main thread, so a slow player can't
/// stall the UI). Scripts are wrapped in `with timeout`.
final class AppleScriptRunner {
    private var cache: [String: NSAppleScript] = [:]
    private(set) var lastErrorCode: Int?
    private(set) var lastErrorMessage: String?

    /// Runs `source` and returns its result descriptor, or `nil` on error.
    /// Pass `cache: false` for one-off scripts (e.g. a seek with a literal value).
    @discardableResult
    func run(_ source: String, cache useCache: Bool = true) -> NSAppleEventDescriptor? {
        let script: NSAppleScript
        if useCache, let cached = cache[source] {
            script = cached
        } else {
            guard let compiled = NSAppleScript(source: source) else { return nil }
            var compileError: NSDictionary?
            guard compiled.compileAndReturnError(&compileError) else {
                lastErrorCode = compileError?[NSAppleScript.errorNumber] as? Int
                lastErrorMessage = "compile: " + ((compileError?[NSAppleScript.errorMessage] as? String) ?? "?")
                return nil
            }
            script = compiled
            if useCache { cache[source] = compiled }
        }

        var error: NSDictionary?
        let result = script.executeAndReturnError(&error)
        if let error {
            lastErrorCode = error[NSAppleScript.errorNumber] as? Int
            lastErrorMessage = error[NSAppleScript.errorMessage] as? String
            return nil
        }
        lastErrorCode = nil
        lastErrorMessage = nil
        return result
    }
}

extension NSAppleEventDescriptor {
    /// 1-based list item helper that tolerates out-of-range access.
    func item(_ index: Int) -> NSAppleEventDescriptor? {
        guard index >= 1, index <= numberOfItems else { return nil }
        return atIndex(index)
    }

    var isMissingValue: Bool {
        descriptorType == OSType(0x7479_7065) /* 'type' */ && typeCodeValue == OSType(0x6D73_6E67) /* 'msng' */
    }
}
