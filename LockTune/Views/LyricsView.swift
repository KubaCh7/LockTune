import AppKit
import SwiftUI

enum LyricsAlignment {
    case leading, center

    var frame: Alignment { self == .center ? .center : .leading }
    var text: TextAlignment { self == .center ? .center : .leading }
    var anchor: UnitPoint { self == .center ? .center : .leading }
}

/// Apple Music-style lyrics: time-synced when possible, plain otherwise.
struct LyricsView: View {
    @ObservedObject var lyrics: LyricsController
    @ObservedObject var player: NowPlayingController
    @ObservedObject var overlay: OverlayState
    var alignment: LyricsAlignment = .leading
    var wordByWord = true
    /// Seconds the highlight runs ahead of the playback position.
    var lead: TimeInterval = 0.6
    /// Text size multiplier.
    var textScale: CGFloat = 0.85

    var body: some View {
        switch lyrics.state {
        case .loaded(.synced(let lines)):
            SyncedLyricsView(lines: lines, revision: lyrics.revision, player: player, overlay: overlay,
                             alignment: alignment, wordByWord: wordByWord, lead: lead, textScale: textScale)
        case .loaded(.plain(let lines)):
            PlainLyricsView(lines: lines, alignment: alignment)
        case .loaded(.instrumental):
            message("Instrumental", systemImage: "music.note")
        case .loading:
            InstrumentalDots(isActive: true, dotSize: 14)
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: alignment.frame)
        case .notFound:
            message("No lyrics found for this song", systemImage: "text.badge.xmark")
        case .failed:
            message("Couldn't load lyrics", systemImage: "wifi.exclamationmark")
        case .idle:
            message("Lyrics are turned off", systemImage: "quote.bubble")
        }
    }

    private func message(_ text: String, systemImage: String) -> some View {
        Label(text, systemImage: systemImage)
            .font(.system(size: 22, weight: .semibold))
            .opacity(0.6)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: alignment.frame)
    }
}

// MARK: - Synced

private struct SyncedLyricsView: View {
    let lines: [LyricLine]
    let revision: Int
    @ObservedObject var player: NowPlayingController
    @ObservedObject var overlay: OverlayState
    let alignment: LyricsAlignment
    let wordByWord: Bool
    /// Lines are highlighted slightly early, as Apple Music does (adjustable).
    let lead: TimeInterval
    let textScale: CGFloat
    @Environment(\.frameInterval) private var frameInterval

    var body: some View {
        // `player` is observed, so a seek while paused (which bumps
        // `timelineRevision`) re-renders this even though the timeline is paused.
        TimelineView(.animation(minimumInterval: frameInterval, paused: !player.isPlaying)) { context in
            let time = player.position(at: context.date) + lead
            LyricsStack(lines: lines,
                        activeIndex: LRCParser.activeIndex(in: lines, at: time),
                        time: time,
                        revision: revision,
                        scrollOffset: overlay.lyricsScrollOffset,
                        isBrowsing: overlay.isBrowsingLyrics,
                        isPlaying: player.isPlaying,
                        alignment: alignment,
                        wordByWord: wordByWord,
                        textScale: textScale,
                        onSeek: { player.seek(to: $0) })
        }
    }
}

private struct LineHeightKey: PreferenceKey {
    static let defaultValue: [Int: CGFloat] = [:]
    static func reduce(value: inout [Int: CGFloat], nextValue: () -> [Int: CGFloat]) {
        value.merge(nextValue()) { $1 }
    }
}

/// Lays lines out manually so each one can spring to its new position with a
/// small per-line delay — the signature Apple Music "wave" effect.
private struct LyricsStack: View {
    let lines: [LyricLine]
    let activeIndex: Int
    let time: TimeInterval
    let revision: Int
    let scrollOffset: CGFloat
    let isBrowsing: Bool
    let isPlaying: Bool
    let alignment: LyricsAlignment
    let wordByWord: Bool
    let textScale: CGFloat
    let onSeek: (TimeInterval) -> Void

