import AppKit
import SwiftUI
import UniformTypeIdentifiers

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

            Section("Look") {
                GlassPicker(settings: settings)
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

/// Liquid Glass picker, shown on both the Status and the General tab.
private struct GlassPicker: View {
    @ObservedObject var settings: AppSettings

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Picker("Liquid Glass", selection: $settings.glassLevel) {
                ForEach(GlassLevel.allCases) { Text($0.title).tag($0) }
            }
            .pickerStyle(.segmented)
            Text(detail).font(.caption).foregroundStyle(.secondary)
        }
    }

    private var detail: String {
        let real = ProcessInfo.processInfo.isOperatingSystemAtLeast(OperatingSystemVersion(majorVersion: 26, minorVersion: 0, patchVersion: 0))
        switch settings.glassLevel {
        case .off: return "Classic frosted blur."
        case .normal: return "Glass player card and toolbar." + (real ? "" : " (Imitation — real Liquid Glass needs macOS 26+.)")
        case .mega: return "Glass everywhere: player card, every button, a glass frame around the artwork and a glass panel behind the lyrics."
            + (real ? "" : " (Imitation — real Liquid Glass needs macOS 26+.)")
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
    @ObservedObject private var updater = Updater.shared
    @AppStorage(Updater.autoCheckKey) private var autoCheckUpdates = true

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

            Section("Look") {
                GlassPicker(settings: settings)
            }

            Section("Updates") {
                LabeledContent("Installed version", value: updater.versionString)
                Toggle("Check for updates automatically", isOn: $autoCheckUpdates)
                HStack {
                    if let build = updater.availableBuild {
                        Button("Install Update (build \(build))") { updater.installUpdate() }
                            .keyboardShortcut(.defaultAction)
                    } else {
                        Button("Check for Updates") { Task { await updater.check(userInitiated: true) } }
                    }
                    Spacer()
                    Text(updateStatus).font(.caption).foregroundStyle(.secondary)
                }
            }

            Section("Lock screen") {
                Toggle("Show the music widget when the Mac is locked", isOn: $settings.lockWidgetEnabled)
                VStack(alignment: .leading, spacing: 4) {
                    Text("Screen stays on after locking: \(AppSettings.awakeTitle(settings.awakeDuration))")
                    Slider(value: Binding(get: { Double(settings.awakeDurationIndex) },
                                          set: { settings.awakeDurationIndex = Int($0.rounded()) }),
                           in: 0...Double(AppSettings.awakeDurations.count - 1), step: 1) {
                        EmptyView()
                    } minimumValueLabel: {
                        Text("Default").font(.caption)
                    } maximumValueLabel: {
                        Text("Always").font(.caption)
                    }
                    Text("Only while music is playing. With “macOS default” the display turns off after a few seconds as usual.")
                        .font(.caption).foregroundStyle(.secondary)
                }
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

    private var updateStatus: String {
        switch updater.state {
        case .idle: return ""
        case .checking: return "Checking…"
        case .upToDate: return "Up to date"
        case .available(let build): return "Build \(build) available"
        case .downloading: return "Downloading…"
        case .installing: return "Installing…"
        case .failed(let message): return message
        }
    }
}

// MARK: - Appearance

private struct AppearanceSettings: View {
    @ObservedObject var coordinator: AppCoordinator
    @ObservedObject var settings: AppSettings

    /// Knob position along the slider, 0–100 % (50 % = default).
    private func percent(_ value: Double, _ range: ClosedRange<Double>) -> Int {
        Int(((value - range.lowerBound) / (range.upperBound - range.lowerBound) * 100).rounded())
    }

    /// Mirrors a "further down the screen = bigger number" value so that,
    /// like every other slider, left = less (lower) and right = more (higher).
    private func upward(_ value: Binding<Double>, _ range: ClosedRange<Double>) -> Binding<Double> {
        Binding(get: { range.lowerBound + range.upperBound - value.wrappedValue },
                set: { value.wrappedValue = range.lowerBound + range.upperBound - $0 })
    }

    private func heightSlider(_ value: Binding<Double>, range: ClosedRange<Double>) -> some View {
        let knob = upward(value, range)
        return VStack(alignment: .leading, spacing: 4) {
            Text("Player height (artwork + controls): \(percent(knob.wrappedValue, range)) %")
            Slider(value: knob, in: range) {
                EmptyView()
            } minimumValueLabel: {
                Text("Lower").font(.caption)
            } maximumValueLabel: {
                Text("Higher").font(.caption)
            }
        }
    }

    private func modeHeader(_ title: String, inUse: Bool) -> some View {
        HStack(spacing: 8) {
            Text(title)
            if inUse {
                Text("IN USE")
                    .font(.system(size: 9, weight: .bold))
                    .padding(.horizontal, 6)
                    .padding(.vertical, 2)
                    .background(Capsule().fill(Color.accentColor.opacity(0.25)))
            }
        }
    }

    private func lyricsOffsetSlider(_ value: Binding<Double>, range: ClosedRange<Double>) -> some View {
        let knob = upward(value, range)
        return VStack(alignment: .leading, spacing: 4) {
            Text("Lyrics position: \(percent(knob.wrappedValue, range)) %")
            Slider(value: knob, in: range) {
                EmptyView()
            } minimumValueLabel: {
                Text("Lower").font(.caption)
            } maximumValueLabel: {
                Text("Higher").font(.caption)
            }
            Text("50 % is the default. Too high can touch the clock, too low your name.")
                .font(.caption).foregroundStyle(.secondary)
        }
    }

    private func sizeSlider(_ value: Binding<Double>) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("Artwork size: \(Int((value.wrappedValue * 100).rounded())) %")
            Slider(value: value, in: 0.6...AppSettings.maxArtworkScale, step: 0.05) {
                EmptyView()
            } minimumValueLabel: {
                Image(systemName: "photo").font(.caption)
            } maximumValueLabel: {
                Image(systemName: "photo").font(.title3)
            }
        }
    }

    var body: some View {
        Form {
            Section("Presets") {
                HStack {
                    Button("Export Preset…") { PresetFiles.export(settings) }
                    Button("Import Preset…") { PresetFiles.importFile(into: settings) }
                }
                Text("Save your whole look (layout, sizes, lyrics, glass, clock) to a file you can share, or load someone else's preset.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section("Widget") {
                Toggle("Artwork wallpaper behind the widget", isOn: $settings.lockWallpaper)
                Picker("Wallpaper style", selection: $settings.backgroundStyle) {
                    ForEach(BackgroundStyle.allCases) { Text($0.title).tag($0) }
                }
                .disabled(!settings.lockWallpaper)
                Text("On: the screen is filled with the artwork, like in the reference video. Off: only the album art, controls and lyrics float over your normal lock screen. Each mode keeps its own height and artwork size below.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section {
                heightSlider(Binding(get: { 1 - settings.widgetBottomInset },
                                     set: { settings.widgetBottomInset = 1 - $0 }),
                             // Centred on the old lowest point (72 %), so it can go lower still.
                             range: AppSettings.playerBottomRange)
                sizeSlider($settings.artworkScale)
                lyricsOffsetSlider($settings.lyricsOffset, range: AppSettings.wallpaperLyricsRange)
            } header: {
                modeHeader("With artwork wallpaper", inUse: settings.lockWallpaper)
            }

            Section {
                heightSlider(Binding(get: { 1 - settings.plainBottomInset },
                                     set: { settings.plainBottomInset = 1 - $0 }),
                             range: AppSettings.playerBottomRange)
                sizeSlider($settings.plainArtworkScale)
                Label("A big change here can make the album cover reach up into the clock.",
                      systemImage: "exclamationmark.triangle")
                    .font(.caption).foregroundStyle(.orange)
                lyricsOffsetSlider($settings.plainLyricsOffset, range: AppSettings.plainLyricsRange)
                Text("Every slider: left = less / lower, right = more / higher. Below 50 % the player can reach your name and “Touch ID or Enter Password”.")
                    .font(.caption).foregroundStyle(.secondary)
            } header: {
                modeHeader("Without wallpaper (normal lock screen)", inUse: !settings.lockWallpaper)
            }

            Section("Animation") {
                Picker("Smoothness", selection: $settings.animationFPS) {
                    ForEach(AppSettings.fpsOptions, id: \.self) { Text("\($0) fps").tag($0) }
                }
                .pickerStyle(.segmented)
                Text("Frame rate of the moving background, lyrics and progress bar. 120 fps needs a ProMotion display; higher uses a bit more battery.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section {
                Button("Reset Layout to Defaults") {
                    settings.widgetBottomInset = 0.253
                    settings.artworkScale = 2.5
                    settings.plainBottomInset = 0.28
                    settings.plainArtworkScale = 2.0
                    settings.lyricsScale = 0.85
                    settings.lyricsOffset = 0.184
                    settings.plainLyricsOffset = 0.10
                }
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
                VStack(alignment: .leading, spacing: 4) {
                    Text("Lyrics text size: \(Int((settings.lyricsScale * 100).rounded())) %")
                    Slider(value: $settings.lyricsScale, in: 0.6...1.4, step: 0.05) {
                        EmptyView()
                    } minimumValueLabel: {
                        Image(systemName: "textformat.size.smaller").font(.caption)
                    } maximumValueLabel: {
                        Image(systemName: "textformat.size.larger").font(.caption)
                    }
                    Text("Smaller text shows more lines at once.")
                        .font(.caption).foregroundStyle(.secondary)
                }
                Picker("Lyrics position", selection: $settings.lyricsPosition) {
                    ForEach(LyricsPosition.allCases) { Text($0.title).tag($0) }
                }
                Picker("Highlight", selection: $settings.wordByWord) {
                    Text("Whole line (like before)").tag(false)
                    Text("Word by word (like Apple Music)").tag(true)
                }
                VStack(alignment: .leading, spacing: 4) {
                    Text("Lyrics timing: \(String(format: "%.1f", settings.lyricsLead)) s ahead")
                    Slider(value: $settings.lyricsLead, in: 0...1.5, step: 0.1) {
                        EmptyView()
                    } minimumValueLabel: {
                        Text("On time").font(.caption)
                    } maximumValueLabel: {
                        Text("Earlier").font(.caption)
                    }
                    Text("If lyrics feel late, move this towards Earlier. Word timing is estimated when the lyrics only have line times.")
                        .font(.caption).foregroundStyle(.secondary)
                }
                Button("Clear Lyrics Cache") { coordinator.lyrics.clearCache() }
            }
        }
        .formStyle(.grouped)
    }
}


/// Save/open panels for presets.
@MainActor
enum PresetFiles {
    static func export(_ settings: AppSettings) {
        let panel = NSSavePanel()
        panel.title = "Export LockTune Preset"
        panel.allowedContentTypes = [.json]
        panel.nameFieldStringValue = "LockTune Preset.json"
        NSApp.activate()
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            let name = url.deletingPathExtension().lastPathComponent
            try settings.exportPresetData(name: name).write(to: url, options: .atomic)
        } catch {
            show("Couldn't export the preset", error.localizedDescription, style: .warning)
        }
    }

    static func importFile(into settings: AppSettings) {
        let panel = NSOpenPanel()
        panel.title = "Import LockTune Preset"
        panel.allowedContentTypes = [.json]
        panel.allowsMultipleSelection = false
        NSApp.activate()
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            let name = try settings.importPreset(Data(contentsOf: url))
            show("Preset imported", "“\(name ?? url.deletingPathExtension().lastPathComponent)” is now active.", style: .informational)
        } catch {
            show("Couldn't import the preset", "This file isn't a LockTune preset.", style: .warning)
        }
    }

    private static func show(_ title: String, _ text: String, style: NSAlert.Style) {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = text
        alert.alertStyle = style
        alert.runModal()
    }
}
