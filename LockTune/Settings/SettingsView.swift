import AppKit
import SwiftUI

struct SettingsView: View {
    @ObservedObject var coordinator: AppCoordinator
    @ObservedObject var settings: AppSettings
    @ObservedObject var player: NowPlayingController

    var body: some View {
        TabView {
            StatusTab(coordinator: coordinator, settings: settings, player: player)
                .tabItem { Label("Status", systemImage: "checkmark.circle") }
            GeneralSettings(coordinator: coordinator, settings: settings)
                .tabItem { Label("General", systemImage: "gearshape") }
            AppearanceSettings(coordinator: coordinator, settings: settings)
                .tabItem { Label("Appearance", systemImage: "paintpalette") }
        }
        .padding(.top, 8)
        .frame(width: 560, height: 560)
    }
}

// MARK: - Status

private struct StatusTab: View {
    @ObservedObject var coordinator: AppCoordinator
    @ObservedObject var settings: AppSettings
    @ObservedObject var player: NowPlayingController

    var body: some View {
        Form {
            Section {
                HStack(spacing: 14) {
                    Image(nsImage: NSApp.applicationIconImage)
                        .resizable()
                        .frame(width: 56, height: 56)
                    VStack(alignment: .leading, spacing: 4) {
                        Text("LockTune is running").font(.title3.weight(.semibold))
                        Text("Play music in Spotify or Music, then lock your Mac (⌃⌘Q). The player widget appears for your lock screen, with lyrics on the left toolbar.")
                            .font(.callout)
                            .foregroundStyle(.secondary)
                    }
                }
                .padding(.vertical, 4)
            }

            Section("Checklist") {
                StatusRow(ok: player.hasTrack,
                          title: "Music detected",
                          detail: player.track.map { "\($0.title) — \($0.artist) · \(player.sourceName ?? "")" }
                              ?? "Start a song in Spotify or Apple Music.")
                ForEach(player.providers.map { $0.displayName }, id: \.self) { name in
                    // If we're reading this player's track, permission is clearly granted.
                    let status: AutomationStatus = player.sourceName == name
                        ? .allowed
                        : (player.automationStatus[name] ?? .notDetermined)
                    StatusRow(ok: status == .allowed,
                              title: "Permission to control \(name): \(status.title)",
                              detail: permissionHint(status, name: name, lastError: player.lastErrors[name]))
                }
                StatusRow(ok: coordinator.widget.lockScreenLayer?.isAvailable == true,
                          title: "Showing on the lock screen",
                          detail: coordinator.widget.lockScreenLayer?.isAvailable == true
                              ? "Ready. Lock your Mac with music playing."
                              : "This macOS doesn't offer the needed window-server functions. The widget is shown right after you unlock.")
                StatusRow(ok: ScreenLocker.canLockDirectly,
                          title: "Lock Screen Now",
                          detail: ScreenLocker.canLockDirectly
                              ? "Locks immediately, like ⌃⌘Q."
                              : "Only turns the display off; set “Require password” to Immediately.")

                HStack {
                    Button("Ask for Permission") { player.requestAutomationAccess() }
                    Button("Reset & Ask Again") { player.requestAutomationAccess(resetFirst: true) }
                    Button("Open Automation Settings") { AutomationPermission.openSystemSettings() }
                }
                Text("Spotify or Music must be open for macOS to ask.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section("Diagnostics") {
                ForEach(player.providers.map { $0.displayName }, id: \.self) { name in
                    LabeledContent(name) {
                        Text(player.diagnostics[name] ?? "—")
                            .font(.system(.caption, design: .monospaced))
                            .textSelection(.enabled)
                            .multilineTextAlignment(.trailing)
                    }
                }
                HStack {
                    Button("Refresh") { player.poll() }
                    Button("Copy Diagnostics") {
                        let text = diagnosticsText()
                        NSPasteboard.general.clearContents()
                        NSPasteboard.general.setString(text, forType: .string)
                    }
                }
            }

            Section("Try it") {
                HStack {
                    Button("Preview Widget") { coordinator.togglePreview() }
                        .keyboardShortcut(.defaultAction)
                    Button("Lock Screen Now") { coordinator.lockNow() }
                }
                Text("The menu-bar icon (♪ with a house) can be hidden behind the notch on MacBooks. Open LockTune again from Applications to get back to this window.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .task {
            // Keep the checklist live while the window is open.
            while !Task.isCancelled {
                player.poll()
                player.refreshAutomationStatus()
                try? await Task.sleep(nanoseconds: 2_000_000_000)
            }
        }
    }

    private func diagnosticsText() -> String {
        var lines = ["LockTune \(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "?")",
                     "macOS \(ProcessInfo.processInfo.operatingSystemVersionString)",
                     "locked: \(coordinator.isScreenLocked)",
                     "lock-screen layer: \(coordinator.widget.lockScreenLayer?.isAvailable == true)",
                     "lock function: \(ScreenLocker.canLockDirectly)",
                     "track: \(player.track.map { "\($0.title) — \($0.artist)" } ?? "none")"]
        for provider in player.providers {
            let name = provider.displayName
            lines.append("\(name): \(player.diagnostics[name] ?? "—") · permission: \((player.automationStatus[name] ?? .notDetermined).title)")
        }
        return lines.joined(separator: "\n")
    }

    private func permissionHint(_ status: AutomationStatus, name: String, lastError: Int?) -> String {
        switch status {
        case .allowed: return "OK."
        case .notRunning: return "Open \(name) to check."
        case .denied: return "Denied earlier. Click “Reset & Ask Again”, or turn LockTune → \(name) on in System Settings → Privacy & Security → Automation."
        case .notDetermined: return "Click “Ask for Permission” and then OK in the macOS dialog."
        case .unknown(let code): return "macOS returned \(code)\(lastError.map { ", last script error \($0)" } ?? "")."
        }
    }
}

private struct StatusRow: View {
    let ok: Bool
    let title: String
    let detail: String

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: ok ? "checkmark.circle.fill" : "exclamationmark.circle.fill")
                .foregroundStyle(ok ? Color.green : Color.orange)
                .font(.system(size: 16))
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.body.weight(.medium))
                Text(detail).font(.caption).foregroundStyle(.secondary)
            }
        }
    }
}

