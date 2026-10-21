import AppKit
import SwiftUI

@main
struct LockTuneApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    init() {
        // CI runs `LockTune --self-test`; this exits before any UI starts.
        SelfTest.runIfRequested()
    }

    var body: some Scene {
        // The menu-bar item is an AppKit NSStatusItem (see StatusItemController),
        // which works on macOS 12; SwiftUI's MenuBarExtra needs macOS 13.
        // An app needs at least one scene, and this one never shows a window.
        Settings { EmptyView() }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    private static let welcomeKey = "didShowWelcome2"
    private var statusItem: StatusItemController?

    func applicationDidFinishLaunching(_ notification: Notification) {
        // LSUIElement = YES in Info.plist keeps us out of the Dock and ⌘⇥.
        AppCoordinator.shared.start()
        Updater.shared.start()
        statusItem = StatusItemController()

        // First launch: show the status window so it's obvious the app runs
        // (on notched MacBooks the menu-bar icon can hide behind the notch).
        if !UserDefaults.standard.bool(forKey: Self.welcomeKey) {
            UserDefaults.standard.set(true, forKey: Self.welcomeKey)
            SettingsWindowController.shared.show()
        }
    }

    /// Opening LockTune again from Applications/Launchpad brings up the window.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        SettingsWindowController.shared.show()
        return true
    }
}

/// The ♪ menu-bar item and its menu (rebuilt each time it opens).
@MainActor
final class StatusItemController: NSObject, NSMenuDelegate {
    private let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
    private let menu = NSMenu()

    override init() {
        super.init()
        let image = NSImage(systemSymbolName: "music.note.house", accessibilityDescription: "LockTune")
        image?.isTemplate = true
        item.button?.image = image
        menu.delegate = self
        item.menu = menu
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        let coordinator = AppCoordinator.shared
        let player = coordinator.nowPlaying
        let settings = coordinator.settings
        let updater = Updater.shared

        let nowPlaying = player.track.map { "\(player.isPlaying ? "▶︎" : "❚❚")  \($0.title) — \($0.artist)" }
            ?? "Nothing playing"
        let header = NSMenuItem(title: nowPlaying, action: nil, keyEquivalent: "")
        header.isEnabled = false
        menu.addItem(header)
        menu.addItem(.separator())

        let lockTitle = settings.hotkeyEnabled
            ? "Lock Screen Now   \(settings.hotkey.displayString)"
            : "Lock Screen Now"
        menu.addItem(ClosureMenuItem(lockTitle) { coordinator.afterMenuCloses { coordinator.lockNow() } })
        menu.addItem(ClosureMenuItem("Preview Widget") { coordinator.afterMenuCloses { coordinator.togglePreview() } })
        menu.addItem(.separator())

        menu.addItem(ClosureMenuItem("Status & Settings…", key: ",") { SettingsWindowController.shared.show() })
        if let build = updater.availableBuild {
            menu.addItem(ClosureMenuItem("Install Update (build \(build))…") { updater.installUpdate() })
        } else {
            menu.addItem(ClosureMenuItem("Check for Updates…") { Task { await updater.check(userInitiated: true) } })
        }
        menu.addItem(.separator())
        menu.addItem(ClosureMenuItem("Quit LockTune", key: "q") { NSApp.terminate(nil) })
    }
}

/// NSMenuItem that runs a closure.
final class ClosureMenuItem: NSMenuItem {
    private let handler: () -> Void

    init(_ title: String, key: String = "", handler: @escaping () -> Void) {
        self.handler = handler
        super.init(title: title, action: #selector(fire), keyEquivalent: key)
        target = self
    }

    required init(coder: NSCoder) {
        fatalError("init(coder:) is not used")
    }

    @objc private func fire() { handler() }
}

/// Our own settings window (the SwiftUI `Settings` scene can't be opened from
/// AppKit code on macOS 14+, and we need that for first launch / reopen).
@MainActor
final class SettingsWindowController {
    static let shared = SettingsWindowController()
    private var window: NSWindow?

    func show() {
        if window == nil {
            let coordinator = AppCoordinator.shared
            let root = SettingsView(coordinator: coordinator,
                                    settings: coordinator.settings,
                                    player: coordinator.nowPlaying)
            let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 580, height: 640),
                                  styleMask: [.titled, .closable, .miniaturizable, .fullSizeContentView],
                                  backing: .buffered, defer: false)
            window.title = "LockTune"
            window.isReleasedWhenClosed = false
            // Glass window: transparent title bar, see-through background
            // (the SwiftUI view draws a behind-window blur + artwork tint).
            window.titlebarAppearsTransparent = true
            window.titleVisibility = .hidden
            window.isOpaque = false
            window.backgroundColor = .clear
            // Not movable by background: that swallowed clicks on the tab bar.
            window.identifier = NSUserInterfaceItemIdentifier("LockTuneSettings")
            window.appearance = NSAppearance(named: .darkAqua)
            window.contentView = NSHostingView(rootView: root)
            window.center()
            self.window = window
        }
        NSApp.activateCompat()
        window?.makeKeyAndOrderFront(nil)
    }
}
