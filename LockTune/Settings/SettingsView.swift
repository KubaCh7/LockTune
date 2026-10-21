import AppKit
import SwiftUI
import UniformTypeIdentifiers

struct SettingsView: View {
    @ObservedObject var coordinator: AppCoordinator
    @ObservedObject var settings: AppSettings
    @ObservedObject var player: NowPlayingController

    enum Tab: String, CaseIterable, Identifiable {
        case status = "Status", general = "General", look = "Look", layout = "Layout", lyrics = "Lyrics"
        var id: String { rawValue }
        var icon: String {
            switch self {
            case .status: return "checkmark.circle"
            case .general: return "gearshape"
            case .look: return "paintpalette"
            case .layout: return "rectangle.3.offgrid"
            case .lyrics: return "quote.bubble"
            }
        }
    }

    @State private var tab: Tab = .status
    @Namespace private var tabNamespace
    @StateObject private var swipe = TabSwipeMonitor()

    private func select(_ newTab: Tab) {
        withAnimation(.spring(response: 0.35, dampingFraction: 0.8)) { tab = newTab }
    }

    /// Next/previous tab for a trackpad swipe (wraps around).
    private func step(_ delta: Int) {
        let all = Tab.allCases
        let index = all.firstIndex(of: tab) ?? 0
        let next = min(max(index + delta, 0), all.count - 1)
        if next != index { select(all[next]) }
    }

    var body: some View {
        ZStack {
            // See-through, blurred window + a glow tinted by the current artwork.
            WindowBlur()
            LinearGradient(colors: [player.palette.primary.color.opacity(0.45),
                                    player.palette.secondary.color.opacity(0.25),
                                    player.palette.tertiary.color.opacity(0.35)],
                           startPoint: .topLeading, endPoint: .bottomTrailing)
                .animation(.easeInOut(duration: 1), value: player.palette)

            VStack(spacing: 0) {
                tabBar
                    .padding(.top, 34) // room for the traffic lights
                    .padding(.bottom, 6)
                Group {
                    switch tab {
                    case .status: StatusTab(coordinator: coordinator, settings: settings, player: player)
                    case .general: GeneralSettings(coordinator: coordinator, settings: settings)
                    case .look: LookSettings(settings: settings)
                    case .layout: LayoutSettings(settings: settings)
                    case .lyrics: LyricsSettings(coordinator: coordinator, settings: settings)
                    }
                }
                .hiddenScrollBackground()
                .transition(.opacity)
            }
        }
        .environment(\.glassLevel, .normal)
        .frame(width: 580, height: 640)
        .preferredColorScheme(.dark)
        .onAppear {
            swipe.onSwipe = { step($0) }
            swipe.start()
        }
        .onDisappear { swipe.stop() }
    }

    /// Glass pill tab bar.
    private var tabBar: some View {
        HStack(spacing: 4) {
            ForEach(Array(Tab.allCases.enumerated()), id: \.element) { index, item in
                Button {
                    select(item)
                } label: {
                    Label(item.rawValue, systemImage: item.icon)
                        .font(.system(size: 12, weight: .semibold))
                        .padding(.horizontal, 10)
                        .padding(.vertical, 7)
                        .background {
                            if tab == item {
                                Capsule()
                                    .fill(.white.opacity(0.18))
                                    .overlay(Capsule().strokeBorder(.white.opacity(0.35), lineWidth: 0.8))
                                    .matchedGeometryEffect(id: "tab", in: tabNamespace)
                            }
                        }
                        .contentShape(Capsule())
                }
                .buttonStyle(PressableButtonStyle())
                // ⌘1 … ⌘5
                .keyboardShortcut(KeyEquivalent(Character(String(index + 1))), modifiers: .command)
                .help("\(item.rawValue) (⌘\(index + 1))")
            }
        }
        .padding(4)
        .glassBackground(Capsule())
    }
}

