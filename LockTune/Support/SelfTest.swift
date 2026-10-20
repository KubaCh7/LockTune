import AppKit

/// `LockTune --self-test` — run by CI on a real Mac. Checks the parts that
/// can't be verified by compiling alone, prints PASS/FAIL lines and exits.
@MainActor
enum SelfTest {
    static func runIfRequested() {
        guard CommandLine.arguments.contains("--self-test") else { return }
        var failures = 0

        func report(_ ok: Bool?, _ name: String, _ detail: String = "") {
            let tag = ok == nil ? "SKIP" : (ok! ? "PASS" : "FAIL")
            print("\(tag)  \(name)\(detail.isEmpty ? "" : " — \(detail)")")
            if ok == false { failures += 1 }
        }

        print("LockTune self-test on macOS \(ProcessInfo.processInfo.operatingSystemVersionString)")

        // 1. Every AppleScript we send compiles against the player's dictionary.
        let providers: [AppleScriptPlayerProvider] = [AppleMusicProvider(runner: AppleScriptRunner()),
                                                      SpotifyProvider(runner: AppleScriptRunner())]
        for provider in providers {
            guard provider.isInstalled else {
                report(nil, "\(provider.displayName) scripts", "app not installed")
                continue
            }
            for script in provider.selfTestScripts {
                var error: NSDictionary?
                let compiled = NSAppleScript(source: script.source)?.compileAndReturnError(&error) ?? false
                let message = (error?[NSAppleScript.errorMessage] as? String) ?? ""
                report(compiled, "\(provider.displayName) \(script.name) script compiles", message)
            }
        }

        // 2. LRC parsing.
        let lrc = "[ar:Test]\n[offset:+500]\n[00:05.00]First\n[00:07.50][00:20.00]Twice\n[00:12.00]\n[00:18.00]Last"
        let lines = LRCParser.parse(lrc)
        report(lines.first?.isInstrumental == true && lines.contains { $0.text == "First" && abs($0.time - 4.5) < 0.01 },
               "LRC parser (offset, intro dots)", lines.map { "\($0.time):\($0.text)" }.joined(separator: " | "))
        report(lines.filter { $0.text == "Twice" }.count == 2, "LRC parser (multiple timestamps)")
        report(LRCParser.activeIndex(in: lines, at: 8) == lines.firstIndex { $0.text == "Twice" },
               "Active lyric line lookup")

        // 3. Private functions needed for the lock screen / locking.
        let missing = SkyLightLockScreenLayer.missingSymbols
        report(missing.isEmpty, "Window-server functions for the lock-screen layer",
               missing.isEmpty ? "" : "missing: \(missing.joined(separator: ", "))")
        report(ScreenLocker.canLockDirectly, "System lock function (SACLockScreenImmediate)")

        // 4. A widget panel can actually be moved into the lock-screen layer.
        if let screen = NSScreen.main ?? NSScreen.screens.first {
            _ = NSApplication.shared
            let layer = SkyLightLockScreenLayer()
            let panel = LockWidgetPanel(screen: screen)
            panel.orderFrontRegardless()
            let attached = layer.isAvailable && layer.attach(panel)
            report(attached, "Panel attached to the lock-screen layer", "window \(panel.windowNumber)")
            panel.orderOut(nil)
        } else {
            report(nil, "Panel attached to the lock-screen layer", "no screen on this machine")
        }

        // 5. Lock state can be read.
        print("INFO  session locked: \(LockMonitor.sessionIsLocked)")

        print(failures == 0 ? "ALL PASSED" : "\(failures) FAILED")
        fflush(stdout)
        exit(failures == 0 ? 0 : 1)
    }
}
