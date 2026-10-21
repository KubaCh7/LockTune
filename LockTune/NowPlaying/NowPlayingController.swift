import AppKit
import Combine

/// Polls the available `NowPlayingProvider`s, picks the active one and
/// publishes track, state, artwork and an interpolated playback position.
@MainActor
final class NowPlayingController: ObservableObject {
    @Published private(set) var track: TrackInfo?
    @Published private(set) var state: PlaybackState = .stopped
    @Published private(set) var artwork: NSImage?
    @Published private(set) var blurredArtwork: NSImage?
    @Published private(set) var palette: ArtworkPalette = .fallback
    /// Incremented whenever `artwork` changes; used to drive cross-fades.
    @Published private(set) var artworkToken = 0
    @Published private(set) var sourceName: String?
    @Published private(set) var sourceBundleID: String?
    /// Bumped whenever the interpolation base is reset (seek, resync, state
    /// change) so views that only redraw while playing pick up the new position.
    @Published private(set) var timelineRevision = 0
    /// True when macOS refused our Apple Events (Automation permission denied).
    @Published private(set) var automationDenied = false
    /// Per-player Automation status, keyed by provider display name.
    @Published private(set) var automationStatus: [String: AutomationStatus] = [:]
    /// Last AppleScript error per player (for the Status window).
    @Published private(set) var lastErrors: [String: Int] = [:]
    /// Human-readable result of the last poll per player (Status → Diagnostics).
    @Published private(set) var diagnostics: [String: String] = [:]

    let providers: [NowPlayingProvider]
    private let settings: AppSettings
    private var active: NowPlayingProvider?

    // Position interpolation: position = basePosition + (now - baseDate) while playing.
    private var basePosition: TimeInterval = 0
    private var baseDate = Date()

    private var timer: Timer?
    private var pollInterval: TimeInterval = 2
    private var artworkTask: Task<Void, Never>?
    private var observers: [(NotificationCenter, NSObjectProtocol)] = []
    /// After a command we update the UI optimistically and ignore stale polls briefly.
    private var ignoreStateUntil = Date.distantPast

    var isPlaying: Bool { state == .playing }
    var hasTrack: Bool { track != nil }
    var duration: TimeInterval { track?.duration ?? 0 }

    init(settings: AppSettings) {
        self.settings = settings
        // One runner per provider so error codes are attributable.
        providers = [
            AppleMusicProvider(runner: AppleScriptRunner()),
            SpotifyProvider(runner: AppleScriptRunner()),
        ]
    }

    // MARK: Lifecycle

