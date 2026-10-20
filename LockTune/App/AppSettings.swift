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
        static let wordByWord = "wordByWord"
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
    /// Apple Music-style word-by-word highlight of the current lyric line.
    @Published var wordByWord: Bool { didSet { defaults.set(wordByWord, forKey: Key.wordByWord) } }
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
            Key.lockWallpaper: false,
            Key.lyricsEnabled: true,
            Key.widgetBottomInset: 0.25,
            Key.keepDisplayAwake: true,
            Key.wordByWord: true,
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
        widgetBottomInset = min(max(defaults.double(forKey: Key.widgetBottomInset), 0.05), 0.6)
        keepDisplayAwake = defaults.bool(forKey: Key.keepDisplayAwake)
        wordByWord = defaults.bool(forKey: Key.wordByWord)
        lyricsEnabled = defaults.bool(forKey: Key.lyricsEnabled)
        preferredSource = defaults.string(forKey: Key.preferredSource).flatMap(PreferredSource.init(rawValue:)) ?? .automatic
        allDisplays = defaults.bool(forKey: Key.allDisplays)
    }
}
