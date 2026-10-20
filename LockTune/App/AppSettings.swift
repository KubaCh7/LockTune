import SwiftUI

enum ClockFontStyle: String, CaseIterable, Identifiable {
    case newYork, rounded, sfPro
    var id: String { rawValue }

    var title: String {
        switch self {
        case .newYork: return "New York (serif)"
        case .rounded: return "SF Pro Rounded"
        case .sfPro: return "SF Pro"
        }
    }

    var design: Font.Design {
        switch self {
        case .newYork: return .serif
        case .rounded: return .rounded
        case .sfPro: return .default
        }
    }
}

enum ClockWeight: String, CaseIterable, Identifiable {
    case ultraLight, thin, light, regular, medium, semibold, bold
    var id: String { rawValue }
    var title: String { rawValue.prefix(1).uppercased() + rawValue.dropFirst() }

    var fontWeight: Font.Weight {
        switch self {
        case .ultraLight: return .ultraLight
        case .thin: return .thin
        case .light: return .light
        case .regular: return .regular
        case .medium: return .medium
        case .semibold: return .semibold
        case .bold: return .bold
        }
    }
}

enum BackgroundStyle: String, CaseIterable, Identifiable {
    /// Heavily blurred, slowly drifting artwork (Apple Music style).
    case blurred
    /// The artwork itself, scaled to fill the screen like a wallpaper (as in the reference video).
    case artwork
    var id: String { rawValue }
    var title: String { self == .blurred ? "Blurred & animated" : "Artwork wallpaper" }
}

enum LyricsPosition: String, CaseIterable, Identifiable {
    /// Artwork + controls on the left, lyrics on the right (reference video).
    case right
    /// Lyrics on the left, artwork + controls on the right.
    case left
    /// Large centred lyrics, small artwork + controls underneath.
    case center
    var id: String { rawValue }
    var title: String {
        switch self {
        case .right: return "Right of the artwork"
        case .left: return "Left of the artwork"
        case .center: return "Centered (big)"
        }
    }
}

enum PreferredSource: String, CaseIterable, Identifiable {
    case automatic, music, spotify
    var id: String { rawValue }
    var title: String {
        switch self {
        case .automatic: return "Automatic"
        case .music: return "Apple Music only"
        case .spotify: return "Spotify only"
        }
    }

    func allows(_ providerID: String) -> Bool {
        self == .automatic || rawValue == providerID
    }
}

/// Frame interval used by the timeline-driven animations (drifting
/// background, lyrics, progress bar). Set once at the widget's root.
private struct FrameIntervalKey: EnvironmentKey {
    static let defaultValue: Double = 1.0 / 60
}

extension EnvironmentValues {
    var frameInterval: Double {
        get { self[FrameIntervalKey.self] }
        set { self[FrameIntervalKey.self] = newValue }
    }
}

/// User preferences, persisted in UserDefaults.
final class AppSettings: ObservableObject {
    private enum Key {
        static let hotkeyEnabled = "hotkeyEnabled"
        static let hotkey = "hotkey"
        static let idleEnabled = "idleEnabled"
        static let idleMinutes = "idleMinutes"
        static let lockWidgetEnabled = "lockWidgetEnabled"
        static let lockWallpaper = "lockWallpaper"
        static let clockFont = "clockFont"
        static let clockWeight = "clockWeight"
        static let backgroundStyle = "backgroundStyle"
        static let lyricsPosition = "lyricsPosition"
        static let widgetBottomInset = "widgetBottomInset"
        static let keepDisplayAwake = "keepDisplayAwake"
        static let awakeDurationIndex = "awakeDurationIndex"
        static let wordByWord = "wordByWord"
        static let artworkScale = "artworkScale"
        static let plainBottomInset = "plainBottomInset"
        static let plainArtworkScale = "plainArtworkScale"
        static let animationFPS = "animationFPS"
        static let lyricsScale = "lyricsScale"
        static let glassLevel = "glassLevel"
        static let lyricsOffset = "lyricsOffset"
        static let plainLyricsOffset = "plainLyricsOffset"
        static let layoutVersion = "layoutVersion"
        static let lyricsLead = "lyricsLead"
        static let lyricsEnabled = "lyricsEnabled"
        static let preferredSource = "preferredSource"
        static let allDisplays = "allDisplays"
    }

