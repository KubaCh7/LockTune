import AppKit

/// Shared implementation for players that speak AppleScript (Music, Spotify).
///
/// Subclasses only describe how to read the duration and the artwork; the
/// polling/command scripts are identical apart from the application id.
class AppleScriptPlayerProvider: NowPlayingProvider {
    let id: String
    let displayName: String
    let bundleIdentifier: String
    let changeNotificationName: Notification.Name?

    let runner: AppleScriptRunner
    /// `tell application id "…"` avoids name lookups (and the "Where is …?" dialog).
    var tellTarget: String { "application id \"\(bundleIdentifier)\"" }

    var lastErrorCode: Int? { runner.lastErrorCode }
    var lastErrorMessage: String? { runner.lastErrorMessage }

    init(id: String, displayName: String, bundleIdentifier: String,
         changeNotification: String?, runner: AppleScriptRunner) {
        self.id = id
        self.displayName = displayName
        self.bundleIdentifier = bundleIdentifier
        self.changeNotificationName = changeNotification.map { Notification.Name($0) }
        self.runner = runner
    }

    // MARK: Scripts subclasses customise

    /// AppleScript expression (inside the tell block) that yields the track id.
    var trackIDExpression: String { "persistent ID of current track" }
    /// AppleScript expression for the duration of `current track`.
    var durationExpression: String { "duration of current track" }
    /// Converts the raw duration value into seconds.
    func durationSeconds(from raw: Double) -> TimeInterval { raw }
    var previousCommand: String { "previous track" }

    // MARK: Polling

    private lazy var snapshotScript = """
    tell \(tellTarget)
        with timeout of 2 seconds
            -- Compare against the enum instead of coercing it to text:
            -- coercion can yield raw codes («constant ****kPSP») in some setups.
            set st to "stopped"
            if player state is playing then set st to "playing"
            if player state is paused then set st to "paused"
            set pos to 0
            set tid to ""
            try
                set pos to player position
            end try
            try
                set tid to \(trackIDExpression)
            end try
            return {st, pos, tid}
        end timeout
    end tell
    """

    private lazy var trackScript = """
    tell \(tellTarget)
        with timeout of 3 seconds
            set tid to \(trackIDExpression)
            set nm to ""
            set ar to ""
            set al to ""
            set du to 0
            try
                set nm to name of current track
            end try
            try
                set ar to artist of current track
            end try
            try
                set al to album of current track
            end try
            try
                set du to \(durationExpression)
            end try
            return {tid, nm, ar, al, du}
        end timeout
    end tell
    """

    func snapshot() -> PlayerSnapshot? {
        guard isRunning, let result = runner.run(snapshotScript) else { return nil }
        let stateText = result.item(1)?.stringValue ?? "stopped"
        let state: PlaybackState
        switch stateText {
        case "playing": state = .playing
        case "paused": state = .paused
        default: state = .stopped
        }
        let position = result.item(2)?.doubleValue ?? 0
        let trackID = result.item(3)?.stringValue.flatMap { $0.isEmpty ? nil : $0 }
        return PlayerSnapshot(state: state,
                              position: position.isFinite ? max(0, position) : 0,
                              trackID: state == .stopped ? nil : trackID)
    }

    func currentTrack() -> TrackInfo? {
        guard isRunning, let result = runner.run(trackScript) else { return nil }
        guard let id = result.item(1)?.stringValue, !id.isEmpty else { return nil }
        let rawDuration = result.item(5)?.doubleValue ?? 0
        return TrackInfo(id: id,
                         title: result.item(2)?.stringValue ?? "",
                         artist: result.item(3)?.stringValue ?? "",
                         album: result.item(4)?.stringValue ?? "",
                         duration: rawDuration.isFinite ? durationSeconds(from: rawDuration) : 0)
    }

    func artwork(for track: TrackInfo) -> ArtworkSource { .none }

    /// Every script this provider runs, for `--self-test` (compiled, never executed).
    var selfTestScripts: [(name: String, source: String)] {
        [("snapshot", snapshotScript),
         ("track", trackScript),
         ("playpause", "tell \(tellTarget) to playpause"),
         ("next", "tell \(tellTarget) to next track"),
         ("previous", "tell \(tellTarget) to \(previousCommand)"),
         ("seek", "tell \(tellTarget) to set player position to 1.5")]
    }

    // MARK: Commands

    private func command(_ verb: String) {
        guard isRunning else { return }
        runner.run("tell \(tellTarget) to \(verb)")
    }

    func playPause() { command("playpause") }
    func nextTrack() { command("next track") }
    func previousTrack() { command(previousCommand) }

    func seek(to position: TimeInterval) {
        guard isRunning else { return }
        // String(format:) is not localised, so the decimal separator is always ".".
        let value = String(format: "%.3f", max(0, position))
        runner.run("tell \(tellTarget) to set player position to \(value)", cache: false)
    }
}

/// Apple Music (Music.app).
final class AppleMusicProvider: AppleScriptPlayerProvider {
    init(runner: AppleScriptRunner) {
        super.init(id: "music", displayName: "Music",
                   bundleIdentifier: "com.apple.Music",
                   changeNotification: "com.apple.Music.playerInfo",
                   runner: runner)
    }

    private lazy var artworkScript = """
    tell \(tellTarget)
        with timeout of 4 seconds
            try
                if (count of artworks of current track) > 0 then
                    try
                        return raw data of artwork 1 of current track
                    on error
                        return data of artwork 1 of current track
                    end try
                end if
            end try
        end timeout
    end tell
    return ""
    """

    override var selfTestScripts: [(name: String, source: String)] {
        super.selfTestScripts + [("artwork", artworkScript)]
    }

    override func artwork(for track: TrackInfo) -> ArtworkSource {
        // Tracks streamed from Apple Music that are not in the library often
        // expose no artwork to AppleScript; the controller then falls back to
        // the iTunes Search API.
        guard isRunning, let result = runner.run(artworkScript), !result.isMissingValue else { return .none }
        let data = result.data
        if !data.isEmpty, let image = NSImage(data: data) {
            return .image(image)
        }
        return .none
    }
}

/// Spotify desktop app.
final class SpotifyProvider: AppleScriptPlayerProvider {
    init(runner: AppleScriptRunner) {
        super.init(id: "spotify", displayName: "Spotify",
                   bundleIdentifier: "com.spotify.client",
                   changeNotification: "com.spotify.client.PlaybackStateChanged",
                   runner: runner)
    }

    override var trackIDExpression: String { "id of current track" }
    /// Spotify reports the duration in milliseconds.
    override func durationSeconds(from raw: Double) -> TimeInterval { raw / 1000 }

    private var artworkURLScript: String { "tell \(tellTarget) to return artwork url of current track" }

    override var selfTestScripts: [(name: String, source: String)] {
        super.selfTestScripts + [("artwork", artworkURLScript)]
    }

    override func artwork(for track: TrackInfo) -> ArtworkSource {
        guard isRunning,
              let result = runner.run(artworkURLScript),
              let string = result.stringValue,
              let url = URL(string: string), url.scheme?.hasPrefix("http") == true
        else { return .none }
        return .url(url)
    }
}
