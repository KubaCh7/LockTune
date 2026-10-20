import AppKit

/// Self-updater that follows this repo's "latest" GitHub release.
///
/// CI stamps every build with `CFBundleVersion` = the workflow run number and
/// publishes `version.json` (`{"build": N, "commit": "…"}`) next to the .dmg
/// and the zipped .app. When a newer build exists, the zip is downloaded,
/// checked (bundle id, build number, code signature) and swapped in place of
/// the running app by a tiny helper script, which then relaunches LockTune.
@MainActor
final class Updater: ObservableObject {
    enum State: Equatable {
        case idle
        case checking
        case upToDate
        case available(build: Int)
        case downloading
        case installing
        case failed(String)
    }

    static let shared = Updater()
    private static let repository = "KubaCh7/LockTune"
    private static let releaseAPI = URL(string: "https://api.github.com/repos/\(repository)/releases/tags/latest")!
    private static let lastPromptedKey = "updaterLastPromptedBuild"
    static let autoCheckKey = "updaterAutoCheck"

    @Published private(set) var state: State = .idle
    @Published private(set) var lastChecked: Date?
    private var appZipURL: URL?
    private var timer: Timer?

    var currentBuild: Int {
        Int(Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "") ?? 0
    }

    var versionString: String {
        let marketing = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "1.0"
        return "\(marketing) (build \(currentBuild))"
    }

    var availableBuild: Int? {
        if case .available(let build) = state { return build }
        return nil
    }

    private var autoCheckEnabled: Bool {
        UserDefaults.standard.object(forKey: Self.autoCheckKey) as? Bool ?? true
    }

    // MARK: Scheduling

