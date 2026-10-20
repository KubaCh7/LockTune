import AppKit
import SwiftUI

/// Shared UI state of the overlay (one instance drives every screen's window).
@MainActor
final class OverlayState: ObservableObject {
    enum Mode { case player, lyrics }

    @Published var mode: Mode = .player
    /// Vertical offset while the user swipes/drag up (≤ 0).
    @Published private(set) var dragOffset: CGFloat = 0
    @Published private(set) var isPresented = false
    @Published private(set) var isDismissing = false
    /// Manual scroll offset of the synced lyrics (0 = follow playback).
    @Published private(set) var lyricsScrollOffset: CGFloat = 0
    @Published private(set) var isBrowsingLyrics = false

    /// Called once the dismiss animation has finished.
    var onDismissed: (() -> Void)?

    static let modeAnimation = Animation.spring(response: 0.6, dampingFraction: 0.86)
    private static let dismissDistance: CGFloat = 140

    private var browseResetTask: Task<Void, Never>?
    private var wheelAccumulator: CGFloat = 0
    private var lastSwipeDelta: CGFloat = 0

    func reset() {
        mode = .player
        dragOffset = 0
        isPresented = false
        isDismissing = false
        lyricsScrollOffset = 0
        isBrowsingLyrics = false
        wheelAccumulator = 0
        browseResetTask?.cancel()
    }

    func present() {
        withAnimation(.spring(response: 0.7, dampingFraction: 0.9)) { isPresented = true }
    }

    func toggleLyrics() {
        withAnimation(Self.modeAnimation) {
            mode = mode == .player ? .lyrics : .player
        }
        lyricsScrollOffset = 0
        isBrowsingLyrics = false
    }

    /// Slides everything up (like unlocking an iPhone), then calls `onDismissed`.
    func requestDismiss() {
        guard !isDismissing else { return }
        withAnimation(.easeIn(duration: 0.3)) { isDismissing = true }
        Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: 320_000_000)
            self?.onDismissed?()
        }
    }

    // MARK: Mouse drag (on the background)

    func dragChanged(_ translation: CGFloat) {
        guard !isDismissing else { return }
        // Follow the pointer upwards, rubber-band downwards.
        dragOffset = translation < 0 ? translation : translation * 0.15
    }

    func dragEnded(_ translation: CGFloat, predicted: CGFloat) {
        if translation < -Self.dismissDistance || predicted < -400 {
            requestDismiss()
        } else {
            withAnimation(.spring(response: 0.4, dampingFraction: 0.8)) { dragOffset = 0 }
        }
    }

    // MARK: Scroll wheel / trackpad

    /// Returns true if the event was consumed.
    func handleScroll(_ event: NSEvent, hasSyncedLyrics: Bool) -> Bool {
        guard !isDismissing else { return true }
        if mode == .lyrics {
            // Plain lyrics live in a regular ScrollView: let it scroll.
            guard hasSyncedLyrics else { return false }
            scrollLyrics(event)
            return true
        }
        swipeToDismiss(event)
        return true
    }

    private func scrollLyrics(_ event: NSEvent) {
        let delta = event.hasPreciseScrollingDeltas ? event.scrollingDeltaY : event.scrollingDeltaY * 12
        guard delta != 0 else { return }
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
    }

    private func swipeToDismiss(_ event: NSEvent) {
        guard abs(event.scrollingDeltaY) >= abs(event.scrollingDeltaX) || !event.phase.isEmpty else { return }
        // Positive = fingers moving up, independent of the "natural scrolling" setting.
        let up = event.isDirectionInvertedFromDevice ? -event.scrollingDeltaY : event.scrollingDeltaY

        guard event.hasPreciseScrollingDeltas else {
            // Classic mouse wheel: a few notches "up" dismisses.
            wheelAccumulator = max(0, wheelAccumulator + up)
            if wheelAccumulator >= 3 { requestDismiss() }
            return
        }

        if !event.momentumPhase.isEmpty { return } // ignore inertia after lift-off

        if event.phase.contains(.began) || event.phase.contains(.changed) {
            lastSwipeDelta = up
            let proposed = dragOffset - up
            dragOffset = proposed < 0 ? proposed : proposed * 0.15
        } else if event.phase.contains(.ended) || event.phase.contains(.cancelled) {
            if -dragOffset > Self.dismissDistance || lastSwipeDelta > 14 {
                requestDismiss()
            } else {
                withAnimation(.spring(response: 0.4, dampingFraction: 0.8)) { dragOffset = 0 }
            }
            lastSwipeDelta = 0
        }
    }
}
