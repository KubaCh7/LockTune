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
        MenuBarExtra {
            MenuBarMenu(coordinator: AppCoordinator.shared,
                        player: AppCoordinator.shared.nowPlaying,
                        settings: AppCoordinator.shared.settings,
                        updater: Updater.shared)
        } label: {
            Image(systemName: "music.note.house")
        }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    private static let welcomeKey = "didShowWelcome2"

    func applicationDidFinishLaunching(_ notification: Notification) {
        // LSUIElement = YES in Info.plist keeps us out of the Dock and ⌘⇥.
        AppCoordinator.shared.start()
        Updater.shared.start()

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

/// Contents of the menu-bar item.
struct MenuBarMenu: View {
    @ObservedObject var coordinator: AppCoordinator
    @ObservedObject var player: NowPlayingController
    @ObservedObject var settings: AppSettings
    @ObservedObject var updater: Updater

    var body: some View {
        if let track = player.track {
            Text("\(player.isPlaying ? "▶︎" : "❚❚")  \(track.title) — \(track.artist)")
        } else {
            Text("Nothing playing")
        }

        Divider()

        Button(lockTitle) { coordinator.afterMenuCloses { coordinator.lockNow() } }
        Button("Preview Widget") { coordinator.afterMenuCloses { coordinator.togglePreview() } }

        Divider()

        Button("Status & Settings…") { SettingsWindowController.shared.show() }
            .keyboardShortcut(",", modifiers: .command)

        if let build = updater.availableBuild {
            Button("Install Update (build \(build))…") { updater.installUpdate() }
        } else {
            Button("Check for Updates…") { Task { await updater.check(userInitiated: true) } }
        }

        Button("Quit LockTune") { NSApp.terminate(nil) }
            .keyboardShortcut("q", modifiers: .command)
    }

    private var lockTitle: String {
        settings.hotkeyEnabled
            ? "Lock Screen Now   \(settings.hotkey.displayString)"
            : "Lock Screen Now"
    }
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
            let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 560, height: 560),
                                  styleMask: [.titled, .closable, .miniaturizable],
                                  backing: .buffered, defer: false)
            window.title = "LockTune"
            window.isReleasedWhenClosed = false
            window.contentView = NSHostingView(rootView: root)
            window.center()
            self.window = window
        }
        NSApp.activate()
        window?.makeKeyAndOrderFront(nil)
    }
}
