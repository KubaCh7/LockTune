import Combine
import Foundation

/// Loads lyrics for whatever `NowPlayingController` is playing.
@MainActor
final class LyricsController: ObservableObject {
    enum State: Equatable {
        case idle
        case loading
        case loaded(LyricsContent)
        case notFound
        case failed
    }

    @Published private(set) var state: State = .idle
    /// Incremented whenever new lyrics arrive (drives the "lines animate in" effect).
    @Published private(set) var revision = 0
    /// Per-song timing correction in seconds (+ = lyrics earlier), remembered
    /// for every song you adjust.
    @Published private(set) var songOffset: Double = 0
    private static let offsetsKey = "lyricsSongOffsets"
    static let offsetStep = 0.25

    private let service = LyricsService()
    private let settings: AppSettings
    private var currentTrack: TrackInfo?
    private var task: Task<Void, Never>?
    private var cancellables = Set<AnyCancellable>()

    var syncedLines: [LyricLine]? {
        if case .loaded(.synced(let lines)) = state { return lines }
        return nil
    }

    init(nowPlaying: NowPlayingController, settings: AppSettings) {
        self.settings = settings

        nowPlaying.$track
            .removeDuplicates { $0?.id == $1?.id && $0?.title == $1?.title }
            .sink { [weak self] track in self?.load(track) }
            .store(in: &cancellables)

        settings.$lyricsEnabled
            .dropFirst()
            .sink { [weak self] _ in
                // Read the new value on the next turn (Published emits in willSet).
                Task { @MainActor in self?.load(self?.currentTrack) }
            }
            .store(in: &cancellables)
    }

    /// Retries after a network failure, e.g. when the overlay is opened.
    func reloadIfNeeded() {
        if state == .failed { load(currentTrack) }
    }

    func clearCache() {
        Task { [service] in await service.clearCache() }
    }

    /// Nudges the current song's lyrics earlier (+) or later (−).
    func nudge(_ delta: Double) {
        guard let track = currentTrack else { return }
        songOffset = min(max(songOffset + delta, -10), 10)
        var offsets = UserDefaults.standard.dictionary(forKey: Self.offsetsKey) as? [String: Double] ?? [:]
        offsets[track.id] = abs(songOffset) < 0.001 ? nil : songOffset
        UserDefaults.standard.set(offsets, forKey: Self.offsetsKey)
    }

    func resetAllSongOffsets() {
        UserDefaults.standard.removeObject(forKey: Self.offsetsKey)
        songOffset = 0
    }

    private func load(_ track: TrackInfo?) {
        task?.cancel()
        currentTrack = track
        let offsets = UserDefaults.standard.dictionary(forKey: Self.offsetsKey) as? [String: Double] ?? [:]
        songOffset = track.flatMap { offsets[$0.id] } ?? 0
        guard let track, settings.lyricsEnabled, !track.title.isEmpty else {
            state = .idle
            return
        }
        state = .loading
        task = Task { @MainActor [weak self, service] in
            do {
                let content = try await service.lyrics(for: track)
                guard !Task.isCancelled, let self else { return }
                self.state = .loaded(content)
                self.revision &+= 1
            } catch {
                guard !Task.isCancelled, let self else { return }
                if case LyricsService.LyricsError.notFound = error {
                    self.state = .notFound
                } else {
                    self.state = .failed
                }
            }
        }
    }
}
