import AppKit
import SwiftUI

/// Desktop turntable widget: a deck tinted with the artwork's colours, a
/// spinning vinyl with the album art as its label, a tone arm that rests on
/// the record while playing, and a now-playing card underneath.
struct TurntableView: View {
    @ObservedObject var player: NowPlayingController
    @ObservedObject var settings: AppSettings
    var onDragChanged: () -> Void
    var onDragEnded: () -> Void

    static let baseSize = CGSize(width: 250, height: 290)
    static let infoHeight: CGFloat = 78

    private var scale: CGFloat { CGFloat(settings.turntableScale) }
    private var palette: ArtworkPalette { player.hasTrack ? player.palette : .fallback }
    private var deckTop: Color { palette.primary.mixed(with: .black, 0.42).color }
    private var deckBottom: Color { palette.primary.mixed(with: .black, 0.62).color }

    var body: some View {
        VStack(spacing: 10 * scale) {
            deck
            info
        }
        .padding(10)
        .environment(\.colorScheme, .dark)
        .environment(\.liteMode, settings.liteMode)
        .environment(\.frameInterval, settings.frameInterval)
        .animation(.easeInOut(duration: 0.8), value: palette)
        // Drag anywhere to move the widget (the controller moves the window).
        .gesture(
            DragGesture(minimumDistance: 3)
                .onChanged { _ in onDragChanged() }
                .onEnded { _ in onDragEnded() }
        )
    }

    // MARK: Deck

    private var deck: some View {
        let w = Self.baseSize.width * scale
        let h = Self.baseSize.height * scale
        let recordSize = 176 * scale
        let recordCenter = CGPoint(x: w * 0.5, y: h * 0.42)

        return ZStack(alignment: .topLeading) {
            RoundedRectangle(cornerRadius: 30 * scale, style: .continuous)
                .fill(LinearGradient(colors: [deckTop, deckBottom], startPoint: .top, endPoint: .bottom))
                .overlay(RoundedRectangle(cornerRadius: 30 * scale, style: .continuous)
                    .strokeBorder(.white.opacity(0.08), lineWidth: 1))
                .shadow(color: .black.opacity(settings.liteMode ? 0.2 : 0.35),
                        radius: settings.liteMode ? 6 : 18, y: 8)

            // Power light (on while playing).
            Circle()
                .strokeBorder(.white.opacity(0.55), lineWidth: 2 * scale)
                .frame(width: 22 * scale, height: 22 * scale)
                .overlay(Circle()
                    .fill(.white.opacity(player.isPlaying ? 0.9 : 0.25))
                    .frame(width: 7 * scale, height: 7 * scale))
                .position(x: 28 * scale, y: 28 * scale)

            SpinningRecord(artwork: player.artwork, isPlaying: player.isPlaying, size: recordSize)
                .position(recordCenter)
                .onTapGesture { player.playPause() }
                .help(player.isPlaying ? "Pause" : "Play")

            // Colour dots from the artwork palette.
            HStack(spacing: 8 * scale) {
                ForEach(Array([palette.primary, palette.secondary, palette.tertiary,
                               palette.primary.mixed(with: .white, 0.6)].enumerated()), id: \.offset) { _, rgb in
                    Circle()
                        .fill(rgb.color)
                        .overlay(Circle().strokeBorder(.white.opacity(0.25), lineWidth: 0.5))
                        .frame(width: 11 * scale, height: 11 * scale)
                }
            }
            .position(x: 62 * scale, y: h - 40 * scale)

            ToneArm(isPlaying: player.isPlaying, scale: scale)
                .frame(width: w, height: h)
                .allowsHitTesting(false)
        }
        .frame(width: w, height: h)
    }

    // MARK: Info card

    private var info: some View {
        VStack(spacing: 3 * scale) {
            Text(player.track?.title ?? "Nothing playing")
                .font(.system(size: 14 * scale, weight: .bold))
            Text(player.track?.artist ?? "Start a song in Spotify or Music")
                .font(.system(size: 12 * scale, weight: .medium))
                .opacity(0.7)
            HStack(spacing: 5 * scale) {
                Image(systemName: player.isPlaying ? "waveform" : "pause.fill")
                    .font(.system(size: 9 * scale, weight: .bold))
                Text(player.hasTrack
                     ? (player.isPlaying ? "Playing on \(player.sourceName ?? "")" : "Paused")
                     : "Idle")
                    .font(.system(size: 10 * scale, weight: .medium))
            }
            .opacity(0.55)
            .padding(.top, 2 * scale)
        }
        .lineLimit(1)
        .foregroundStyle(.white)
        .padding(.horizontal, 14 * scale)
        .frame(width: Self.baseSize.width * scale, height: Self.infoHeight * scale)
        .background(
            RoundedRectangle(cornerRadius: 22 * scale, style: .continuous)
                .fill(deckBottom)
                .overlay(RoundedRectangle(cornerRadius: 22 * scale, style: .continuous)
                    .strokeBorder(.white.opacity(0.08), lineWidth: 1))
                .shadow(color: .black.opacity(0.25), radius: settings.liteMode ? 4 : 10, y: 5)
        )
    }
}

