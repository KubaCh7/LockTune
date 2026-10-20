import AppKit
import SwiftUI

@main
struct LockTuneApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    var body: some Scene {
        MenuBarExtra {
            MenuBarMenu(coordinator: AppCoordinator.shared,
                        player: AppCoordinator.shared.nowPlaying,
                        settings: AppCoordinator.shared.settings)
        } label: {
            Image(systemName: "music.note.house")
        }

        Settings {
            SettingsView(coordinator: AppCoordinator.shared,
                         settings: AppCoordinator.shared.settings,
                         player: AppCoordinator.shared.nowPlaying)
        }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        // LSUIElement = YES in Info.plist keeps us out of the Dock and ⌘⇥.
        AppCoordinator.shared.start()
    }
}

/// Contents of the menu-bar item.
struct MenuBarMenu: View {
    @ObservedObject var coordinator: AppCoordinator
    @ObservedObject var player: NowPlayingController
    @ObservedObject var settings: AppSettings
    @Environment(\.openSettings) private var openSettings

    var body: some View {
        if let track = player.track {
            Text("\(player.isPlaying ? "▶︎" : "❚❚")  \(track.title) — \(track.artist)")
        } else {
            Text("Nothing playing")
        }

        Divider()

        Button(showTitle) { coordinator.showOverlayFromMenu() }

        Button("Settings…") {
            NSApp.activate()
            openSettings()
        }
        .keyboardShortcut(",", modifiers: .command)

        Divider()

        Button("Quit LockTune") { NSApp.terminate(nil) }
            .keyboardShortcut("q", modifiers: .command)
    }

    private var showTitle: String {
        settings.hotkeyEnabled
            ? "Show Lock Screen Now   \(settings.hotkey.displayString)"
            : "Show Lock Screen Now"
    }
}
