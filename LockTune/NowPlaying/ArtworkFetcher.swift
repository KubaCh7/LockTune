import AppKit

/// Downloads artwork from a URL, or looks it up via the public iTunes Search
/// API when the player does not expose any (common for Apple Music streams).
enum ArtworkFetcher {
    private static let session: URLSession = {
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 10
        config.urlCache = URLCache(memoryCapacity: 16 << 20, diskCapacity: 64 << 20)
        return URLSession(configuration: config)
    }()

    static func image(from url: URL) async -> NSImage? {
        guard let result = try? await session.data(from: url) else { return nil }
        if let http = result.1 as? HTTPURLResponse, http.statusCode >= 400 { return nil }
        return NSImage(data: result.0)
    }

    /// Best-effort lookup of high-resolution album art by artist + album/title.
    static func searchITunes(for track: TrackInfo) async -> NSImage? {
        let term = [track.artist, track.album.isEmpty ? track.title : track.album]
            .filter { !$0.isEmpty }
            .joined(separator: " ")
        guard !term.isEmpty else { return nil }

        var components = URLComponents(string: "https://itunes.apple.com/search")!
        components.queryItems = [
            URLQueryItem(name: "term", value: term),
            URLQueryItem(name: "entity", value: track.album.isEmpty ? "song" : "album"),
            URLQueryItem(name: "limit", value: "1"),
        ]
        guard let url = components.url,
              let result = try? await session.data(from: url),
              let json = try? JSONSerialization.jsonObject(with: result.0) as? [String: Any],
              let first = (json["results"] as? [[String: Any]])?.first,
              let small = first["artworkUrl100"] as? String
        else { return nil }

        let large = small.replacingOccurrences(of: "100x100bb", with: "1000x1000bb")
        guard let artURL = URL(string: large) else { return nil }
        return await image(from: artURL)
    }
}