    @State private var heights: [Int: CGFloat] = [:]
    @State private var revealed = false
    @State private var hovered: Int?

    var body: some View {
        GeometryReader { geo in
            let fontSize = min(max(geo.size.width * 0.05, 18), 42) * textScale
            let spacing = fontSize * 0.3
            // Current line near the top so several upcoming lines are visible.
            let anchor = geo.size.height * (alignment == .center ? 0.3 : 0.16)
            let tops = lineTops(spacing: spacing, estimatedHeight: fontSize * 1.5)
            let focus = min(max(activeIndex, 0), max(lines.count - 1, 0))
            let focusTop = tops.isEmpty ? 0 : tops[focus]
            // Manual scrolling, clamped between the first and the last line.
            let scroll = min(max(scrollOffset, focusTop - (tops.last ?? 0)), focusTop)

            ZStack(alignment: .topLeading) {
                ForEach(lines) { line in
                    let index = line.id
                    let distance = activeIndex < 0 ? index + 1 : abs(index - activeIndex)
                    let isActive = index == activeIndex
                    let lineTop: CGFloat = tops.isEmpty ? 0 : tops[index]
                    let y: CGFloat = anchor - focusTop + scroll + lineTop

                    LyricLineView(line: line,
                                  isActive: isActive,
                                  isPlaying: isPlaying,
                                  fontSize: fontSize,
                                  isHovered: hovered == index,
                                  alignment: alignment,
                                  progress: isActive && wordByWord ? line.wordProgress(at: time) : nil)
                        .frame(width: geo.size.width, alignment: alignment.frame)
                        .background(GeometryReader { proxy in
                            Color.clear.preference(key: LineHeightKey.self, value: [index: proxy.size.height])
                        })
                        .scaleEffect(isActive ? 1 : 0.94, anchor: alignment.anchor)
                        .opacity(revealed ? opacity(distance: distance, isActive: isActive) : 0)
                        .blur(radius: blur(distance: distance))
                        .offset(y: revealed ? y : y + 36)
                        .animation(.spring(response: 0.65, dampingFraction: 0.84).delay(stagger(index, focus: focus)),
                                   value: activeIndex)
                        .animation(.spring(response: 0.7, dampingFraction: 0.85).delay(Double(min(index, 14)) * 0.035),
                                   value: revealed)
                        .onHover { inside in
                            if inside { hovered = index } else if hovered == index { hovered = nil }
                        }
                        .onTapGesture { onSeek(line.time) }
                }
            }
            .frame(width: geo.size.width, height: geo.size.height, alignment: .topLeading)
            .clipped()
            .onPreferenceChange(LineHeightKey.self) { heights = $0 }
            .mask(
                LinearGradient(stops: [
                    .init(color: .clear, location: 0),
                    .init(color: .black, location: 0.06),
                    .init(color: .black, location: 0.86),
                    .init(color: .clear, location: 1),
                ], startPoint: .top, endPoint: .bottom)
            )
        }
        .onAppear { reveal() }
        .onChange(of: revision) { reveal() }
    }

    private func reveal() {
        revealed = false
        Task { @MainActor in
            try? await Task.sleep(nanoseconds: 50_000_000)
            revealed = true
        }
    }

    private func lineTops(spacing: CGFloat, estimatedHeight: CGFloat) -> [CGFloat] {
        var result: [CGFloat] = []
        result.reserveCapacity(lines.count)
        var y: CGFloat = 0
        for line in lines {
            result.append(y)
            y += (heights[line.id] ?? estimatedHeight) + spacing
        }
        return result
    }

    private func opacity(distance: Int, isActive: Bool) -> Double {
        if isActive { return 1 }
        if isBrowsing { return 0.65 }
        return max(0.28, 0.6 - Double(distance - 1) * 0.06)
    }

