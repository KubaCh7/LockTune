import AppKit

/// Playback state reported by a player.
enum PlaybackState: String, Equatable {
    case playing, paused, stopped
}

/// Static metadata for the current track.
struct TrackInfo: Equatable {
    /// Stable identifier supplied by the player (persistent ID / Spotify URI).
    var id: String
    var title: String
    var artist: String
    var album: String
    /// Duration in seconds (0 if unknown, e.g. radio streams).
    var duration: TimeInterval
}

/// A cheap, frequently-polled view of the player.
struct PlayerSnapshot: Equatable {
    var state: PlaybackState
    /// Position in seconds.
    var position: TimeInterval
    /// Identifier of the current track, `nil` when there is none.
    var trackID: String?
    /// When `position` was true (middle of the Apple Event round trip), so
    /// interpolation isn't thrown off by how long the query took to come back.
    var capturedAt = Date()
}

/// Where a provider can get the artwork for a track.
enum ArtworkSource {
    case image(NSImage)
    case url(URL)
    case none
}

/// A source of "now playing" information and transport commands.
///
/// Every method is called on one serial background queue (never concurrently).
/// Implementations must never
/// launch the player app as a side effect (check `isRunning` first).
protocol NowPlayingProvider: AnyObject {
    /// Stable identifier, used for the "preferred source" setting.
    var id: String { get }
    var displayName: String { get }
    var bundleIdentifier: String { get }
    /// Distributed notification the player posts whenever track/state changes.
    var changeNotificationName: Notification.Name? { get }

    func snapshot() -> PlayerSnapshot?
    func currentTrack() -> TrackInfo?
    func artwork(for track: TrackInfo) -> ArtworkSource

    func playPause()
    func nextTrack()
    func previousTrack()
    func seek(to position: TimeInterval)

    /// AppleScript error number from the last failed call (-1743 = Automation denied).
    var lastErrorCode: Int? { get }
    var lastErrorMessage: String? { get }
}

extension NowPlayingProvider {
    var isRunning: Bool {
        !NSRunningApplication.runningApplications(withBundleIdentifier: bundleIdentifier).isEmpty
    }

    var isInstalled: Bool {
        NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleIdentifier) != nil
    }

    var appIcon: NSImage? {
        guard let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleIdentifier) else { return nil }
        return NSWorkspace.shared.icon(forFile: url.path)
    }
}
