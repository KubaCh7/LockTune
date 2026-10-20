import AppKit
import SwiftUI

/// The now-playing widget: artwork + glass controls in the middle of the
/// screen, lyrics on the right when enabled, and a small toolbar on the left
/// (like the reference app). The background is transparent so the real lock
/// screen (clock, password field) stays visible, unless "artwork wallpaper" is on.
struct LockWidgetView: View {
    @ObservedObject var overlay: OverlayState
    @ObservedObject var player: NowPlayingController
    @ObservedObject var lyrics: LyricsController
    @ObservedObject var settings: AppSettings
    let presentation: LockWidgetController.Presentation
    var onClose: () -> Void

    @Namespace private var namespace

    private var showsLyrics: Bool { overlay.mode == .lyrics && player.hasTrack }
    /// Full-screen artwork + our own clock (covers the system lock screen UI).
    private var drawsBackground: Bool { settings.lockWallpaper || presentation == .preview }
    private var palette: ArtworkPalette { player.hasTrack ? player.palette : .fallback }
    private var textColor: Color { drawsBackground ? palette.textColor : .white }
    private var isLightBackground: Bool { drawsBackground && palette.isLight }

    var body: some View {
        GeometryReader { geo in
            let size = geo.size
            ZStack(alignment: .topLeading) {
                if drawsBackground {
                    background(size: size)
                        .transition(.opacity)
                }

                content(size: size)
                    .opacity(overlay.isCollapsed ? 0 : 1)
                    .scaleEffect(overlay.isCollapsed ? 0.92 : 1)
                    .allowsHitTesting(!overlay.isCollapsed)

                toolbar
                    .padding(.leading, 26)
                    .padding(.top, 34)
            }
            .frame(width: size.width, height: size.height)
            .opacity(overlay.isPresented ? 1 : 0)
            .scaleEffect(overlay.isPresented ? 1 : 0.97)
        }
        .ignoresSafeArea()
        .foregroundStyle(textColor)
        .environment(\.colorScheme, isLightBackground ? .light : .dark)
        .focusEffectDisabled()
        .animation(.easeInOut(duration: 0.5), value: drawsBackground)
        .animation(.easeInOut(duration: 0.8), value: palette)
    }

    // MARK: Background (artwork wallpaper mode / preview)

    private func background(size: CGSize) -> some View {
        ZStack(alignment: .top) {
            ArtworkBackgroundView(artwork: player.artwork,
                                  blurred: player.blurredArtwork,
                                  palette: palette,
                                  style: showsLyrics ? .blurred : settings.backgroundStyle,
                                  token: player.artworkToken)
                .contentShape(Rectangle())
                .onTapGesture { if presentation == .preview { onClose() } }

            ClockView(size: min(size.height * 0.12, 150), style: settings.clockFont, weight: settings.clockWeight)
                .padding(.top, size.height * 0.06)
                .allowsHitTesting(false)

            if presentation == .preview {
                Text("Preview · click the background or press Esc to close")
                    .font(.system(size: 12, weight: .medium))
                    .opacity(0.6)
                    .frame(maxHeight: .infinity, alignment: .bottom)
                    .padding(.bottom, 18)
                    .allowsHitTesting(false)
            }
        }
        .frame(width: size.width, height: size.height)
    }

    // MARK: Widget

    /// Vertical band between the lock-screen clock (top) and the password field (bottom).
    private func bandFrame(_ size: CGSize) -> (top: CGFloat, height: CGFloat) {
        let top = size.height * 0.27
        let bottom = size.height * 0.80
        return (top, bottom - top)
    }

    @ViewBuilder
    private func content(size: CGSize) -> some View {
        let band = bandFrame(size)
        Group {
            if player.hasTrack {
                if showsLyrics {
                    lyricsLayout(size: size, bandHeight: band.height)
                } else {
                    playerLayout(size: size, bandHeight: band.height)
                }
            }
        }
        .frame(width: size.width, height: band.height)
        .offset(y: band.top)
        .shadow(color: drawsBackground ? .clear : .black.opacity(0.35), radius: 18, y: 6)
    }

    private func playerLayout(size: CGSize, bandHeight: CGFloat) -> some View {
        let art = min(bandHeight * 0.6, size.width * 0.28)
        return VStack(spacing: art * 0.07) {
            artwork(side: art)
            controls(width: max(art * 0.95, 300))
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    /// Artwork + controls on the left, lyrics on the right (reference video).
    private func lyricsLayout(size: CGSize, bandHeight: CGFloat) -> some View {
        let art = min(bandHeight * 0.48, size.width * 0.22)
        return HStack(alignment: .center, spacing: size.width * 0.05) {
            VStack(spacing: 18) {
                artwork(side: art)
                controls(width: max(art, 300))
            }
            .frame(width: max(art, 300))

            LyricsView(lyrics: lyrics, player: player, overlay: overlay)
                .frame(width: size.width * 0.4)
                .frame(maxHeight: .infinity)
                .shadow(color: drawsBackground ? .clear : .black.opacity(0.45), radius: 10)
                .transition(.opacity.combined(with: .offset(x: 40)))
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private func artwork(side: CGFloat) -> some View {
        ArtworkView(image: player.artwork, token: player.artworkToken, isPlaying: player.isPlaying)
            .matchedGeometryEffect(id: "artwork", in: namespace)
            .frame(width: side, height: side)
            .contentShape(Rectangle())
            .onTapGesture { overlay.toggleLyrics() }
            .help(showsLyrics ? "Back to player" : "Show lyrics")
    }

    private func controls(width: CGFloat) -> some View {
        PlayerControlsView(player: player,
                           showsTitle: true,
                           isLyricsActive: showsLyrics,
                           onToggleLyrics: { overlay.toggleLyrics() })
            .matchedGeometryEffect(id: "controls", in: namespace)
            .frame(width: width)
    }

    // MARK: Left toolbar

    private var toolbar: some View {
        VStack(spacing: 10) {
            ToolbarButton(systemName: showsLyrics ? "quote.bubble.fill" : "quote.bubble",
                          isOn: showsLyrics, help: "Lyrics") {
                overlay.toggleLyrics()
            }
            .disabled(!player.hasTrack)

            ToolbarButton(systemName: settings.lockWallpaper ? "photo.fill" : "photo",
                          isOn: settings.lockWallpaper, help: "Artwork wallpaper") {
                settings.lockWallpaper.toggle()
            }

            ToolbarButton(systemName: overlay.isCollapsed ? "eye.slash" : "eye",
                          isOn: overlay.isCollapsed, help: overlay.isCollapsed ? "Show widget" : "Hide widget") {
                overlay.toggleCollapsed()
            }

            if presentation == .preview {
                ToolbarButton(systemName: "xmark", isOn: false, help: "Close preview (Esc)", action: onClose)
            }
        }
    }
}

private struct ToolbarButton: View {
    let systemName: String
    let isOn: Bool
    let help: String
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: systemName)
                .font(.system(size: 13, weight: .semibold))
                .frame(width: 34, height: 34)
                .background(.ultraThinMaterial, in: Circle())
                .overlay(Circle().strokeBorder(.white.opacity(isOn ? 0.55 : 0.15), lineWidth: isOn ? 1 : 0.5))
                .contentTransition(.symbolEffect(.replace))
        }
        .buttonStyle(PressableButtonStyle())
        .help(help)
    }
}