/// Behind-window blur for a transparent settings window.
private struct WindowBlur: NSViewRepresentable {
    func makeNSView(context: Context) -> NSVisualEffectView {
        let view = NSVisualEffectView()
        view.material = .hudWindow
        view.blendingMode = .behindWindow
        view.state = .active
        return view
    }

    func updateNSView(_ nsView: NSVisualEffectView, context: Context) {}
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
                    LabeledRow(name) {
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
        .groupedFormStyle()
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

/// How the current lyric line lights up; shown in General and Appearance.
private struct LyricsHighlightPicker: View {
    @ObservedObject var settings: AppSettings

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Picker("Lyrics highlight", selection: $settings.wordByWord) {
                Text("Word by word").tag(true)
                Text("Whole line").tag(false)
            }
            .pickerStyle(.segmented)
            Text(settings.wordByWord
                 ? "Words light up one after another, like Apple Music (whole line on macOS 12–13)."
                 : "The whole current line lights up at once.")
                .font(.caption).foregroundStyle(.secondary)
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
        case .mega: return "⚠️ Experimental — doesn't work very well yet and can look off, especially without the artwork wallpaper. Glass everywhere: player card, every button, a frame around the artwork and a panel behind the lyrics."
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
                    .disabled(!LaunchAtLogin.isSupported)
                if !LaunchAtLogin.isSupported {
                    Text("On macOS 12 add LockTune under System Preferences → Users & Groups → Login Items.")
                        .font(.caption).foregroundStyle(.secondary)
                }
                if let launchError {
                    Text(launchError).font(.caption).foregroundStyle(.red)
                }
            }

            Section("Look") {
                GlassPicker(settings: settings)
            }

            Section("Lyrics") {
                LyricsHighlightPicker(settings: settings)
            }

            Section("Updates") {
                LabeledRow("Installed version") { Text(updater.versionString).foregroundStyle(.secondary) }
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
                LabeledRow("Lock screen now") {
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
        .groupedFormStyle()
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

// MARK: - Look

private struct LookSettings: View {
    @ObservedObject var settings: AppSettings

    var body: some View {
        Form {
            Section("Background") {
                Toggle("Artwork wallpaper behind the widget", isOn: $settings.lockWallpaper)
                Picker("Wallpaper style", selection: $settings.backgroundStyle) {
                    ForEach(BackgroundStyle.allCases) { Text($0.title).tag($0) }
                }
                .disabled(!settings.lockWallpaper)
                Text("On: the screen is filled with the artwork. Off: only the album art, controls and lyrics float over your normal lock screen.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section("Liquid Glass") {
                GlassPicker(settings: settings)
            }

            Section("Clock (wallpaper mode & preview)") {
                FontPickers(font: $settings.clockFont, weight: $settings.clockWeight)
                HStack {
                    Spacer()
                    ClockView(size: 56, style: settings.clockFont, weight: settings.clockWeight)
                    Spacer()
                }
                .padding(.vertical, 4)
            }

            Section("Animation") {
                Picker("Smoothness", selection: $settings.animationFPS) {
                    ForEach(AppSettings.fpsOptions, id: \.self) { Text("\($0) fps").tag($0) }
                }
                .pickerStyle(.segmented)
                Text("Frame rate of the moving background, lyrics and progress bar. 120 fps needs a ProMotion display.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section("Presets") {
                HStack {
                    Button("Export Preset…") { PresetFiles.export(settings) }
                    Button("Import Preset…") { PresetFiles.importFile(into: settings) }
                }
                Text("Save your whole look (layout, sizes, fonts, lyrics, glass, clock) to a file you can share, or load someone else's.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .groupedFormStyle()
    }
}

// MARK: - Layout

private struct LayoutSettings: View {
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
            Section {
                Text("Each mode has its own sliders. Left = less / lower, right = more / higher; 50 % is the default.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section {
                heightSlider(Binding(get: { 1 - settings.widgetBottomInset },
                                     set: { settings.widgetBottomInset = 1 - $0 }),
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
                Text("Below 50 % the player can reach your name and “Touch ID or Enter Password”.")
                    .font(.caption).foregroundStyle(.secondary)
            } header: {
                modeHeader("Without wallpaper (normal lock screen)", inUse: !settings.lockWallpaper)
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
        }
        .groupedFormStyle()
    }
}

// MARK: - Lyrics

private struct LyricsSettings: View {
    @ObservedObject var coordinator: AppCoordinator
    @ObservedObject var settings: AppSettings

    var body: some View {
        Form {
            Section {
                Toggle("Load lyrics from LRCLIB", isOn: $settings.lyricsEnabled)
                Picker("Where", selection: $settings.lyricsPosition) {
                    ForEach(LyricsPosition.allCases) { Text($0.title).tag($0) }
                }
                LyricsHighlightPicker(settings: settings)
            }

            Section("Font") {
                FontPickers(font: $settings.lyricsFont, weight: $settings.lyricsWeight)
                VStack(alignment: .leading, spacing: 6) {
                    Text("Here is the place")
                        .font(settings.lyricsFont.font(size: 26, weight: settings.lyricsWeight.fontWeight))
                    Text("Come together in the dream")
                        .font(settings.lyricsFont.font(size: 20, weight: settings.lyricsWeight.fontWeight))
                        .opacity(0.45)
                }
                .padding(.vertical, 4)
                VStack(alignment: .leading, spacing: 4) {
                    Text("Text size: \(Int((settings.lyricsScale * 100).rounded())) %")
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
            }

            Section("Timing") {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Lyrics timing: \(String(format: "%.1f", settings.lyricsLead)) s ahead")
                    Slider(value: $settings.lyricsLead, in: 0...1.5, step: 0.1) {
                        EmptyView()
                    } minimumValueLabel: {
                        Text("On time").font(.caption)
                    } maximumValueLabel: {
                        Text("Earlier").font(.caption)
                    }
                    Text("If lyrics feel late, move this towards Earlier.")
                        .font(.caption).foregroundStyle(.secondary)
                }
                Button("Clear Lyrics Cache") { coordinator.lyrics.clearCache() }
            }
        }
        .groupedFormStyle()
    }
}

/// Font + weight pickers shared by the clock and the lyrics.
private struct FontPickers: View {
    @Binding var font: FontChoice
    @Binding var weight: ClockWeight

    var body: some View {
        Picker("Font", selection: $font) {
            ForEach(FontChoice.allCases) { Text($0.title).tag($0) }
        }
        Picker("Weight", selection: $weight) {
            ForEach(ClockWeight.allCases) { Text($0.title).tag($0) }
        }
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
        NSApp.activateCompat()
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
        NSApp.activateCompat()
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


/// Two-finger horizontal trackpad swipe in the settings window → change tab.
final class TabSwipeMonitor: ObservableObject {
    /// -1 = previous tab, +1 = next tab.
    var onSwipe: ((Int) -> Void)?
    private var monitor: Any?
    private var accumulated: CGFloat = 0
    private var fired = false

    func start() {
        guard monitor == nil else { return }
        monitor = NSEvent.addLocalMonitorForEvents(matching: .scrollWheel) { [weak self] event in
            self?.handle(event)
            return event
        }
    }

    func stop() {
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
    }

    private func handle(_ event: NSEvent) {
        guard event.window?.identifier?.rawValue == "LockTuneSettings",
              event.hasPreciseScrollingDeltas, event.momentumPhase.isEmpty else { return }
        if event.phase.contains(.began) {
            accumulated = 0
            fired = false
        }
        // Only clearly horizontal movement, so scrolling a list doesn't flip tabs.
        if abs(event.scrollingDeltaX) > abs(event.scrollingDeltaY) * 1.5 {
            accumulated += event.scrollingDeltaX
        }
        if !fired, abs(accumulated) > 70 {
            fired = true
            // Fingers moving left (content moves left) = next tab, like pages.
            onSwipe?(accumulated < 0 ? 1 : -1)
        }
        if event.phase.contains(.ended) || event.phase.contains(.cancelled) {
            accumulated = 0
            fired = false
        }
    }

    deinit { stop() }
}
