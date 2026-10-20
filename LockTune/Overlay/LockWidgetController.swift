import AppKit
import Carbon.HIToolbox
import SwiftUI

/// Something that can lift a window above the macOS lock screen.
///
/// macOS has no public API for this. LockTune ships without an implementation,
/// so on lock the widget is prepared behind the lock screen and is the first
/// thing you see after unlocking. An implementation can be plugged in here
/// (see README → "Showing the widget on the lock screen itself").
protocol LockScreenLayer {
    var isAvailable: Bool { get }
    /// Returns false if the window could not be moved above the lock screen.
    func attach(_ window: NSWindow) -> Bool
}

/// Transparent, borderless panel that hosts the widget on one screen.
final class LockWidgetPanel: NSPanel {
    var allowsKey = false
    /// Return true to swallow the event.
    var keyHandler: ((NSEvent) -> Bool)?
    var scrollHandler: ((NSEvent) -> Bool)?

    convenience init(screen: NSScreen) {
        self.init(contentRect: screen.frame,
                  styleMask: [.borderless, .nonactivatingPanel],
                  backing: .buffered, defer: false)
        level = .screenSaver
        collectionBehavior = [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary, .ignoresCycle]
        isOpaque = false
        backgroundColor = .clear
        hasShadow = false
        isReleasedWhenClosed = false
        hidesOnDeactivate = false
        isFloatingPanel = true
        animationBehavior = .none
        isMovable = false
        setFrame(screen.frame, display: false)
    }

    override var canBecomeKey: Bool { allowsKey }
    override var canBecomeMain: Bool { false }

    override func sendEvent(_ event: NSEvent) {
        switch event.type {
        case .keyDown:
            if keyHandler?(event) == true { return }
        case .scrollWheel:
            if scrollHandler?(event) == true { return }
        default:
            break
        }
        super.sendEvent(event)
    }
}

/// Hosting view that reacts to the first click even when its window isn't key.
final class FirstMouseHostingView<Content: View>: NSHostingView<Content> {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
}

/// Shows the now-playing widget on every display.
///
/// The lock-screen panels are created once and kept alive (invisible and
/// click-through) inside the lock-screen layer, so when the Mac locks the
/// widget appears instantly instead of after a visible delay.
@MainActor
final class LockWidgetController {
    enum Presentation: Equatable {
        /// Shown because the Mac is (being) locked.
        case lockScreen
        /// Shown from the menu / status window to check how it looks.
        case preview
    }

    private let overlay: OverlayState
    private let player: NowPlayingController
    private let lyrics: LyricsController
    private let settings: AppSettings
    var lockScreenLayer: LockScreenLayer?

    private var lockPanels: [LockWidgetPanel] = []
    private var previewPanels: [LockWidgetPanel] = []
    private var lockPanelsLifted = false
    private(set) var presentation: Presentation?
    private var generation = 0
    /// Set while an animated hide is in flight.
    private var hidingToken: Int?
    private var screenObserver: NSObjectProtocol?
    /// Keeps App Nap from throttling our timers/rendering while the widget is
    /// up (that made lyrics stall now and then).
    private var activity: NSObjectProtocol?

    var isVisible: Bool { presentation != nil }
    /// True when the lock-screen panels really sit above the lock screen.
    var isAboveLockScreen: Bool { presentation == .lockScreen && lockPanelsLifted }
    var canShowAboveLockScreen: Bool { lockScreenLayer?.isAvailable == true }

    init(overlay: OverlayState, player: NowPlayingController, lyrics: LyricsController, settings: AppSettings) {
        self.overlay = overlay
        self.player = player
        self.lyrics = lyrics
        self.settings = settings
    }

