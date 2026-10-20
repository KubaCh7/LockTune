import AppKit
import SwiftUI

struct SettingsView: View {
    @ObservedObject var coordinator: AppCoordinator
    @ObservedObject var settings: AppSettings
    @ObservedObject var player: NowPlayingController

    var body: some View {
        TabView {
            GeneralSettings(coordinator: coordinator, settings: settings, player: player)
                .tabItem { Label("General", systemImage: "gearshape") }
            TriggerSettings(settings: settings)
                .tabItem { Label("Triggers", systemImage: "bolt.fill") }
            AppearanceSettings(coordinator: coordinator, settings: settings)
                .tabItem { Label("Appearance", systemImage: "paintpalette") }
        }
        .frame(width: 520)
        .fixedSize(horizontal: false, vertical: true)
    }
}

// MARK: - General

private struct GeneralSettings: View {
    @ObservedObject var coordinator: AppCoordinator
    @ObservedObject var settings: AppSettings
    @ObservedObject var player: NowPlayingController

    @State private var launchAtLogin = LaunchAtLogin.isEnabled
    @State private var launchError: String?

    var body: some View {
        Form {
            Section {
                Toggle("Launch at login", isOn: Binding(
                    get: { launchAtLogin },
                    set: { newValue in
                        do {
                            try LaunchAtLogin.set(newValue)
                            launchError = nil
                        } catch {
                            launchError = error.localizedDescription
                        }
                        launchAtLogin = LaunchAtLogin.isEnabled
                    }))
                if let launchError {
                    Text(launchError).font(.caption).foregroundStyle(.red)
                }
            }

            Section("Keyboard shortcut") {
                Toggle("Global shortcut", isOn: $settings.hotkeyEnabled)
                LabeledContent("Show / hide lock screen") {
                    HotkeyRecorderView(hotkey: $settings.hotkey) { recording in
                        recording ? coordinator.suspendHotkey() : coordinator.resumeHotkey()
                    }
                    .disabled(!settings.hotkeyEnabled)
                }
                if coordinator.hotkeyUnavailable {
                    Text("This shortcut is already used by another app. Pick a different one.")
                        .font(.caption).foregroundStyle(.orange)
                }
                Text("While the lock screen is up: Space play/pause · ←/→ previous/next · L lyrics · Esc or swipe up to close.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section("Music source") {
                Picker("Player", selection: $settings.preferredSource) {
                    ForEach(PreferredSource.allCases) { Text($0.title).tag($0) }
                }
                LabeledContent("Now playing") {
                    if let track = player.track {
                        Text("\(track.title) — \(track.artist) (\(player.sourceName ?? ""))")
                            .lineLimit(1)
                    } else {
                        Text("Nothing").foregroundStyle(.secondary)
                    }
                }
                if player.automationDenied {
                    Text("LockTune isn't allowed to control Music/Spotify. Enable it in System Settings → Privacy & Security → Automation.")
                        .font(.caption).foregroundStyle(.orange)
                }
                HStack {
                    Button("Request Access") { player.requestAutomationAccess() }
                    Button("Open Automation Settings") {
                        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Automation") {
                            NSWorkspace.shared.open(url)
                        }
                    }
                }
            }
        }
        .formStyle(.grouped)
        .onAppear { launchAtLogin = LaunchAtLogin.isEnabled }
    }
}

// MARK: - Triggers

private struct TriggerSettings: View {
    @ObservedObject var settings: AppSettings

    var body: some View {
        Form {
            Section("Show automatically") {
                Toggle("After being idle while music plays", isOn: $settings.idleEnabled)
                Stepper(value: $settings.idleMinutes, in: 1...120) {
                    Text("Idle time: \(settings.idleMinutes) min")
                }
                .disabled(!settings.idleEnabled)

                Toggle("When the screen locks", isOn: $settings.showOnLock)
                Toggle("When the display goes to sleep", isOn: $settings.showOnDisplaySleep)
                Text("macOS draws its lock screen above every app, so the overlay appears behind it and is what you see right after unlocking or waking the display.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section("Behaviour") {
                Picker("When nothing is playing", selection: $settings.emptyBehavior) {
                    ForEach(EmptyBehavior.allCases) { Text($0.title).tag($0) }
                }
                Toggle("Any other key closes the overlay", isOn: $settings.dismissOnAnyKey)
                Toggle("Cover all displays", isOn: $settings.allDisplays)
            }

            Section {
                Text("The overlay is visual only — it does not lock your Mac. For real security turn on System Settings → Lock Screen → \"Require password after screen saver begins or display is turned off: Immediately\".")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }
}

// MARK: - Appearance

private struct AppearanceSettings: View {
    @ObservedObject var coordinator: AppCoordinator
    @ObservedObject var settings: AppSettings

    var body: some View {
        Form {
            Section("Clock") {
                Picker("Font", selection: $settings.clockFont) {
                    ForEach(ClockFontStyle.allCases) { Text($0.title).tag($0) }
                }
                Picker("Weight", selection: $settings.clockWeight) {
                    ForEach(ClockWeight.allCases) { Text($0.title).tag($0) }
                }
                HStack {
                    Spacer()
                    ClockView(size: 64, style: settings.clockFont, weight: settings.clockWeight)
                    Spacer()
                }
                .padding(.vertical, 6)
            }

            Section("Background") {
                Picker("Style", selection: $settings.backgroundStyle) {
                    ForEach(BackgroundStyle.allCases) { Text($0.title).tag($0) }
                }
            }

            Section("Lyrics") {
                Toggle("Load lyrics from LRCLIB", isOn: $settings.lyricsEnabled)
                Picker("Layout", selection: $settings.lyricsLayout) {
                    ForEach(LyricsLayout.allCases) { Text($0.title).tag($0) }
                }
                Button("Clear Lyrics Cache") { coordinator.lyrics.clearCache() }
            }
        }
        .formStyle(.grouped)
    }
}
