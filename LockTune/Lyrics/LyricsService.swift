import CryptoKit
import Foundation

/// Fetches lyrics from LRCLIB (https://lrclib.net) and caches them on disk.
actor LyricsService {
    enum LyricsError: Error { case notFound, network(Error) }

    private struct CacheEntry: Codable {
        var record: LRCLibRecord?
        var fetchedAt: Date
    }

    private let session: URLSession
    private let cacheDirectory: URL
    /// How long a "not found" answer is trusted before asking again.
    private let negativeCacheLifetime: TimeInterval = 3 * 24 * 3600

    init() {
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 12
        config.httpAdditionalHeaders = [
            // LRCLIB asks clients to identify themselves.
            "User-Agent": "LockTune/1.0 (macOS lock-screen player; https://github.com/)",
        ]
        session = URLSession(configuration: config)

        let base = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
        cacheDirectory = base
            .appendingPathComponent(Bundle.main.bundleIdentifier ?? "LockTune", isDirectory: true)
            .appendingPathComponent("Lyrics", isDirectory: true)
        try? FileManager.default.createDirectory(at: cacheDirectory, withIntermediateDirectories: true)
    }

    func lyrics(for track: TrackInfo) async throws -> LyricsContent {
        let key = cacheKey(for: track)

        if let cached = readCache(key) {
            if let record = cached.record, let content = Self.content(from: record) {
                return content
            }
            if Date().timeIntervalSince(cached.fetchedAt) < negativeCacheLifetime {
                throw LyricsError.notFound
            }
        }

        let record = try await fetch(track)
        writeCache(key, CacheEntry(record: record, fetchedAt: Date()))
        guard let record, let content = Self.content(from: record) else { throw LyricsError.notFound }
        return content
    }

    func clearCache() {
        try? FileManager.default.removeItem(at: cacheDirectory)
        try? FileManager.default.createDirectory(at: cacheDirectory, withIntermediateDirectories: true)
    }

    // MARK: Network

    private func fetch(_ track: TrackInfo) async throws -> LRCLibRecord? {
        // 1. Exact signature lookup.
        var exact = URLComponents(string: "https://lrclib.net/api/get")!
        exact.queryItems = [
            URLQueryItem(name: "artist_name", value: track.artist),
            URLQueryItem(name: "track_name", value: track.title),
            URLQueryItem(name: "album_name", value: track.album),
            URLQueryItem(name: "duration", value: String(Int(track.duration.rounded()))),
        ]
        let exactRecord: LRCLibRecord? = try await get(exact.url!)
        // Only stop here if it's *synced*: LRCLIB often has a plain-only entry
        // for the exact match while a synced version of the same song exists.
        if let exactRecord, exactRecord.syncedLyrics?.isEmpty == false {
            return exactRecord
        }

        // 2. Fuzzy search with a cleaned-up title, pick the closest duration.
        var search = URLComponents(string: "https://lrclib.net/api/search")!
        search.queryItems = [
            URLQueryItem(name: "track_name", value: Self.cleanTitle(track.title)),
            URLQueryItem(name: "artist_name", value: Self.primaryArtist(track.artist)),
        ]
        guard let results: [LRCLibRecord] = try await get(search.url!), !results.isEmpty else {
            return exactRecord.flatMap { Self.content(from: $0) != nil ? $0 : nil }
        }

        func durationDelta(_ r: LRCLibRecord) -> Double {
            guard track.duration > 0, let d = r.duration else { return 0 }
            return abs(d - track.duration)
        }
        let plausible = results.filter { durationDelta($0) <= 5 }
        let pool = plausible.isEmpty ? results : plausible
        let best = pool.min { a, b in
            let aSynced = (a.syncedLyrics?.isEmpty == false) ? 0 : 1
            let bSynced = (b.syncedLyrics?.isEmpty == false) ? 0 : 1
            if aSynced != bSynced { return aSynced < bSynced }
            return durationDelta(a) < durationDelta(b)
        }
        if let best, best.syncedLyrics?.isEmpty == false, durationDelta(best) <= 5 {
            return best
        }
        // No synced version anywhere: prefer the exact (plain) match.
        if let exactRecord, Self.content(from: exactRecord) != nil { return exactRecord }
        return best
    }

    /// GET + decode; returns nil on 404.
    private func get<T: Decodable>(_ url: URL) async throws -> T? {
        let result: (Data, URLResponse)
        do {
            result = try await session.data(from: url)
        } catch {
            throw LyricsError.network(error)
        }
        let (data, response) = result
        guard let http = response as? HTTPURLResponse else { return nil }
        if http.statusCode == 404 { return nil }
        guard (200..<300).contains(http.statusCode) else {
            throw LyricsError.network(URLError(.badServerResponse))
        }
        return try? JSONDecoder().decode(T.self, from: data)
    }

    // MARK: Helpers

    static func content(from record: LRCLibRecord) -> LyricsContent? {
        if let synced = record.syncedLyrics, !synced.isEmpty {
            let lines = LRCParser.parse(synced)
            if lines.contains(where: { !$0.isInstrumental }) { return .synced(lines) }
        }
        if let plain = record.plainLyrics, !plain.isEmpty {
            return .plain(plain.components(separatedBy: .newlines))
        }
        if record.instrumental == true { return .instrumental }
        return nil
    }

    /// "Song (feat. X) - Remastered 2011" → "Song"
    static func cleanTitle(_ title: String) -> String {
        var t = title
        for pattern in [#"\s*[\(\[](feat\.?|ft\.?|with)\s[^\)\]]*[\)\]]"#,
                        #"\s+-\s+.*(remaster|version|edit|live|mono|stereo|mix).*$"#] {
            t = t.replacingOccurrences(of: pattern, with: "", options: [.regularExpression, .caseInsensitive])
        }
        return t.trimmingCharacters(in: .whitespaces)
    }

    static func primaryArtist(_ artist: String) -> String {
        let separators = [", ", " & ", " feat. ", " ft. ", " x "]
        var result = artist
        for s in separators {
            if let range = result.range(of: s, options: .caseInsensitive) {
                result = String(result[..<range.lowerBound])
            }
        }
        return result
    }

    // MARK: Cache

    private func cacheKey(for track: TrackInfo) -> String {
        // "v2": entries cached before synced lyrics were preferred are ignored.
        let raw = ["v2", track.artist, track.title, track.album, String(Int(track.duration.rounded()))]
            .joined(separator: "\u{1F}")
            .lowercased()
        let digest = SHA256.hash(data: Data(raw.utf8))
        return digest.map { String(format: "%02x", $0) }.joined()
    }

    private func readCache(_ key: String) -> CacheEntry? {
        let url = cacheDirectory.appendingPathComponent(key + ".json")
        guard let data = try? Data(contentsOf: url) else { return nil }
        return try? JSONDecoder().decode(CacheEntry.self, from: data)
    }

    private func writeCache(_ key: String, _ entry: CacheEntry) {
        let url = cacheDirectory.appendingPathComponent(key + ".json")
        if let data = try? JSONEncoder().encode(entry) {
            try? data.write(to: url, options: .atomic)
        }
    }
}