// MARK: - General

private struct GeneralSettings: View {
    @ObservedObject var coordinator: AppCoordinator
    @ObservedObject var settings: AppSettings

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

            Section("Lock screen") {
                Toggle("Show the music widget when the Mac is locked", isOn: $settings.lockWidgetEnabled)
                Toggle("Keep the display on while locked and music plays", isOn: $settings.keepDisplayAwake)
                Toggle("Lock after being idle while music plays", isOn: $settings.idleEnabled)
                Stepper(value: $settings.idleMinutes, in: 1...120) {
                    Text("Idle time: \(settings.idleMinutes) min")
                }
                .disabled(!settings.idleEnabled)
                Toggle("Show on all displays", isOn: $settings.allDisplays)
            }

            Section("Keyboard shortcut") {
                Toggle("Global shortcut", isOn: $settings.hotkeyEnabled)
                LabeledContent("Lock screen now") {
                    HotkeyRecorderView(hotkey: $settings.hotkey) { recording in
                        recording ? coordinator.suspendHotkey() : coordinator.resumeHotkey()
                    }
                    .disabled(!settings.hotkeyEnabled)
                }
                if coordinator.hotkeyUnavailable {
                    Text("This shortcut is already used by another app. Pick a different one.")
                        .font(.caption).foregroundStyle(.orange)
                }
                Text("The shortcut turns the display off; it locks when System Settings → Lock Screen → “Require password” is set to Immediately. ⌃⌘Q always locks.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section("Music source") {
                Picker("Player", selection: $settings.preferredSource) {
                    ForEach(PreferredSource.allCases) { Text($0.title).tag($0) }
                }
            }
        }
        .formStyle(.grouped)
        .onAppear { launchAtLogin = LaunchAtLogin.isEnabled }
    }
}

// MARK: - Appearance

private struct AppearanceSettings: View {
    @ObservedObject var coordinator: AppCoordinator
    @ObservedObject var settings: AppSettings

    var body: some View {
        Form {
            Section("Widget") {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Height on the lock screen")
                    Slider(value: Binding(get: { 1 - settings.widgetBottomInset },
                                          set: { settings.widgetBottomInset = 1 - $0 }),
                           in: 0.4...0.95) {
                        EmptyView()
                    } minimumValueLabel: {
                        Text("Higher").font(.caption)
                    } maximumValueLabel: {
                        Text("Lower").font(.caption)
                    }
                    Text("Default sits just above your name and password field. Use Preview Widget to check.")
                        .font(.caption).foregroundStyle(.secondary)
                }
                Toggle("Artwork wallpaper behind the widget", isOn: $settings.lockWallpaper)
                Picker("Wallpaper style", selection: $settings.backgroundStyle) {
                    ForEach(BackgroundStyle.allCases) { Text($0.title).tag($0) }
                }
                .disabled(!settings.lockWallpaper)
                Text("Off: only the album art, controls and lyrics float over your normal lock screen. On: the screen is filled with the artwork, like in the reference video.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section("Clock (wallpaper mode & preview)") {
                Picker("Font", selection: $settings.clockFont) {
                    ForEach(ClockFontStyle.allCases) { Text($0.title).tag($0) }
                }
                Picker("Weight", selection: $settings.clockWeight) {
                    ForEach(ClockWeight.allCases) { Text($0.title).tag($0) }
                }
                HStack {
                    Spacer()
                    ClockView(size: 56, style: settings.clockFont, weight: settings.clockWeight)
                    Spacer()
                }
                .padding(.vertical, 4)
            }

            Section("Lyrics") {
                Toggle("Load lyrics from LRCLIB", isOn: $settings.lyricsEnabled)
                Picker("Lyrics position", selection: $settings.lyricsPosition) {
                    ForEach(LyricsPosition.allCases) { Text($0.title).tag($0) }
                }
                Toggle("Highlight word by word (like Apple Music)", isOn: $settings.wordByWord)
                Text("Uses per-word timing when the lyrics have it; otherwise the timing within a line is estimated.")
                    .font(.caption).foregroundStyle(.secondary)
                Button("Clear Lyrics Cache") { coordinator.lyrics.clearCache() }
            }
        }
        .formStyle(.grouped)
    }
}