    private let defaults: UserDefaults

    @Published var hotkeyEnabled: Bool { didSet { defaults.set(hotkeyEnabled, forKey: Key.hotkeyEnabled) } }
    @Published var hotkey: Hotkey {
        didSet { defaults.set(try? JSONEncoder().encode(hotkey), forKey: Key.hotkey) }
    }
    @Published var idleEnabled: Bool { didSet { defaults.set(idleEnabled, forKey: Key.idleEnabled) } }
    @Published var idleMinutes: Int { didSet { defaults.set(idleMinutes, forKey: Key.idleMinutes) } }
    /// Show the widget when the Mac is locked.
    @Published var lockWidgetEnabled: Bool { didSet { defaults.set(lockWidgetEnabled, forKey: Key.lockWidgetEnabled) } }
    /// Full-screen artwork + clock behind the widget (like the reference video).
    @Published var lockWallpaper: Bool { didSet { defaults.set(lockWallpaper, forKey: Key.lockWallpaper) } }
    @Published var clockFont: ClockFontStyle { didSet { defaults.set(clockFont.rawValue, forKey: Key.clockFont) } }
    @Published var clockWeight: ClockWeight { didSet { defaults.set(clockWeight.rawValue, forKey: Key.clockWeight) } }
    @Published var backgroundStyle: BackgroundStyle { didSet { defaults.set(backgroundStyle.rawValue, forKey: Key.backgroundStyle) } }
    @Published var lyricsPosition: LyricsPosition { didSet { defaults.set(lyricsPosition.rawValue, forKey: Key.lyricsPosition) } }
    /// Gap between the bottom of the widget and the bottom of the screen, as a
    /// fraction of the screen height (default sits just above the user name).
    @Published var widgetBottomInset: Double { didSet { defaults.set(widgetBottomInset, forKey: Key.widgetBottomInset) } }
    /// Keep the display on while the widget is on the lock screen and music plays.
    @Published var keepDisplayAwake: Bool { didSet { defaults.set(keepDisplayAwake, forKey: Key.keepDisplayAwake) } }
    /// How long the display stays on after locking (index into `awakeDurations`).
    @Published var awakeDurationIndex: Int { didSet { defaults.set(awakeDurationIndex, forKey: Key.awakeDurationIndex) } }

    /// Seconds; 0 = macOS default (turns off after a few seconds), .infinity = while music plays.
    static let awakeDurations: [TimeInterval] = [0, 30, 60, 120, 300, 600, 900, 1800, 3600, .infinity]

    var awakeDuration: TimeInterval { Self.awakeDurations[min(max(awakeDurationIndex, 0), Self.awakeDurations.count - 1)] }

    static func awakeTitle(_ seconds: TimeInterval) -> String {
        if seconds == 0 { return "macOS default" }
        if seconds.isInfinite { return "Whole time music plays" }
        return seconds < 60 ? "\(Int(seconds)) s" : "\(Int(seconds / 60)) min"
    }
    /// Apple Music-style word-by-word highlight of the current lyric line.
    @Published var wordByWord: Bool { didSet { defaults.set(wordByWord, forKey: Key.wordByWord) } }
    /// Artwork size multiplier with the artwork wallpaper on (0.6 … 1.6).
    @Published var artworkScale: Double { didSet { defaults.set(artworkScale, forKey: Key.artworkScale) } }
    /// Same two settings for the plain (transparent) mode over the normal lock screen.
    @Published var plainBottomInset: Double { didSet { defaults.set(plainBottomInset, forKey: Key.plainBottomInset) } }
    @Published var plainArtworkScale: Double { didSet { defaults.set(plainArtworkScale, forKey: Key.plainArtworkScale) } }

    /// 30, 60 or 120 frames per second for the continuous animations.
    @Published var animationFPS: Int { didSet { defaults.set(animationFPS, forKey: Key.animationFPS) } }
    static let fpsOptions = [30, 60, 120]
    var frameInterval: Double { 1.0 / Double(max(animationFPS, 1)) }

