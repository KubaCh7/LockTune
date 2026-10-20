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
@MainActor
final class LockWidgetController {
    enum Presentation: Equatable {
        /// Shown because the Mac was locked.
        case lockScreen
        /// Shown from the menu / status window to check how it looks.
        case preview
    }

    private let overlay: OverlayState
    private let player: NowPlayingController
    private let lyrics: LyricsController
    private let settings: AppSettings
    var lockScreenLayer: LockScreenLayer?

    private var panels: [LockWidgetPanel] = []
    private(set) var presentation: Presentation?
    /// True when the current panels really sit above the lock screen.
    private(set) var isAboveLockScreen = false
    private var generation = 0
    private var screenObserver: NSObjectProtocol?

    var isVisible: Bool { !panels.isEmpty }

    init(overlay: OverlayState, player: NowPlayingController, lyrics: LyricsController, settings: AppSettings) {
        self.overlay = overlay
        self.player = player
        self.lyrics = lyrics
        self.settings = settings
    }

    // MARK: Show / hide

    func show(_ presentation: Presentation) {
        if self.presentation == presentation, isVisible, overlay.isPresented { return }
        generation &+= 1
        tearDown()
        self.presentation = presentation
        overlay.prepare()
        buildPanels()

        if presentation == .preview {
            NSApp.activate()
            panels.first?.makeKeyAndOrderFront(nil)
        }

        player.setHighFrequency(true)
        lyrics.reloadIfNeeded()

        screenObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.rebuild() }
        }

        // Let the first frame render with isPresented == false, then animate in.
        Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: 16_000_000)
            self?.overlay.present()
        }
    }

    func hide(animated: Bool = true) {
        guard isVisible else { return }
        generation &+= 1
        let current = generation
        guard animated else {
            tearDown()
            return
        }
        overlay.dismiss()
        Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: 280_000_000)
            guard let self, self.generation == current else { return }
            self.tearDown()
        }
    }

    private func tearDown() {
        if let screenObserver { NotificationCenter.default.removeObserver(screenObserver) }
        screenObserver = nil
        for panel in panels {
            panel.orderOut(nil)
            panel.contentView = nil
            panel.close()
        }
        let wasVisible = !panels.isEmpty
        panels.removeAll()
        presentation = nil
        isAboveLockScreen = false
        if wasVisible { player.setHighFrequency(false) }
    }

    private func rebuild() {
        guard isVisible, let presentation else { return }
        for panel in panels {
            panel.orderOut(nil)
            panel.close()
        }
        panels.removeAll()
        self.presentation = presentation
        buildPanels()
    }

    private func buildPanels() {
        guard let presentation else { return }
        let screens: [NSScreen]
        if settings.allDisplays {
            screens = NSScreen.screens
        } else {
            screens = [NSScreen.main ?? NSScreen.screens.first].compactMap { $0 }
        }

        var lifted = true
        for screen in screens {
            let panel = LockWidgetPanel(screen: screen)
            panel.allowsKey = presentation == .preview
            let root = LockWidgetView(overlay: overlay, player: player, lyrics: lyrics, settings: settings,
                                      presentation: presentation,
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
            panel.orderFrontRegardless()

            if presentation == .lockScreen {
                let attached = lockScreenLayer.map { $0.isAvailable && $0.attach(panel) } ?? false
                lifted = lifted && attached
                panel.orderFrontRegardless()
            }
            panels.append(panel)
        }
        isAboveLockScreen = presentation == .lockScreen && lifted && !screens.isEmpty
    }

    /// After unlocking (when the widget wasn't above the lock screen) let it
    /// take keyboard focus so Esc / Space / arrows work.
    func makeInteractive() {
        guard isVisible else { return }
        panels.forEach { $0.allowsKey = true }
        NSApp.activate()
        panels.first?.makeKeyAndOrderFront(nil)
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
