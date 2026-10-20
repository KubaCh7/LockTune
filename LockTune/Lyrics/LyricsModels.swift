import Foundation

struct LyricLine: Identifiable, Equatable {
    let id: Int
    /// Start time in seconds.
    let time: TimeInterval
    /// Empty text marks an instrumental break (rendered as animated dots).
    let text: String

    var isInstrumental: Bool { text.isEmpty }
}

enum LyricsContent: Equatable {
    case synced([LyricLine])
    case plain([String])
    case instrumental
}

/// What LRCLIB returns (subset).
struct LRCLibRecord: Codable {
    var id: Int?
    var trackName: String?
    var artistName: String?
    var albumName: String?
    var duration: Double?
    var instrumental: Bool?
    var plainLyrics: String?
    var syncedLyrics: String?
}