    /// Checks shortly after launch and then every 6 hours (if enabled).
    func start() {
        Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: 10_000_000_000)
            guard let self, self.autoCheckEnabled else { return }
            await self.check(userInitiated: false)
        }
        let timer = Timer(timeInterval: 6 * 3600, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self, self.autoCheckEnabled else { return }
                Task { await self.check(userInitiated: false) }
            }
        }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    // MARK: Check

    func check(userInitiated: Bool) async {
        if state == .checking || state == .downloading || state == .installing { return }
        state = .checking
        do {
            let release = try await fetchRelease()
            lastChecked = Date()
            appZipURL = release.appZip
            if release.build > currentBuild {
                state = .available(build: release.build)
                promptIfNeeded(build: release.build, userInitiated: userInitiated)
            } else {
                state = .upToDate
                if userInitiated { showUpToDateAlert() }
            }
        } catch {
            state = .failed(error.localizedDescription)
            if userInitiated { showFailureAlert(error.localizedDescription) }
        }
    }

    private struct Release {
        var build: Int
        var appZip: URL
    }

    private enum UpdateError: LocalizedError {
        case message(String)
        var errorDescription: String? {
            if case .message(let text) = self { return text }
            return nil
        }
    }

    private func fetchRelease() async throws -> Release {
        var request = URLRequest(url: Self.releaseAPI)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        request.setValue("LockTune-Updater", forHTTPHeaderField: "User-Agent")
        request.cachePolicy = .reloadIgnoringLocalCacheData
        let (data, response) = try await URLSession.shared.data(for: request)
        guard (response as? HTTPURLResponse)?.statusCode == 200,
              let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let assets = json["assets"] as? [[String: Any]]
        else { throw UpdateError.message("Couldn't read the latest release from GitHub.") }

        func assetURL(_ name: String) -> URL? {
            assets.first { $0["name"] as? String == name }
                .flatMap { $0["browser_download_url"] as? String }
                .flatMap(URL.init(string:))
        }
        guard let versionURL = assetURL("version.json"), let zipURL = assetURL("LockTune-app.zip") else {
            throw UpdateError.message("The latest release has no update files yet.")
        }

        let (versionData, _) = try await URLSession.shared.data(from: versionURL)
        guard let version = try JSONSerialization.jsonObject(with: versionData) as? [String: Any],
              let build = version["build"] as? Int
        else { throw UpdateError.message("The release's version.json is invalid.") }
        return Release(build: build, appZip: zipURL)
    }

    // MARK: Install

    func installUpdate() {
        guard let zipURL = appZipURL, let build = availableBuild else { return }
        state = .downloading
        Task { @MainActor in
            do {
                let (downloaded, _) = try await URLSession.shared.download(from: zipURL)
                state = .installing
                let newApp = try await Task.detached { try Self.unpackAndVerify(zip: downloaded, expectedBuild: build) }.value
                try relaunch(replacingWith: newApp)
            } catch {
                state = .failed(error.localizedDescription)
                showFailureAlert(error.localizedDescription)
            }
        }
    }

    /// Unzips the downloaded app and checks it really is a newer LockTune.
    nonisolated private static func unpackAndVerify(zip: URL, expectedBuild: Int) throws -> URL {
        let fm = FileManager.default
        let work = fm.temporaryDirectory.appendingPathComponent("LockTuneUpdate-\(UUID().uuidString)")
        try fm.createDirectory(at: work, withIntermediateDirectories: true)

        guard run("/usr/bin/ditto", ["-x", "-k", zip.path, work.path]) == 0 else {
            throw UpdateError.message("Couldn't unpack the update.")
        }
        let app = work.appendingPathComponent("LockTune.app")
        guard let bundle = Bundle(url: app),
              bundle.bundleIdentifier == Bundle.main.bundleIdentifier,
              Int(bundle.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "") == expectedBuild
        else { throw UpdateError.message("The downloaded file isn't the expected LockTune build.") }

        guard run("/usr/bin/codesign", ["--verify", "--deep", "--strict", app.path]) == 0 else {
            throw UpdateError.message("The downloaded app's signature is invalid.")
        }
        return app
    }

    nonisolated private static func run(_ tool: String, _ arguments: [String]) -> Int32 {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: tool)
        process.arguments = arguments
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        do { try process.run() } catch { return -1 }
        process.waitUntilExit()
        return process.terminationStatus
    }

    /// Where the new version goes: in place of the running app, or
    /// /Applications when running from the .dmg / a translocated copy.
    private var installDestination: URL {
        let current = Bundle.main.bundleURL
        let parent = current.deletingLastPathComponent()
        let translocated = current.path.contains("/AppTranslocation/")
        let onDiskImage = current.path.hasPrefix("/Volumes/")
        if translocated || onDiskImage || !FileManager.default.isWritableFile(atPath: parent.path) {
            return URL(fileURLWithPath: "/Applications/LockTune.app")
        }
        return current
    }

    /// Hands over to a helper script that waits for us to quit, swaps the app
    /// (restoring the old one if anything fails) and launches the new one.
    private func relaunch(replacingWith newApp: URL) throws {
        let destination = installDestination.path
        let script = """
        #!/bin/sh
        while kill -0 \(ProcessInfo.processInfo.processIdentifier) 2>/dev/null; do sleep 0.2; done
        DEST="\(destination)"
        NEW="\(newApp.path)"
        rm -rf "$DEST.old"
        if [ -e "$DEST" ]; then mv "$DEST" "$DEST.old" || exit 1; fi
        if /usr/bin/ditto "$NEW" "$DEST"; then
            rm -rf "$DEST.old"
        else
            rm -rf "$DEST"; mv "$DEST.old" "$DEST"
        fi
        /usr/bin/xattr -dr com.apple.quarantine "$DEST" 2>/dev/null
        /usr/bin/open "$DEST"
        """
        let scriptURL = FileManager.default.temporaryDirectory.appendingPathComponent("locktune-update.sh")
        try script.write(to: scriptURL, atomically: true, encoding: .utf8)

        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/sh")
        process.arguments = [scriptURL.path]
        try process.run()
        NSApp.terminate(nil)
    }

    // MARK: Alerts

    private func promptIfNeeded(build: Int, userInitiated: Bool) {
        let defaults = UserDefaults.standard
        guard userInitiated || defaults.integer(forKey: Self.lastPromptedKey) != build else { return }
        defaults.set(build, forKey: Self.lastPromptedKey)

        NSApp.activateCompat()
        let alert = NSAlert()
        alert.messageText = "A new version of LockTune is available"
        alert.informativeText = "Build \(build) is ready (you have build \(currentBuild)). LockTune will download it, replace itself and restart."
        alert.addButton(withTitle: "Update Now")
        alert.addButton(withTitle: "Later")
        if alert.runModal() == .alertFirstButtonReturn {
            installUpdate()
        }
    }

    private func showUpToDateAlert() {
        NSApp.activateCompat()
        let alert = NSAlert()
        alert.messageText = "LockTune is up to date"
        alert.informativeText = "You have the latest version, \(versionString)."
        alert.runModal()
    }

    private func showFailureAlert(_ message: String) {
        NSApp.activateCompat()
        let alert = NSAlert()
        alert.messageText = "Update failed"
        alert.informativeText = message
        alert.alertStyle = .warning
        alert.runModal()
    }
}
