import AppKit
import SwiftUI

/// Shared UI state of the widget (one instance drives every screen's panel).
@MainActor
final class OverlayState: ObservableObject {
    enum Mode { case player, lyrics }

    /// Kept between appearances, so lyrics stay on if you turned them on.
    @Published var mode: Mode = .player
    /// Drives the entrance/exit animation (the views animate on this value).
    @Published private(set) var isPresented = false
    /// While false the panels render nothing at all (they stay alive, invisible,
    /// so they can appear instantly when the Mac locks).
    @Published var isActive = false
    @Published var presentation: LockWidgetController.Presentation = .lockScreen
    /// "Hide widget" toolbar toggle: only the toolbar stays visible.
    @Published var isCollapsed = false
    /// Manual scroll offset of the synced lyrics (0 = follow playback).
    @Published private(set) var lyricsScrollOffset: CGFloat = 0
    @Published private(set) var isBrowsingLyrics = false

    static let modeAnimation = Animation.spring(response: 0.6, dampingFraction: 0.86)

    private var browseResetTask: Task<Void, Never>?

    func prepare() {
        isPresented = false
        isCollapsed = false
        lyricsScrollOffset = 0
        isBrowsingLyrics = false
        browseResetTask?.cancel()
    }

    func present() {
        isPresented = true
    }

    func dismiss() {
        isPresented = false
    }

    /// Called when lyrics are shown so the position is fresh.
    var onLyricsShown: (() -> Void)?

    func toggleLyrics() {
        withAnimation(Self.modeAnimation) {
            mode = mode == .player ? .lyrics : .player
            isCollapsed = false
        }
        lyricsScrollOffset = 0
        isBrowsingLyrics = false
        if mode == .lyrics { onLyricsShown?() }
    }

    func toggleCollapsed() {
        withAnimation(.spring(response: 0.5, dampingFraction: 0.85)) { isCollapsed.toggle() }
    }

    // MARK: Scroll wheel / trackpad (lyrics only)

    /// Returns true if the event was consumed.
    func handleScroll(_ event: NSEvent, hasSyncedLyrics: Bool) -> Bool {
        guard mode == .lyrics, hasSyncedLyrics, !isCollapsed else { return false }
        let delta = event.hasPreciseScrollingDeltas ? event.scrollingDeltaY : event.scrollingDeltaY * 12
        guard delta != 0 else { return true }
        isBrowsingLyrics = true
        lyricsScrollOffset += delta

        // Snap back to the current line after a few seconds without scrolling.
        browseResetTask?.cancel()
        browseResetTask = Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: 3_000_000_000)
            guard let self, !Task.isCancelled else { return }
            withAnimation(.spring(response: 0.7, dampingFraction: 0.85)) {
                self.lyricsScrollOffset = 0
                self.isBrowsingLyrics = false
            }
        }
        return true
    }
}
