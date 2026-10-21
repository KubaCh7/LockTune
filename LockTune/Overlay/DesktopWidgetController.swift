import AppKit
import Combine
import SwiftUI

/// Shows the turntable on the desktop: above the wallpaper and desktop icons,
/// below every app window. Drag it anywhere; the position is remembered.
@MainActor
final class DesktopWidgetController {
    private let player: NowPlayingController
    private let settings: AppSettings
    private var panel: NSPanel?
    private var dragStart: (mouse: NSPoint, origin: NSPoint)?
    private var cancellables = Set<AnyCancellable>()
    private static let originKey = "turntableOrigin"

    init(player: NowPlayingController, settings: AppSettings) {
        self.player = player
        self.settings = settings
    }

    func start() {
        settings.$turntableEnabled
            .sink { [weak self] enabled in
                Task { @MainActor in enabled ? self?.show() : self?.hide() }
            }
            .store(in: &cancellables)
        settings.$turntableScale
            .dropFirst()
            .sink { [weak self] _ in
                Task { @MainActor in self?.resize() }
            }
            .store(in: &cancellables)
    }

    private var contentSize: NSSize {
        let scale = CGFloat(settings.turntableScale)
        let base = TurntableView.baseSize
        return NSSize(width: base.width * scale + 20,
                      height: (base.height + TurntableView.infoHeight + 10) * scale + 20)
    }

    private func show() {
        guard panel == nil else { return }
        let size = contentSize
        let panel = NSPanel(contentRect: NSRect(origin: savedOrigin(for: size), size: size),
                            styleMask: [.borderless, .nonactivatingPanel],
                            backing: .buffered, defer: false)
        // Desktop-icon level + 1: on the wallpaper, under all normal windows.
        panel.level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.desktopIconWindow)) + 1)
        panel.collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle]
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.isReleasedWhenClosed = false
        panel.hidesOnDeactivate = false
        panel.isMovable = false

        let root = TurntableView(player: player, settings: settings,
                                 onDragChanged: { [weak self] in self?.dragChanged() },
                                 onDragEnded: { [weak self] in self?.dragEnded() })
        let hosting = FirstMouseHostingView(rootView: root)
        if #available(macOS 13.0, *) { hosting.sizingOptions = [] }
        panel.contentView = hosting
        panel.orderFrontRegardless()
        self.panel = panel
    }

    private func hide() {
        panel?.orderOut(nil)
        panel?.contentView = nil
        panel?.close()
        panel = nil
    }

    private func resize() {
        guard let panel else { return }
        var frame = panel.frame
        let size = contentSize
        frame.origin.y += frame.height - size.height // keep the top edge in place
        frame.size = size
        panel.setFrame(frame, display: true)
        saveOrigin()
    }

    /// Puts the widget back at its default spot.
    func resetPosition() {
        UserDefaults.standard.removeObject(forKey: Self.originKey)
        guard let panel else { return }
        panel.setFrameOrigin(defaultOrigin(for: panel.frame.size))
    }

    // MARK: Dragging (window follows the mouse in screen coordinates)

    private func dragChanged() {
        guard let panel else { return }
        let mouse = NSEvent.mouseLocation
        if dragStart == nil { dragStart = (mouse, panel.frame.origin) }
        guard let start = dragStart else { return }
        panel.setFrameOrigin(NSPoint(x: start.origin.x + mouse.x - start.mouse.x,
                                     y: start.origin.y + mouse.y - start.mouse.y))
    }

    private func dragEnded() {
        dragStart = nil
        saveOrigin()
    }

    // MARK: Position

    private func saveOrigin() {
        guard let origin = panel?.frame.origin else { return }
        UserDefaults.standard.set([origin.x, origin.y], forKey: Self.originKey)
    }

    private func savedOrigin(for size: NSSize) -> NSPoint {
        if let values = UserDefaults.standard.array(forKey: Self.originKey) as? [Double], values.count == 2 {
            let origin = NSPoint(x: values[0], y: values[1])
            // Only if it's still on a connected screen.
            if NSScreen.screens.contains(where: { $0.frame.intersects(NSRect(origin: origin, size: size)) }) {
                return origin
            }
        }
        return defaultOrigin(for: size)
    }

    /// Top-right of the main screen, like the photo.
    private func defaultOrigin(for size: NSSize) -> NSPoint {
        let visible = (NSScreen.main ?? NSScreen.screens.first)?.visibleFrame
            ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
        return NSPoint(x: visible.maxX - size.width - 40, y: visible.maxY - size.height - 30)
    }
}
