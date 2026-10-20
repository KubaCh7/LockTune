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
        pollInterval = high ? 0.25 : 2
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

    private func setPosition(_ position: TimeInterval) {
        basePosition = position
        baseDate = Date()
        timelineRevision &+= 1
    }

    // MARK: Polling

    func refreshSoon(after delay: TimeInterval = 0.15) {
        Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000))
            self?.poll()
        }
    }

    func poll() {
        var best: (provider: NowPlayingProvider, snapshot: PlayerSnapshot, rank: Double)?
        var denied = false

        for provider in providers where settings.preferredSource.allows(provider.id) && provider.isRunning {
            let snapshot = provider.snapshot()
            if provider.lastErrorCode == -1743 { denied = true }
            if lastErrors[provider.displayName] != provider.lastErrorCode {
                lastErrors[provider.displayName] = provider.lastErrorCode
            }
            guard let snapshot, snapshot.trackID != nil else { continue }

            var rank: Double = snapshot.state == .playing ? 2 : 1
            if provider === active { rank += 0.5 } // stickiness
            if best == nil || rank > best!.rank {
                best = (provider: provider, snapshot: snapshot, rank: rank)
            }
        }

        if automationDenied != denied { automationDenied = denied }

        guard let best else {
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

        if providerChanged || snapshot.trackID != track?.id {
            if let info = provider.currentTrack() {
                track = info
                state = snapshot.state
                setPosition(snapshot.position)
                ignoreStateUntil = .distantPast
                loadArtwork(for: info, from: provider)
            }
            return
        }

        guard Date() >= ignoreStateUntil else { return }

        if snapshot.state != state {
            state = snapshot.state
            setPosition(snapshot.position)
        } else if abs(position() - snapshot.position) > 0.35 {
            // Drifted (or the user seeked in the player itself): resync.
            setPosition(snapshot.position)
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

    private func loadArtwork(for info: TrackInfo, from provider: NowPlayingProvider) {
        artworkTask?.cancel()
        // AppleScript must run on the main thread, so resolve the source here.
        let source = provider.artwork(for: info)

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

    func playPause() {
        guard let active else { return }
        let current = position()
        active.playPause()
        state = isPlaying ? .paused : .playing
        setPosition(current)
        ignoreStateUntil = Date().addingTimeInterval(0.8)
        refreshSoon(after: 0.9)
    }

    func next() {
        active?.nextTrack()
        refreshSoon()
    }

    /// iPhone behaviour: restart the song if more than 3 s in, otherwise go back.
    func previous() {
        if position() > 3 {
            seek(to: 0)
        } else {
            active?.previousTrack()
            refreshSoon()
        }
    }

    func seek(to target: TimeInterval) {
        guard let active else { return }
        let clamped = duration > 0 ? min(max(0, target), duration - 0.5) : max(0, target)
        active.seek(to: clamped)
        setPosition(clamped)
        ignoreStateUntil = Date().addingTimeInterval(0.6)
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
