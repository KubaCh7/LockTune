import Foundation

/// Parses `.lrc` text such as `[01:23.45]Line` (multiple timestamps per line
/// and the `[offset:±ms]` tag are supported).
enum LRCParser {
    private static let timestamp = try! NSRegularExpression(
        pattern: #"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]"#)

    static func parse(_ text: String) -> [LyricLine] {
        var offset: TimeInterval = 0
        var entries: [(time: TimeInterval, text: String)] = []

        for rawLine in text.components(separatedBy: .newlines) {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            guard line.hasPrefix("[") else { continue }

            if line.lowercased().hasPrefix("[offset:") {
                let value = line.dropFirst(8).dropLast().trimmingCharacters(in: .whitespaces)
                if let ms = Double(value) { offset = ms / 1000 }
                continue
            }

            let ns = line as NSString
            var cursor = 0
            var times: [TimeInterval] = []
            for match in timestamp.matches(in: line, range: NSRange(location: 0, length: ns.length)) {
                // Only leading, consecutive timestamps belong to this line.
                guard match.range.location == cursor else { break }
                let minutes = Double(ns.substring(with: match.range(at: 1))) ?? 0
                let seconds = Double(ns.substring(with: match.range(at: 2))
                    .replacingOccurrences(of: ":", with: ".")) ?? 0
                times.append(minutes * 60 + seconds)
                cursor = match.range.location + match.range.length
            }
            guard !times.isEmpty else { continue }

            let lyric = ns.substring(from: cursor).trimmingCharacters(in: .whitespaces)
            for t in times {
                // A positive offset means the lyrics should appear earlier.
                entries.append((time: max(0, t - offset), text: lyric))
            }
        }

        entries.sort { $0.time < $1.time }

        // Keep empty lines only when they mark a real instrumental gap (≥ 4 s).
        var cleaned: [(time: TimeInterval, text: String)] = []
        for (index, entry) in entries.enumerated() {
            if entry.text.isEmpty {
                let next = entries[(index + 1)...].first { !$0.text.isEmpty }?.time
                guard let next, next - entry.time >= 4 else { continue }
                if cleaned.last?.text.isEmpty == true { continue }
            }
            cleaned.append(entry)
        }

        // Instrumental intro before the first line.
        if let first = cleaned.first, !first.text.isEmpty, first.time >= 4 {
            cleaned.insert((time: 0, text: ""), at: 0)
        }
        // Trailing empty marker is useless.
        while cleaned.last?.text.isEmpty == true { cleaned.removeLast() }

        return cleaned.enumerated().map { LyricLine(id: $0.offset, time: $0.element.time, text: $0.element.text) }
    }

    /// Index of the line active at `time`, or -1 before the first line.
    static func activeIndex(in lines: [LyricLine], at time: TimeInterval) -> Int {
        // Binary search for the last line whose time <= `time`.
        var low = 0, high = lines.count - 1, result = -1
        while low <= high {
            let mid = (low + high) / 2
            if lines[mid].time <= time {
                result = mid
                low = mid + 1
            } else {
                high = mid - 1
            }
        }
        return result
    }
}