    /// How much Liquid Glass the widget uses.
    @Published var glassLevel: GlassLevel { didSet { defaults.set(glassLevel.rawValue, forKey: Key.glassLevel) } }

    /// Moves the lyrics block down (fraction of screen height, 0 … 0.4), per mode.
    @Published var lyricsOffset: Double { didSet { defaults.set(lyricsOffset, forKey: Key.lyricsOffset) } }
    @Published var plainLyricsOffset: Double { didSet { defaults.set(plainLyricsOffset, forKey: Key.plainLyricsOffset) } }
    /// Slider ranges, centred on the defaults (the owner's chosen look), so a
    /// fresh install has every knob in the middle with room both ways.
    static let wallpaperLyricsRange: ClosedRange<Double> = -0.18...0.55
    static let plainLyricsRange: ClosedRange<Double> = -0.25...0.45
    /// Bottom edge of artwork + controls, as a fraction of the screen height.
    static let playerBottomRange: ClosedRange<Double> = 0.48...0.96
    var activeLyricsOffset: Double { lockWallpaper ? lyricsOffset : plainLyricsOffset }

    /// Lyrics text size multiplier (0.6 … 1.4).
    @Published var lyricsScale: Double { didSet { defaults.set(lyricsScale, forKey: Key.lyricsScale) } }

    /// Over the normal lock screen the widget stays above the user name and
    /// "Touch ID or Enter Password" (around 75–85 % down). With the artwork
    /// wallpaper the artwork may go lower; lyrics are kept above ~70 % in both
    /// modes by the layout itself.
    static let minPlainBottomInset = 1 - playerBottomRange.upperBound
    static let minWallpaperBottomInset = 1 - playerBottomRange.upperBound
    static let maxArtworkScale = 2.5

    /// Layout values for the current mode.
    var activeBottomInset: Double {
        lockWallpaper ? max(widgetBottomInset, Self.minWallpaperBottomInset)
                      : max(plainBottomInset, Self.minPlainBottomInset)
    }
    var activeArtworkScale: Double { lockWallpaper ? artworkScale : plainArtworkScale }
    /// How many seconds early lyrics are highlighted (0 … 1.5).
    @Published var lyricsLead: Double { didSet { defaults.set(lyricsLead, forKey: Key.lyricsLead) } }
    @Published var lyricsEnabled: Bool { didSet { defaults.set(lyricsEnabled, forKey: Key.lyricsEnabled) } }
    @Published var preferredSource: PreferredSource { didSet { defaults.set(preferredSource.rawValue, forKey: Key.preferredSource) } }
    @Published var allDisplays: Bool { didSet { defaults.set(allDisplays, forKey: Key.allDisplays) } }

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        defaults.register(defaults: [
            Key.hotkeyEnabled: true,
            Key.idleEnabled: false,
            Key.idleMinutes: 5,
            Key.lockWidgetEnabled: true,
            Key.lockWallpaper: true,
            Key.lyricsEnabled: true,
            // Middle of the wallpaper-mode slider (what used to be its lowest point).
            Key.widgetBottomInset: 0.253,
            Key.plainBottomInset: 0.28,
            Key.plainArtworkScale: 2.0,
            Key.lyricsOffset: 0.184,
            Key.plainLyricsOffset: 0.10,
            Key.animationFPS: 60,
            Key.lyricsScale: 0.85,
            Key.keepDisplayAwake: true,
            Key.awakeDurationIndex: 9,
            Key.wordByWord: true,
            Key.artworkScale: 2.5,
            Key.lyricsLead: 0.6,
            Key.allDisplays: true,
        ])

