import AppKit
import SwiftUI

/// Root view of every overlay window.
struct LockScreenView: View {
    @ObservedObject var overlay: OverlayState
    @ObservedObject var player: NowPlayingController
    @ObservedObject var lyrics: LyricsController
    @ObservedObject var settings: AppSettings

    @Namespace private var namespace

    private var palette: ArtworkPalette { player.hasTrack ? player.palette : .fallback }
    private var showsLyrics: Bool { overlay.mode == .lyrics && player.hasTrack }

    var body: some View {
        GeometryReader { geo in
            let size = geo.size
            ZStack {
                ArtworkBackgroundView(artwork: player.artwork,
                                      blurred: player.blurredArtwork,
                                      palette: palette,
                                      style: showsLyrics ? .blurred : settings.backgroundStyle,
                                      token: player.artworkToken)
                    .contentShape(Rectangle())
                    .gesture(
                        DragGesture(minimumDistance: 8)
                            .onChanged { overlay.dragChanged($0.translation.height) }
                            .onEnded { overlay.dragEnded($0.translation.height,
                                                         predicted: $0.predictedEndTranslation.height) }
                    )

                foreground(size: size)
            }
            .frame(width: size.width, height: size.height)
            .offset(y: overlay.isDismissing ? -size.height : overlay.dragOffset)
            .opacity(overlay.isPresented ? 1 : 0)
            .scaleEffect(overlay.isPresented ? 1 : 1.04)
        }
        .ignoresSafeArea()
        .foregroundStyle(palette.textColor)
        .environment(\.colorScheme, palette.isLight ? .light : .dark)
        .focusEffectDisabled()
        .animation(.easeInOut(duration: 0.8), value: palette)
    }

    // MARK: Layout

    private func foreground(size: CGSize) -> some View {
        let clockSize = min(size.height * 0.135, 170)
        return VStack(spacing: 0) {
            ClockView(size: clockSize, style: settings.clockFont, weight: settings.clockWeight)
                .padding(.top, size.height * 0.055)
                .allowsHitTesting(false)

            if player.hasTrack {
                if showsLyrics {
                    switch settings.lyricsLayout {
                    case .sideBySide: sideBySideLyrics(size: size)
                    case .header: headerLyrics(size: size)
                    }
                } else {
                    playerLayout(size: size)
                }
            } else {
                Spacer()
            }

            UnlockHint()
                .padding(.bottom, 14)
        }
        .frame(width: size.width, height: size.height)
    }

    /// Clock, big centred artwork, glass controls (reference video, first half).
    private func playerLayout(size: CGSize) -> some View {
        let art = min(size.height * 0.4, size.width * 0.34)
        return VStack(spacing: art * 0.08) {
            Spacer(minLength: 12)
            artwork(side: art)
            controls(width: max(art * 0.72, 320), showsTitle: true)
            Spacer(minLength: 12)
        }
    }

    /// Artwork + controls on the left, lyrics on the right (reference video, lyrics part).
    private func sideBySideLyrics(size: CGSize) -> some View {
        let art = min(size.height * 0.32, size.width * 0.24)
        return HStack(alignment: .center, spacing: size.width * 0.06) {
            VStack(spacing: 22) {
                artwork(side: art)
                controls(width: max(art, 300), showsTitle: true)
            }
            .frame(width: max(art, 300))

            LyricsView(lyrics: lyrics, player: player, overlay: overlay)
                .frame(maxWidth: size.width * 0.44, maxHeight: .infinity)
                .transition(.opacity.combined(with: .offset(x: 40)))
        }
        .padding(.vertical, 16)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    /// Small artwork next to title/artist at the top left, lyrics fill the rest.
    private func headerLyrics(size: CGSize) -> some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack(spacing: 16) {
                artwork(side: 76)
                if let track = player.track {
                    VStack(alignment: .leading, spacing: 3) {
                        Text(track.title).font(.system(size: 20, weight: .bold))
                        Text(track.artist).font(.system(size: 17, weight: .medium)).opacity(0.65)
                    }
                    .lineLimit(1)
                    .transition(.opacity)
                }
                Spacer()
            }

            LyricsView(lyrics: lyrics, player: player, overlay: overlay)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .transition(.opacity)

            controls(width: min(size.width * 0.38, 520), showsTitle: false)
                .frame(maxWidth: .infinity)
        }
        .padding(.horizontal, size.width * 0.16)
        .padding(.top, 24)
        .padding(.bottom, 8)
    }

    // MARK: Pieces

    private func artwork(side: CGFloat) -> some View {
        ArtworkView(image: player.artwork, token: player.artworkToken, isPlaying: player.isPlaying)
            .matchedGeometryEffect(id: "artwork", in: namespace)
            .frame(width: side, height: side)
            .contentShape(Rectangle())
            .onTapGesture { overlay.toggleLyrics() }
            .help(showsLyrics ? "Back to player" : "Show lyrics")
    }

    private func controls(width: CGFloat, showsTitle: Bool) -> some View {
        PlayerControlsView(player: player,
                           showsTitle: showsTitle,
                           isLyricsActive: showsLyrics,
                           onToggleLyrics: { overlay.toggleLyrics() })
            .matchedGeometryEffect(id: "controls", in: namespace)
            .frame(width: width)
    }
}

/// iPhone-style home indicator with a hint.
private struct UnlockHint: View {
    @State private var showsText = true

    var body: some View {
        VStack(spacing: 8) {
            Text("Swipe up or press Esc to close")
                .font(.system(size: 12, weight: .medium))
                .opacity(showsText ? 0.6 : 0)
            Capsule()
                .frame(width: 140, height: 5)
                .opacity(0.5)
        }
        .allowsHitTesting(false)
        .task {
            try? await Task.sleep(nanoseconds: 4_000_000_000)
            withAnimation(.easeOut(duration: 1)) { showsText = false }
        }
    }
}