    private func blur(distance: Int) -> CGFloat {
        guard !isBrowsing, distance > 1, distance <= 10 else { return 0 }
        return min(CGFloat(distance - 1) * 0.6, 2.5)
    }

    /// Lines below the current one follow a little later than the ones above.
    private func stagger(_ index: Int, focus: Int) -> Double {
        let d = index - focus + 1
        return d > 0 ? Double(min(d, 10)) * 0.045 : 0
    }
}

private struct LyricLineView: View {
    let line: LyricLine
    let isActive: Bool
    let isPlaying: Bool
    let fontSize: CGFloat
    let isHovered: Bool
    let alignment: LyricsAlignment
    /// Per-word progress (0…1) for the word-by-word highlight; nil = whole line lit.
    let progress: [Double]?

    var body: some View {
        Group {
            if line.isInstrumental {
                InstrumentalDots(isActive: isActive && isPlaying, dotSize: fontSize * 0.32)
                    .frame(height: fontSize * 1.1, alignment: alignment.frame)
            } else {
                lineText
                    .font(.system(size: fontSize, weight: .bold))
                    .lineSpacing(fontSize * 0.08)
                    .multilineTextAlignment(alignment.text)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .padding(.vertical, 6)
        .padding(.horizontal, 12)
        .frame(maxWidth: .infinity, alignment: alignment.frame)
        .background(
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .fill(.primary.opacity(isHovered ? 0.08 : 0))
        )
        .contentShape(Rectangle())
    }

    /// Words already sung are fully lit, the current one fills in, the rest
    /// stay dim — like Apple Music's word-by-word lyrics.
    private var lineText: Text {
        guard let progress, progress.count == line.words.count else {
            return Text(line.text)
        }
        var text = Text("")
        for (word, value) in zip(line.words, progress) {
            text = text + Text(word.text).foregroundStyle(.primary.opacity(0.38 + 0.62 * value))
        }
        return text
    }
}

/// Three breathing dots shown during instrumental breaks.
struct InstrumentalDots: View {
    let isActive: Bool
    var dotSize: CGFloat = 12
    @Environment(\.frameInterval) private var frameInterval

    var body: some View {
        TimelineView(.animation(minimumInterval: frameInterval, paused: !isActive)) { context in
            let t = context.date.timeIntervalSinceReferenceDate
            HStack(spacing: dotSize * 0.7) {
                ForEach(0..<3, id: \.self) { k in
                    Circle()
                        .frame(width: dotSize, height: dotSize)
                        .opacity(isActive ? 0.35 + 0.65 * (0.5 + 0.5 * sin(t * 3 - Double(k) * 0.8)) : 0.35)
                }
            }
            .scaleEffect(isActive ? 1 + 0.08 * CGFloat(sin(t * 1.6)) : 1, anchor: .leading)
        }
    }
}

// MARK: - Plain

private struct PlainLyricsView: View {
    let lines: [String]
    let alignment: LyricsAlignment

    var body: some View {
        ScrollView(.vertical, showsIndicators: false) {
            VStack(alignment: alignment == .center ? .center : .leading, spacing: 12) {
                Text("Lyrics aren't synced for this song")
                    .font(.system(size: 12, weight: .semibold))
                    .textCase(.uppercase)
                    .opacity(0.5)
                    .padding(.bottom, 8)
                ForEach(Array(lines.enumerated()), id: \.offset) { _, line in
                    Text(line.isEmpty ? " " : line)
                        .font(.system(size: 26, weight: .bold))
                        .opacity(0.85)
                        .multilineTextAlignment(alignment.text)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            .padding(.vertical, 60)
            .padding(.horizontal, 12)
            .frame(maxWidth: .infinity, alignment: alignment.frame)
        }
        .mask(
            LinearGradient(stops: [
                .init(color: .clear, location: 0),
                .init(color: .black, location: 0.08),
                .init(color: .black, location: 0.9),
                .init(color: .clear, location: 1),
            ], startPoint: .top, endPoint: .bottom)
        )
    }
}