    /// Builds the (hidden) lock-screen panels ahead of time.
    func prepare() {
        buildLockPanels()
        screenObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.screensChanged() }
        }
    }

    // MARK: Show / hide

    func show(_ presentation: Presentation) {
        if self.presentation == presentation {
            // Already up; if it was fading out, bring it back.
            if hidingToken != nil {
                generation &+= 1
                hidingToken = nil
                overlay.present()
            }
            return
        }
        if let current = self.presentation { finishHiding(current) }
        generation &+= 1
        hidingToken = nil
        self.presentation = presentation

        overlay.prepare()
        overlay.presentation = presentation
        overlay.isActive = true

        switch presentation {
        case .lockScreen:
            if lockPanels.isEmpty { buildLockPanels() }
            for panel in lockPanels {
                panel.allowsKey = false
                panel.ignoresMouseEvents = false
                panel.alphaValue = 1
                panel.orderFrontRegardless()
            }
        case .preview:
            buildPreviewPanels()
            NSApp.activate()
            previewPanels.first?.makeKeyAndOrderFront(nil)
        }

        player.setHighFrequency(true)
        lyrics.reloadIfNeeded()
        if activity == nil {
            activity = ProcessInfo.processInfo.beginActivity(
                options: [.userInitiated, .latencyCritical],
                reason: "Showing Now Playing and synced lyrics")
        }

        // Render one frame in the "before" state, then animate in.
        Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: 16_000_000)
            self?.overlay.present()
        }
    }

    func hide(animated: Bool = true) {
        guard let current = presentation else { return }
        generation &+= 1
        let token = generation
        overlay.dismiss()
        guard animated else {
            hidingToken = nil
            finishHiding(current)
            return
        }
        hidingToken = token
        Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: 350_000_000)
            guard let self, self.generation == token else { return }
            self.hidingToken = nil
            self.finishHiding(current)
        }
    }

    private func finishHiding(_ which: Presentation) {
        overlay.isActive = false
        switch which {
        case .lockScreen:
            for panel in lockPanels {
                panel.alphaValue = 0
                panel.ignoresMouseEvents = true
                panel.allowsKey = false
            }
        case .preview:
            for panel in previewPanels {
                panel.orderOut(nil)
                panel.contentView = nil
                panel.close()
            }
            previewPanels.removeAll()
        }
        if presentation == which {
            presentation = nil
            player.setHighFrequency(false)
            if let activity { ProcessInfo.processInfo.endActivity(activity) }
            activity = nil
        }
    }

    /// After unlocking (when the widget wasn't above the lock screen) let it
    /// take keyboard focus so Esc / Space / arrows work.
    func makeInteractive() {
        guard presentation == .lockScreen else { return }
        lockPanels.forEach { $0.allowsKey = true }
        NSApp.activate()
        lockPanels.first?.makeKeyAndOrderFront(nil)
    }

    // MARK: Panels

    private var screens: [NSScreen] {
        if settings.allDisplays { return NSScreen.screens }
        return [NSScreen.main ?? NSScreen.screens.first].compactMap { $0 }
    }

    private func makePanel(for screen: NSScreen) -> LockWidgetPanel {
        let panel = LockWidgetPanel(screen: screen)
        let root = LockWidgetView(overlay: overlay, player: player, lyrics: lyrics, settings: settings,
                                  onClose: { [weak self] in self?.hide() })
        let hosting = FirstMouseHostingView(rootView: root)
        hosting.sizingOptions = []
        panel.contentView = hosting
        panel.keyHandler = { [weak self] event in
            MainActor.assumeIsolated { self?.handleKey(event) ?? false }
        }
        panel.scrollHandler = { [weak self] event in
            MainActor.assumeIsolated {
                guard let self else { return false }
                return self.overlay.handleScroll(event, hasSyncedLyrics: self.lyrics.syncedLines != nil)
            }
        }
        panel.setFrame(screen.frame, display: true)
        return panel
    }

    private func buildLockPanels() {
        for panel in lockPanels {
            panel.orderOut(nil)
            panel.close()
        }
        lockPanels.removeAll()

        var lifted = !screens.isEmpty
        for screen in screens {
            let panel = makePanel(for: screen)
            let visible = presentation == .lockScreen
            panel.alphaValue = visible ? 1 : 0
            panel.ignoresMouseEvents = !visible
            panel.orderFrontRegardless()
            let attached = lockScreenLayer.map { $0.isAvailable && $0.attach(panel) } ?? false
            lifted = lifted && attached
            panel.orderFrontRegardless()
            lockPanels.append(panel)
        }
        lockPanelsLifted = lifted
    }

    private func buildPreviewPanels() {
        for screen in screens {
            let panel = makePanel(for: screen)
            panel.allowsKey = true
            panel.orderFrontRegardless()
            previewPanels.append(panel)
        }
    }

    private func screensChanged() {
        buildLockPanels()
        if presentation == .preview {
            let wasPresented = overlay.isPresented
            for panel in previewPanels { panel.orderOut(nil); panel.close() }
            previewPanels.removeAll()
            buildPreviewPanels()
            if wasPresented { overlay.present() }
        }
    }

    // MARK: Keys (only reach us in preview, or after unlocking)

    private func handleKey(_ event: NSEvent) -> Bool {
        let repeated = event.isARepeat
        switch Int(event.keyCode) {
        case kVK_Escape:
            hide()
        case kVK_Space:
            if !repeated { player.playPause() }
        case kVK_LeftArrow:
            if !repeated { player.previous() }
        case kVK_RightArrow:
            if !repeated { player.next() }
        case kVK_ANSI_L:
            if !repeated, player.hasTrack { overlay.toggleLyrics() }
        default:
            return false
        }
        return true
    }
}