/// Vinyl record that spins at a calm ~33 rpm while playing and stops where it
/// is when paused.
private struct SpinningRecord: View {
    let artwork: NSImage?
    let isPlaying: Bool
    let size: CGFloat

    @Environment(\.frameInterval) private var frameInterval
    @State private var baseAngle: Double = 0
    @State private var baseDate = Date()
    /// Degrees per second (33⅓ rpm = 200°/s).
    private let speed: Double = 200

    var body: some View {
        TimelineView(.animation(minimumInterval: frameInterval, paused: !isPlaying)) { context in
            let angle = baseAngle + (isPlaying ? context.date.timeIntervalSince(baseDate) * speed : 0)
            record
                .rotationEffect(.degrees(angle))
        }
        // Gloss stays put while the record turns underneath it.
        .overlay(
            Circle()
                .fill(AngularGradient(colors: [.white.opacity(0.0), .white.opacity(0.12), .white.opacity(0.0),
                                               .white.opacity(0.08), .white.opacity(0.0)],
                                      center: .center))
                .allowsHitTesting(false)
        )
        .frame(width: size, height: size)
        .onChange(of: isPlaying) { playing in
            if playing {
                baseDate = Date()
            } else {
                baseAngle += Date().timeIntervalSince(baseDate) * speed
            }
        }
    }

    private var record: some View {
        ZStack {
            Circle().fill(Color(white: 0.07))
            // Grooves.
            ForEach(0..<9, id: \.self) { i in
                Circle()
                    .strokeBorder(.white.opacity(i.isMultiple(of: 3) ? 0.09 : 0.04), lineWidth: 1)
                    .padding(size * 0.04 + CGFloat(i) * size * 0.028)
            }
            // Label = album art.
            Group {
                if let artwork {
                    Image(nsImage: artwork)
                        .resizable()
                        .interpolation(.high)
                        .aspectRatio(contentMode: .fill)
                } else {
                    Color(white: 0.92)
                }
            }
            .frame(width: size * 0.46, height: size * 0.46)
            .clipShape(Circle())
            .overlay(Circle().strokeBorder(.black.opacity(0.25), lineWidth: 1))
            // Spindle.
            Circle()
                .fill(Color(white: 0.15))
                .frame(width: size * 0.035, height: size * 0.035)
        }
        .frame(width: size, height: size)
    }
}

/// Tone arm pivoting from the bottom-right corner: on the record while
/// playing, swung off to the side when paused.
private struct ToneArm: View {
    let isPlaying: Bool
    let scale: CGFloat

    var body: some View {
        GeometryReader { geo in
            let pivot = CGPoint(x: geo.size.width - 46 * scale, y: geo.size.height - 50 * scale)
            let tip = CGPoint(x: pivot.x - 72 * scale, y: pivot.y - 128 * scale)

            ZStack(alignment: .topLeading) {
                // Arm.
                Path { path in
                    path.move(to: pivot)
                    path.addLine(to: tip)
                }
                .stroke(LinearGradient(colors: [Color(white: 0.92), Color(white: 0.7)],
                                       startPoint: .top, endPoint: .bottom),
                        style: StrokeStyle(lineWidth: 5 * scale, lineCap: .round))

                // Head-shell.
                RoundedRectangle(cornerRadius: 3 * scale)
                    .fill(Color(white: 0.9))
                    .frame(width: 14 * scale, height: 24 * scale)
                    .rotationEffect(.radians(atan2(tip.y - pivot.y, tip.x - pivot.x) + .pi / 2))
                    .position(tip)

                // Counterweight.
                RoundedRectangle(cornerRadius: 3 * scale)
                    .fill(Color(white: 0.85))
                    .frame(width: 16 * scale, height: 22 * scale)
                    .rotationEffect(.radians(atan2(tip.y - pivot.y, tip.x - pivot.x) + .pi / 2))
                    .position(x: pivot.x + (pivot.x - tip.x) * 0.12, y: pivot.y + (pivot.y - tip.y) * 0.12)
            }
            .shadow(color: .black.opacity(0.35), radius: 4, x: 2, y: 4)
            .rotationEffect(.degrees(isPlaying ? 0 : 20),
                            anchor: UnitPoint(x: pivot.x / max(geo.size.width, 1),
                                              y: pivot.y / max(geo.size.height, 1)))
            .animation(.spring(response: 0.9, dampingFraction: 0.75), value: isPlaying)

            // Pivot base.
            Circle()
                .fill(Color.white.opacity(0.08))
                .frame(width: 46 * scale, height: 46 * scale)
                .overlay(Circle().fill(Color(white: 0.8)).frame(width: 14 * scale, height: 14 * scale))
                .position(pivot)
        }
    }
}