        hotkeyEnabled = defaults.bool(forKey: Key.hotkeyEnabled)
        hotkey = defaults.data(forKey: Key.hotkey)
            .flatMap { try? JSONDecoder().decode(Hotkey.self, from: $0) } ?? .default
        idleEnabled = defaults.bool(forKey: Key.idleEnabled)
        idleMinutes = max(1, defaults.integer(forKey: Key.idleMinutes))
        lockWidgetEnabled = defaults.bool(forKey: Key.lockWidgetEnabled)
        lockWallpaper = defaults.bool(forKey: Key.lockWallpaper)
        clockFont = defaults.string(forKey: Key.clockFont).flatMap(ClockFontStyle.init(rawValue:)) ?? .newYork
        clockWeight = defaults.string(forKey: Key.clockWeight).flatMap(ClockWeight.init(rawValue:)) ?? .regular
        backgroundStyle = defaults.string(forKey: Key.backgroundStyle).flatMap(BackgroundStyle.init(rawValue:)) ?? .blurred
        lyricsPosition = defaults.string(forKey: Key.lyricsPosition).flatMap(LyricsPosition.init(rawValue:)) ?? .right
        // Layout v2 moved everything higher: drop positions saved by older builds.
        let layoutVersion = defaults.integer(forKey: Key.layoutVersion)
        if layoutVersion < 2 { defaults.removeObject(forKey: Key.plainBottomInset) }
        // Layout v4: wallpaper mode may go low again (lyrics have their own
        // limit); start from the new middle position.
        if layoutVersion < 4 { defaults.removeObject(forKey: Key.widgetBottomInset) }
        // Layout v5: new defaults with every slider centred.
        if layoutVersion < 5 {
            for key in [Key.widgetBottomInset, Key.plainBottomInset, Key.artworkScale, Key.plainArtworkScale,
                        Key.lyricsOffset, Key.plainLyricsOffset] {
                defaults.removeObject(forKey: key)
            }
        }
        defaults.set(5, forKey: Key.layoutVersion)
        widgetBottomInset = min(max(defaults.double(forKey: Key.widgetBottomInset), Self.minWallpaperBottomInset), 0.6)
        keepDisplayAwake = defaults.bool(forKey: Key.keepDisplayAwake)
        awakeDurationIndex = min(max(defaults.integer(forKey: Key.awakeDurationIndex), 0), Self.awakeDurations.count - 1)
        wordByWord = defaults.bool(forKey: Key.wordByWord)
        artworkScale = min(max(defaults.double(forKey: Key.artworkScale), 0.6), Self.maxArtworkScale)
        plainBottomInset = min(max(defaults.double(forKey: Key.plainBottomInset), Self.minPlainBottomInset), 0.6)
        plainArtworkScale = min(max(defaults.double(forKey: Key.plainArtworkScale), 0.6), Self.maxArtworkScale)
        lyricsOffset = defaults.double(forKey: Key.lyricsOffset).clamped(to: Self.wallpaperLyricsRange)
        plainLyricsOffset = defaults.double(forKey: Key.plainLyricsOffset).clamped(to: Self.plainLyricsRange)
        glassLevel = defaults.string(forKey: Key.glassLevel).flatMap(GlassLevel.init(rawValue:)) ?? .normal
        lyricsScale = min(max(defaults.double(forKey: Key.lyricsScale), 0.6), 1.4)
        let fps = defaults.integer(forKey: Key.animationFPS)
        animationFPS = Self.fpsOptions.contains(fps) ? fps : 60
        lyricsLead = min(max(defaults.double(forKey: Key.lyricsLead), 0), 1.5)
        lyricsEnabled = defaults.bool(forKey: Key.lyricsEnabled)
        preferredSource = defaults.string(forKey: Key.preferredSource).flatMap(PreferredSource.init(rawValue:)) ?? .automatic
        allDisplays = defaults.bool(forKey: Key.allDisplays)
    }
}


// MARK: - Presets

/// A shareable look: everything about how the widget appears (not the
/// shortcut, launch-at-login or the music source). Saved as JSON.
struct LockTunePreset: Codable {
    var format = "LockTune preset"
    var version = 1
    var name: String?

