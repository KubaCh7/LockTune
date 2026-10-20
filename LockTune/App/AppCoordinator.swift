import AppKit
import Combine

/// Wires the services together: now playing, lyrics, the widget and its triggers.
@MainActor
final class AppCoordinator: ObservableObject {
    static let shared = AppCoordinator()

    let settings: AppSettings
    let nowPlaying: NowPlayingController
    let lyrics: LyricsController
    let overlayState: OverlayState
    let widget: LockWidgetController

    private let hotkeys = HotkeyManager()
    private let idleMonitor = IdleMonitor()
    private let lockMonitor = LockMonitor()
    private var cancellables = Set<AnyCancellable>()
    private var started = false
    /// The widget was put up while locked but couldn't go above the lock screen.
    private var showsAfterUnlock = false

    /// The shortcut could not be registered (already used by another app).
    @Published private(set) var hotkeyUnavailable = false
    @Published private(set) var isScreenLocked = false

    private init() {
        settings = AppSettings()
        nowPlaying = NowPlayingController(settings: settings)
        lyrics = LyricsController(nowPlaying: nowPlaying, settings: settings)
        overlayState = OverlayState()
        widget = LockWidgetController(overlay: overlayState, player: nowPlaying,
                                      lyrics: lyrics, settings: settings)
    }

    func start() {
        guard !started else { return }
        started = true

        nowPlaying.start()

        // Global shortcut: lock the Mac (the widget then appears by itself).
        hotkeys.onTrigger = { [weak self] in self?.lockNow() }
        settings.$hotkey
            .combineLatest(settings.$hotkeyEnabled)
            .sink { [weak self] hotkey, enabled in self?.applyHotkey(hotkey, enabled: enabled) }
            .store(in: &cancellables)

        // Optional: lock after being idle while music plays.
        idleMonitor.onIdle = { [weak self] in
            guard let self, self.nowPlaying.isPlaying, !self.isScreenLocked else { return }
            self.lockNow()
        }
        settings.$idleEnabled
            .combineLatest(settings.$idleMinutes)
            .sink { [weak self] enabled, minutes in
                self?.idleMonitor.isEnabled = enabled
                self?.idleMonitor.threshold = TimeInterval(max(1, minutes) * 60)
            }
            .store(in: &cancellables)
        idleMonitor.start()

        // Lock state drives the widget.
        lockMonitor.onChange = { [weak self] locked in self?.lockStateChanged(locked) }
        lockMonitor.start()

        // Show/hide the widget when music starts/stops while locked.
        // (@Published emits before the value changes, so evaluate on the next turn.)
        nowPlaying.$track
            .map { $0?.id }
            .removeDuplicates()
            .sink { [weak self] _ in
                Task { @MainActor in self?.updateWidget() }
            }
            .store(in: &cancellables)
        settings.$lockWidgetEnabled
            .dropFirst()
            .sink { [weak self] _ in
                Task { @MainActor in self?.updateWidget() }
            }
            .store(in: &cancellables)
        settings.$preferredSource
            .dropFirst()
            .sink { [weak self] _ in self?.nowPlaying.refreshSoon() }
            .store(in: &cancellables)
    }

    // MARK: Actions

    func lockNow() {
        nowPlaying.poll()
        ScreenLocker.lock()
    }

    func togglePreview() {
        if widget.presentation == .preview {
            widget.hide()
        } else {
            nowPlaying.poll()
            widget.show(.preview)
        }
    }

    /// Menu items fire while the menu is still closing; wait a beat.
    func afterMenuCloses(_ action: @escaping @MainActor () -> Void) {
        Task { @MainActor in
            try? await Task.sleep(nanoseconds: 200_000_000)
            action()
        }
    }

    // MARK: Lock handling

    private func lockStateChanged(_ locked: Bool) {
        isScreenLocked = locked
        if locked {
            nowPlaying.poll()
            updateWidget()
        } else {
            if showsAfterUnlock, widget.isVisible {
                // Fallback mode: keep the widget up as the first thing you see.
                widget.makeInteractive()
            } else if widget.presentation == .lockScreen {
                widget.hide(animated: false)
            }
            showsAfterUnlock = false
        }
    }

    private func updateWidget() {
        guard isScreenLocked else { return }
        if settings.lockWidgetEnabled && nowPlaying.hasTrack {
            widget.show(.lockScreen)
            showsAfterUnlock = !widget.isAboveLockScreen
        } else if widget.presentation == .lockScreen {
            widget.hide()
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
