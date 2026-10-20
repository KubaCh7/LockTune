import AppKit
import Combine

/// Wires the services together: now playing, lyrics, overlay and its triggers.
@MainActor
final class AppCoordinator: ObservableObject {
    static let shared = AppCoordinator()

    enum Reason { case manual, hotkey, idle, screenLock, displaySleep }

    let settings: AppSettings
    let nowPlaying: NowPlayingController
    let lyrics: LyricsController
    let overlayState: OverlayState
    let overlay: OverlayWindowController

    private let hotkeys = HotkeyManager()
    private let idleMonitor = IdleMonitor()
    private let lockMonitor = LockMonitor()
    private var cancellables = Set<AnyCancellable>()
    private var started = false

    /// The shortcut could not be registered (already used by another app).
    @Published private(set) var hotkeyUnavailable = false

    private init() {
        settings = AppSettings()
        nowPlaying = NowPlayingController(settings: settings)
        lyrics = LyricsController(nowPlaying: nowPlaying, settings: settings)
        overlayState = OverlayState()
        overlay = OverlayWindowController(overlay: overlayState, player: nowPlaying,
                                          lyrics: lyrics, settings: settings)
    }

    func start() {
        guard !started else { return }
        started = true

        nowPlaying.start()

        // Global hotkey.
        hotkeys.onTrigger = { [weak self] in self?.toggleOverlay(reason: .hotkey) }
        settings.$hotkey
            .combineLatest(settings.$hotkeyEnabled)
            .sink { [weak self] hotkey, enabled in self?.applyHotkey(hotkey, enabled: enabled) }
            .store(in: &cancellables)

        // Idle trigger.
        idleMonitor.onIdle = { [weak self] in
            guard let self, self.nowPlaying.isPlaying else { return }
            self.showOverlay(reason: .idle)
        }
        settings.$idleEnabled
            .combineLatest(settings.$idleMinutes)
            .sink { [weak self] enabled, minutes in
                self?.idleMonitor.isEnabled = enabled
                self?.idleMonitor.threshold = TimeInterval(max(1, minutes) * 60)
            }
            .store(in: &cancellables)
        idleMonitor.start()

        // Screen lock / display sleep.
        lockMonitor.onEvent = { [weak self] event in self?.handle(event) }
        lockMonitor.start()

        // Re-focus the overlay if the player changes while it's up.
        settings.$preferredSource
            .dropFirst()
            .sink { [weak self] _ in self?.nowPlaying.refreshSoon() }
            .store(in: &cancellables)
    }

    // MARK: Overlay

    func showOverlay(reason: Reason) {
        guard !overlay.isVisible else { return }
        nowPlaying.poll() // make sure the state is fresh

        if !nowPlaying.hasTrack && settings.emptyBehavior == .doNotShow {
            if reason == .manual || reason == .hotkey { NSSound.beep() }
            return
        }
        overlay.show()
    }

    func toggleOverlay(reason: Reason) {
        if overlay.isVisible {
            overlay.dismiss()
        } else {
            showOverlay(reason: reason)
        }
    }

    /// Menu items fire while the menu is still closing; wait a beat.
    func showOverlayFromMenu() {
        Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: 200_000_000)
            self?.showOverlay(reason: .manual)
        }
    }

    private func handle(_ event: LockMonitor.Event) {
        switch event {
        case .screenLocked:
            if settings.showOnLock, nowPlaying.isPlaying { showOverlay(reason: .screenLock) }
        case .displaySlept:
            if settings.showOnDisplaySleep, nowPlaying.isPlaying { showOverlay(reason: .displaySleep) }
        case .screenUnlocked, .displayWoke:
            overlay.reactivate()
        }
    }

    // MARK: Hotkey

    private func applyHotkey(_ hotkey: Hotkey, enabled: Bool) {
        if enabled {
            hotkeyUnavailable = !hotkeys.register(hotkey)
        } else {
            hotkeys.unregister()
            hotkeyUnavailable = false
        }
    }

    /// The settings recorder needs the current shortcut to reach it, not us.
    func suspendHotkey() { hotkeys.unregister() }
    func resumeHotkey() { applyHotkey(settings.hotkey, enabled: settings.hotkeyEnabled) }
}