    func start() {
        let distributed = DistributedNotificationCenter.default()
        for provider in providers {
            guard let name = provider.changeNotificationName else { continue }
            let token = distributed.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.refreshSoon(after: 0.05) }
            }
            observers.append((distributed, token))
        }

        let workspace = NSWorkspace.shared.notificationCenter
        for name in [NSWorkspace.didLaunchApplicationNotification, NSWorkspace.didTerminateApplicationNotification] {
            let token = workspace.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.refreshSoon(after: 0.5) }
            }
            observers.append((workspace, token))
        }

        setHighFrequency(false)
        requestAutomationAccess()
    }

    /// 4 Hz while the overlay is visible, every 2 s otherwise.
    func setHighFrequency(_ high: Bool) {
        pollInterval = high ? (settings.liteMode ? 0.5 : 0.25) : 2
        timer?.invalidate()
        let timer = Timer(timeInterval: pollInterval, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.poll() }
        }
        timer.tolerance = pollInterval * 0.1
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
        if high { poll() }
    }

    // MARK: Position

    /// Interpolated playback position at `date`.
    func position(at date: Date = Date()) -> TimeInterval {
        var p = basePosition
        if state == .playing { p += date.timeIntervalSince(baseDate) }
        if let duration = track?.duration, duration > 0 { p = min(p, duration) }
        return max(0, p)
    }

    private func setPosition(_ position: TimeInterval, at date: Date = Date()) {
        basePosition = position
        baseDate = date
        timelineRevision &+= 1
    }

    // MARK: Polling
    //
    // All AppleScript runs on `scriptQueue` (one serial background queue), never
    // on the main thread: a slow or busy player can't freeze the widget, and fast
    // repeated clicks just queue up commands while the UI updates instantly.

    /// One serial queue owns every provider/NSAppleScript call.
    private let scriptQueue = DispatchQueue(label: "LockTune.AppleScript", qos: .userInitiated)
    private var pollInFlight = false
    private var pollAgain = false

    func refreshSoon(after delay: TimeInterval = 0.15) {
        Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000))
            self?.poll()
        }
    }

    /// What one background poll found.
    private struct PollResult {
        var report: [String: String] = [:]
        var errors: [String: Int?] = [:]
        var denied = false
        var best: (provider: NowPlayingProvider, snapshot: PlayerSnapshot)?
        /// Filled when the track changed: its metadata and where to get artwork.
        var newTrack: TrackInfo?
        var artworkSource: ArtworkSource = .none
        var trackError: String?
    }

    func poll() {
        // Coalesce: at most one poll in flight, plus one queued behind it.
        guard !pollInFlight else {
            pollAgain = true
            return
        }
        pollInFlight = true

        let providers = self.providers
        let allowed = providers.map { settings.preferredSource.allows($0.id) }
        let activeProvider = active
        let currentTrackID = track?.id

        scriptQueue.async { [weak self] in
            var result = PollResult()
            var bestRank = -1.0
            for (provider, isAllowed) in zip(providers, allowed) {
                let name = provider.displayName
                guard isAllowed else {
                    result.report[name] = "ignored (Music source setting)"
                    continue
                }
                guard provider.isRunning else {
                    result.report[name] = provider.isInstalled ? "not running" : "not installed"
                    continue
                }
                let snapshot = provider.snapshot()
                result.errors[name] = provider.lastErrorCode
                if provider.lastErrorCode == -1743 { result.denied = true }
                if let snapshot {
                    result.report[name] = "running · state: \(snapshot.state.rawValue) · position: "
                        + String(format: "%.1f", snapshot.position) + " s · track: \(snapshot.trackID ?? "none")"
                } else {
                    result.report[name] = "running · script error \(provider.lastErrorCode.map { String($0) } ?? "?"): "
                        + (provider.lastErrorMessage ?? "unknown")
                }
                guard let snapshot, snapshot.trackID != nil else { continue }
                var rank: Double = snapshot.state == .playing ? 2 : 1
                if provider === activeProvider { rank += 0.5 } // stickiness
                if rank > bestRank {
                    bestRank = rank
                    result.best = (provider, snapshot)
                }
            }

            // New track (or new player): fetch metadata + artwork source here too.
            if let best = result.best,
               best.provider !== activeProvider || best.snapshot.trackID != currentTrackID {
                if let info = best.provider.currentTrack() {
                    result.newTrack = info
                    result.artworkSource = best.provider.artwork(for: info)
                } else {
                    result.trackError = "track info error \(best.provider.lastErrorCode.map { String($0) } ?? "?"): "
                        + (best.provider.lastErrorMessage ?? "unknown")
                }
            }

            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    guard let self else { return }
                    self.apply(result)
                    self.pollInFlight = false
                    if self.pollAgain {
                        self.pollAgain = false
                        self.poll()
                    }
                }
            }
        }
    }

    private func apply(_ result: PollResult) {
        if automationDenied != result.denied { automationDenied = result.denied }
        for (name, code) in result.errors where lastErrors[name] != code {
            lastErrors[name] = code
        }
        var report = result.report
        if let error = result.trackError, let name = result.best?.provider.displayName {
            report[name, default: ""] += " · " + error
        }
        if diagnostics != report { diagnostics = report }

        guard let best = result.best else {
            clear()
            return
        }

        let provider = best.provider
        let snapshot = best.snapshot
        let providerChanged = provider !== active
        active = provider
        if sourceName != provider.displayName {
            sourceName = provider.displayName
            sourceBundleID = provider.bundleIdentifier
        }

        if let info = result.newTrack {
            if info != track { track = info }
            state = snapshot.state
            setPosition(snapshot.position, at: snapshot.capturedAt)
            ignoreStateUntil = .distantPast
            loadArtwork(for: info, source: result.artworkSource)
            return
        }
        if providerChanged || snapshot.trackID != track?.id {
            return // track info failed this time; try again on the next poll
        }

        guard Date() >= ignoreStateUntil else { return }

        if snapshot.state != state {
            state = snapshot.state
            setPosition(snapshot.position, at: snapshot.capturedAt)
        } else if abs(position(at: snapshot.capturedAt) - snapshot.position) > 0.25 {
            // Drifted (or the user seeked in the player itself): resync.
            setPosition(snapshot.position, at: snapshot.capturedAt)
        }
    }

    private func clear() {
        active = nil
        artworkTask?.cancel()
        guard track != nil || state != .stopped else { return }
        track = nil
        state = .stopped
        sourceName = nil
        sourceBundleID = nil
        artwork = nil
        blurredArtwork = nil
        palette = .fallback
        artworkToken &+= 1
        setPosition(0)
    }

    // MARK: Artwork

    private func loadArtwork(for info: TrackInfo, source: ArtworkSource) {
        artworkTask?.cancel()
        artworkTask = Task { @MainActor [weak self] in
            var image: NSImage?
            switch source {
            case .image(let img): image = img
            case .url(let url): image = await ArtworkFetcher.image(from: url)
            case .none: break
            }
            if image == nil {
                image = await ArtworkFetcher.searchITunes(for: info)
            }
            guard !Task.isCancelled else { return }

            let cgImage = image?.cgImage(forProposedRect: nil, context: nil, hints: nil)
            let analysis: ArtworkAnalysis? = await Task.detached(priority: .userInitiated) {
                cgImage.map { ArtworkAnalyzer.analyze($0) }
            }.value

            guard let self, !Task.isCancelled, self.track?.id == info.id else { return }
            self.artwork = image
            self.blurredArtwork = analysis?.blurred
            self.palette = analysis?.palette ?? .fallback
            self.artworkToken &+= 1
        }
    }

    // MARK: Commands
    //
    // The UI reacts immediately (optimistic state); the Apple Event is sent in
    // the background, so rapid clicks never block the widget.

    private func send(_ command: @escaping (NowPlayingProvider) -> Void) {
        guard let provider = active else { return }
        scriptQueue.async { command(provider) }
    }

    func playPause() {
        guard active != nil else { return }
        let current = position()
        state = isPlaying ? .paused : .playing
        setPosition(current)
        ignoreStateUntil = Date().addingTimeInterval(1.0)
        send { $0.playPause() }
        refreshSoon(after: 1.1)
    }

    func next() {
        send { $0.nextTrack() }
        refreshSoon(after: 0.35)
    }

    /// iPhone behaviour: restart the song if more than 3 s in, otherwise go back.
    func previous() {
        if position() > 3 {
            seek(to: 0)
        } else {
            send { $0.previousTrack() }
            refreshSoon(after: 0.35)
        }
    }

    func seek(to target: TimeInterval) {
        guard active != nil else { return }
        let clamped = duration > 0 ? min(max(0, target), duration - 0.5) : max(0, target)
        setPosition(clamped)
        ignoreStateUntil = Date().addingTimeInterval(0.8)
        send { $0.seek(to: clamped) }
    }

    /// Shows the macOS "LockTune wants to control …" prompt for every running
    /// player (and refreshes the status shown in the Status window).
    func requestAutomationAccess(resetFirst: Bool = false) {
        let players = providers.map { ($0.displayName, $0.bundleIdentifier) }
        Task { @MainActor [weak self] in
            if resetFirst {
                await Task.detached { AutomationPermission.resetDecision() }.value
            }
            for (name, bundleID) in players {
                let status = await AutomationPermission.checkAsync(bundleID: bundleID, ask: true)
                self?.automationStatus[name] = status
            }
            self?.poll()
        }
    }

    /// Refreshes the per-player status without prompting.
    func refreshAutomationStatus() {
        let players = providers.map { ($0.displayName, $0.bundleIdentifier) }
        Task { @MainActor [weak self] in
            for (name, bundleID) in players {
                let status = await AutomationPermission.checkAsync(bundleID: bundleID, ask: false)
                self?.automationStatus[name] = status
            }
        }
    }
}
