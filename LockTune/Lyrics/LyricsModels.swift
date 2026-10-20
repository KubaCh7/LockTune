import Foundation

struct LyricWord: Equatable {
    /// The word including its trailing space, so concatenating words gives the line.
    let text: String
    /// Start time from an enhanced LRC tag (`<mm:ss.xx>`), if the file has one.
    let time: TimeInterval?
}

struct LyricLine: Identifiable, Equatable {
    let id: Int
    /// Start time in seconds.
    let time: TimeInterval
    /// When the next line starts (or a few seconds later for the last line).
    var end: TimeInterval
    /// Empty text marks an instrumental break (rendered as animated dots).
    let text: String
    let words: [LyricWord]

    var isInstrumental: Bool { text.isEmpty }
    /// True when the file had per-word timing (enhanced LRC).
    var hasWordTiming: Bool { words.contains { $0.time != nil } }

    init(id: Int, time: TimeInterval, end: TimeInterval, text: String, words: [LyricWord]? = nil) {
        self.id = id
        self.time = time
        self.end = end
        self.text = text
        self.words = words ?? LyricLine.split(text)
    }

    /// Progress (0…1) of each word at `now`. Uses real word timing when present,
    /// otherwise spreads the line's sung duration over the words by length,
    /// which looks close to Apple Music's word-by-word fill.
    func wordProgress(at now: TimeInterval) -> [Double] {
        guard !words.isEmpty else { return [] }
        var starts: [TimeInterval] = []
        if hasWordTiming {
            var last = time
            for word in words {
                last = max(last, word.time ?? last)
                starts.append(last)
            }
        } else {
            // People sing ~16 characters per second; don't stretch a short line
            // across a long pause before the next one.
            let characters = Double(words.reduce(0) { $0 + max($1.text.count, 2) })
            let sung = max(0.4, min((end - time) * 0.85, characters / 16 + 0.3))
            var cursor = time
            for word in words {
                starts.append(cursor)
                cursor += sung * Double(max(word.text.count, 2)) / characters
            }
        }
        return words.indices.map { i in
            let start = starts[i]
            let stop = i + 1 < starts.count ? starts[i + 1] : min(end, start + 1.2)
            guard stop > start else { return now >= start ? 1 : 0 }
            return min(max((now - start) / (stop - start), 0), 1)
        }
    }

    static func split(_ text: String) -> [LyricWord] {
        var result: [LyricWord] = []
        var current = ""
        for character in text {
            current.append(character)
            if character == " " {
                result.append(LyricWord(text: current, time: nil))
                current = ""
            }
        }
        if !current.isEmpty { result.append(LyricWord(text: current, time: nil)) }
        return result
    }
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
