import AppKit
import Carbon.HIToolbox
import SwiftUI

/// Borderless, full-screen window that sits above the menu bar and Dock.
final class OverlayWindow: NSWindow {
    /// Return true to swallow the event.
    var keyHandler: ((NSEvent) -> Bool)?
    var scrollHandler: ((NSEvent) -> Bool)?

    init(screen: NSScreen) {
        super.init(contentRect: screen.frame, styleMask: [.borderless], backing: .buffered, defer: false)
        // .screenSaver (1000) is above the menu bar (24), Dock (20) and pop-up menus.
        level = .screenSaver
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        isOpaque = false
        backgroundColor = .clear // transparent while swiping up
        hasShadow = false
        isReleasedWhenClosed = false
        acceptsMouseMovedEvents = true
        animationBehavior = .none
        isMovable = false
        setFrame(screen.frame, display: false)
    }

    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }

    // Intercept keys and scrolling before SwiftUI sees them, so e.g. Space
    // never "clicks" a focused button.
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

/// Shows the lock-screen overlay on every display and handles its input.
@MainActor
final class OverlayWindowController {
    private let overlay: OverlayState
    private let player: NowPlayingController
    private let lyrics: LyricsController
    private let settings: AppSettings

    private var windows: [OverlayWindow] = []
    private var previousApp: NSRunningApplication?
    private var savedPresentationOptions: NSApplication.PresentationOptions = []
    private var screenObserver: NSObjectProtocol?

    var isVisible: Bool { !windows.isEmpty }

    init(overlay: OverlayState, player: NowPlayingController, lyrics: LyricsController, settings: AppSettings) {
        self.overlay = overlay
        self.player = player
        self.lyrics = lyrics
        self.settings = settings
        overlay.onDismissed = { [weak self] in self?.close() }
    }

    // MARK: Show / hide

    func show() {
        guard windows.isEmpty else { return }
        overlay.reset()

        let front = NSWorkspace.shared.frontmostApplication
        previousApp = front?.processIdentifier == ProcessInfo.processInfo.processIdentifier ? nil : front

        buildWindows()

        NSApp.activate()
        windows.first?.makeKeyAndOrderFront(nil)

        savedPresentationOptions = NSApp.presentationOptions
        // hideMenuBar requires hideDock; disableProcessSwitching blocks ⌘⇥.
        NSApp.presentationOptions = [.hideDock, .hideMenuBar, .disableProcessSwitching, .disableHideApplication]

        player.setHighFrequency(true)
        lyrics.reloadIfNeeded()
        NSCursor.setHiddenUntilMouseMoves(true)

        screenObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.rebuildWindows() }
        }

        // Let the first frame render with isPresented == false, then animate in.
        Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: 16_000_000)
            self?.overlay.present()
        }
    }

    /// Animated dismissal (slides up), then `close()`.
    func dismiss() {
        guard isVisible else { return }
        overlay.requestDismiss()
    }

    /// Re-focuses the overlay, e.g. after the user unlocked the Mac.
    func reactivate() {
        guard isVisible else { return }
        NSApp.activate()
        windows.first?.makeKeyAndOrderFront(nil)
        windows.forEach { $0.orderFrontRegardless() }
    }

    func close() {
        guard isVisible else { return }
        if let screenObserver { NotificationCenter.default.removeObserver(screenObserver) }
        screenObserver = nil

        tearDownWindows()
        NSApp.presentationOptions = savedPresentationOptions
        player.setHighFrequency(false)

        if let previousApp, !previousApp.isTerminated {
            previousApp.activate(options: [])
        } else if !NSApp.windows.contains(where: { $0.isVisible && !($0 is OverlayWindow) && $0.level == .normal }) {
            NSApp.hide(nil)
        }
        previousApp = nil
    }

    // MARK: Windows

    private func buildWindows() {
        let screens: [NSScreen]
        if settings.allDisplays {
            screens = NSScreen.screens
        } else {
            let mouse = NSEvent.mouseLocation
            screens = [NSScreen.screens.first { NSMouseInRect(mouse, $0.frame, false) } ?? NSScreen.main]
                .compactMap { $0 }
        }

        for screen in screens {
            let window = OverlayWindow(screen: screen)
            let root = LockScreenView(overlay: overlay, player: player, lyrics: lyrics, settings: settings)
            let hosting = NSHostingView(rootView: root)
            hosting.sizingOptions = []
            window.contentView = hosting
            window.keyHandler = { [weak self] event in
                MainActor.assumeIsolated { self?.handleKey(event) ?? false }
            }
            window.scrollHandler = { [weak self] event in
                MainActor.assumeIsolated { self?.handleScroll(event) ?? false }
            }
            window.setFrame(screen.frame, display: true)
            window.orderFrontRegardless()
            windows.append(window)
        }
    }

    private func tearDownWindows() {
        for window in windows {
            window.orderOut(nil)
            window.contentView = nil
            window.close()
        }
        windows.removeAll()
    }

    private func rebuildWindows() {
        guard isVisible else { return }
        tearDownWindows()
        buildWindows()
        windows.first?.makeKeyAndOrderFront(nil)
    }

    // MARK: Input

    private func handleKey(_ event: NSEvent) -> Bool {
        let repeatable = event.isARepeat
        switch Int(event.keyCode) {
        case kVK_Escape, kVK_UpArrow:
            overlay.requestDismiss()
        case kVK_Space:
            if !repeatable { player.playPause() }
        case kVK_LeftArrow:
            if !repeatable { player.previous() }
        case kVK_RightArrow:
            if !repeatable { player.next() }
        case kVK_ANSI_L:
            if !repeatable, player.hasTrack { overlay.toggleLyrics() }
        default:
            // Modifier-only presses arrive as flagsChanged, not keyDown.
            if !repeatable, settings.dismissOnAnyKey { overlay.requestDismiss() }
        }
        return true
    }

    private func handleScroll(_ event: NSEvent) -> Bool {
        overlay.handleScroll(event, hasSyncedLyrics: lyrics.syncedLines != nil)
    }
}
