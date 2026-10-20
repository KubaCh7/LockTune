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

enum LyricsLayout: String, CaseIterable, Identifiable {
    /// Artwork + controls on the left, lyrics on the right (reference video).
    case sideBySide
    /// Small artwork next to title/artist at the top left, lyrics fill the screen.
    case header
    var id: String { rawValue }
    var title: String { self == .sideBySide ? "Side by side" : "Compact header" }
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
        static let lyricsLayout = "lyricsLayout"
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
    @Published var lyricsLayout: LyricsLayout { didSet { defaults.set(lyricsLayout.rawValue, forKey: Key.lyricsLayout) } }
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
        lyricsLayout = defaults.string(forKey: Key.lyricsLayout).flatMap(LyricsLayout.init(rawValue:)) ?? .sideBySide
        lyricsEnabled = defaults.bool(forKey: Key.lyricsEnabled)
        preferredSource = defaults.string(forKey: Key.preferredSource).flatMap(PreferredSource.init(rawValue:)) ?? .automatic
        allDisplays = defaults.bool(forKey: Key.allDisplays)
    }
}