    var lockWallpaper: Bool?
    var backgroundStyle: String?
    var widgetBottomInset: Double?
    var artworkScale: Double?
    var lyricsOffset: Double?
    var plainBottomInset: Double?
    var plainArtworkScale: Double?
    var plainLyricsOffset: Double?
    var lyricsEnabled: Bool?
    var lyricsPosition: String?
    var lyricsScale: Double?
    var lyricsLead: Double?
    var wordByWord: Bool?
    var clockFont: String?
    var clockWeight: String?
    var glassLevel: String?
    var animationFPS: Int?
    var awakeDurationIndex: Int?
}

extension AppSettings {
    func makePreset(name: String?) -> LockTunePreset {
        var p = LockTunePreset()
        p.name = name
        p.lockWallpaper = lockWallpaper
        p.backgroundStyle = backgroundStyle.rawValue
        p.widgetBottomInset = widgetBottomInset
        p.artworkScale = artworkScale
        p.lyricsOffset = lyricsOffset
        p.plainBottomInset = plainBottomInset
        p.plainArtworkScale = plainArtworkScale
        p.plainLyricsOffset = plainLyricsOffset
        p.lyricsEnabled = lyricsEnabled
        p.lyricsPosition = lyricsPosition.rawValue
        p.lyricsScale = lyricsScale
        p.lyricsLead = lyricsLead
        p.wordByWord = wordByWord
        p.clockFont = clockFont.rawValue
        p.clockWeight = clockWeight.rawValue
        p.glassLevel = glassLevel.rawValue
        p.animationFPS = animationFPS
        p.awakeDurationIndex = awakeDurationIndex
        return p
    }

    func exportPresetData(name: String?) throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        return try encoder.encode(makePreset(name: name))
    }

    /// Applies every value present in the preset (missing ones are kept),
    /// clamped to the valid ranges. Returns the preset's name, if any.
    @discardableResult
    func importPreset(_ data: Data) throws -> String? {
        let p = try JSONDecoder().decode(LockTunePreset.self, from: data)
        guard p.format == "LockTune preset" else {
            throw CocoaError(.fileReadCorruptFile)
        }
        func clamp(_ v: Double, _ lo: Double, _ hi: Double) -> Double { min(max(v, lo), hi) }

        if let v = p.lockWallpaper { lockWallpaper = v }
        if let v = p.backgroundStyle.flatMap(BackgroundStyle.init(rawValue:)) { backgroundStyle = v }
        if let v = p.widgetBottomInset { widgetBottomInset = clamp(v, Self.minWallpaperBottomInset, 0.6) }
        if let v = p.artworkScale { artworkScale = clamp(v, 0.6, Self.maxArtworkScale) }
        if let v = p.lyricsOffset { lyricsOffset = v.clamped(to: Self.wallpaperLyricsRange) }
        if let v = p.plainBottomInset { plainBottomInset = clamp(v, Self.minPlainBottomInset, 0.6) }
        if let v = p.plainArtworkScale { plainArtworkScale = clamp(v, 0.6, Self.maxArtworkScale) }
        if let v = p.plainLyricsOffset { plainLyricsOffset = v.clamped(to: Self.plainLyricsRange) }
        if let v = p.lyricsEnabled { lyricsEnabled = v }
        if let v = p.lyricsPosition.flatMap(LyricsPosition.init(rawValue:)) { lyricsPosition = v }
        if let v = p.lyricsScale { lyricsScale = clamp(v, 0.6, 1.4) }
        if let v = p.lyricsLead { lyricsLead = clamp(v, 0, 1.5) }
        if let v = p.wordByWord { wordByWord = v }
        if let v = p.clockFont.flatMap(ClockFontStyle.init(rawValue:)) { clockFont = v }
        if let v = p.clockWeight.flatMap(ClockWeight.init(rawValue:)) { clockWeight = v }
        if let v = p.glassLevel.flatMap(GlassLevel.init(rawValue:)) { glassLevel = v }
        if let v = p.animationFPS, Self.fpsOptions.contains(v) { animationFPS = v }
        if let v = p.awakeDurationIndex { awakeDurationIndex = min(max(v, 0), Self.awakeDurations.count - 1) }
        return p.name
    }
}

extension Comparable {
    func clamped(to range: ClosedRange<Self>) -> Self { min(max(self, range.lowerBound), range.upperBound) }
}
