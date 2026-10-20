import AppKit
import SwiftUI

/// The now-playing widget: artwork + glass controls sitting just above the
/// user name on the lock screen, lyrics on the right/left/centre when enabled,
/// and a small toolbar on the left (like the reference app). The background is
/// transparent so the real lock screen stays visible, unless "artwork
/// wallpaper" is on.
struct LockWidgetView: View {
    @ObservedObject var overlay: OverlayState
    @ObservedObject var player: NowPlayingController
    @ObservedObject var lyrics: LyricsController
    @ObservedObject var settings: AppSettings
    var onClose: () -> Void

    @Namespace private var namespace

    private var presentation: LockWidgetController.Presentation { overlay.presentation }
    private var showsLyrics: Bool { overlay.mode == .lyrics && player.hasTrack }
    /// Full-screen artwork + our own clock (covers the system lock screen UI).
    private var drawsBackground: Bool { settings.lockWallpaper || presentation == .preview }
    private var palette: ArtworkPalette { player.hasTrack ? player.palette : .fallback }
    private var textColor: Color { drawsBackground ? palette.textColor : .white }
    private var isLightBackground: Bool { drawsBackground && palette.isLight }
    private var shown: Bool { overlay.isPresented }

    var body: some View {
        GeometryReader { geo in
            if overlay.isActive {
                widget(size: geo.size)
            }
        }
        .ignoresSafeArea()
        .foregroundStyle(textColor)
        .environment(\.colorScheme, isLightBackground ? .light : .dark)
        .focusEffectDisabled()
    }

    private func widget(size: CGSize) -> some View {
        ZStack(alignment: .topLeading) {
            if drawsBackground {
                background(size: size)
                    // Entrance: the artwork wallpaper zooms in out of a blur.
                    .opacity(shown ? 1 : 0)
                    .scaleEffect(shown ? 1 : 1.12)
                    .blur(radius: shown ? 0 : 40)
                    .animation(.easeOut(duration: shown ? 0.55 : 0.3), value: shown)
            }

            content(size: size)
                .opacity(overlay.isCollapsed ? 0 : 1)
                .scaleEffect(overlay.isCollapsed ? 0.92 : 1)
                .allowsHitTesting(!overlay.isCollapsed)
                // Entrance: the widget rises into place with a spring.
                .opacity(shown ? 1 : 0)
                .offset(y: shown ? 0 : 70)
                .scaleEffect(shown ? 1 : 0.94, anchor: .bottom)
                .animation(shown ? .spring(response: 0.62, dampingFraction: 0.8).delay(0.08)
                                 : .easeIn(duration: 0.22), value: shown)

            toolbar
                .padding(.leading, 26)
                .padding(.top, 34)
                .opacity(shown ? 1 : 0)
                .animation(.easeOut(duration: 0.4).delay(shown ? 0.2 : 0), value: shown)
        }
        .frame(width: size.width, height: size.height)
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

    // MARK: Widget layout

    /// The widget's area: below the lock-screen clock, ending just above the
    /// user name / password field (adjustable in Settings).
    private struct Area {
        var top: CGFloat
        var height: CGFloat
    }

    private func widgetArea(_ size: CGSize) -> Area {
        let top = size.height * 0.24
        let bottom = size.height * (1 - settings.activeBottomInset)
        return Area(top: top, height: max(bottom - top, size.height * 0.22))
    }

    @ViewBuilder
    private func content(size: CGSize) -> some View {
        let area = widgetArea(size)
        // Over the normal lock screen, keep lyrics well clear of the user name
        // and the "Touch ID or Enter Password" hint below the widget.
        let lyricsGuard: CGFloat = showsLyrics && !settings.lockWallpaper ? size.height * 0.06 : 0
        let height = area.height - lyricsGuard
        Group {
            if player.hasTrack {
                if showsLyrics {
                    lyricsLayout(size: size, height: height)
                } else {
                    playerLayout(size: size, height: height)
                }
            }
        }
        .frame(width: size.width, height: height, alignment: .bottom)
        .offset(y: area.top)
        .shadow(color: drawsBackground ? .clear : .black.opacity(0.35), radius: 18, y: 6)
    }

    private func playerLayout(size: CGSize, height: CGFloat) -> some View {
        let controlsHeight: CGFloat = 150
        let wanted = min(height - controlsHeight - 24, size.width * 0.24) * settings.activeArtworkScale
        let art = max(90, min(wanted, height - controlsHeight - 24, size.width * 0.4))
        return VStack(spacing: 20) {
            artwork(side: art)
            controls(width: max(art * 0.95, 300))
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottom)
    }

    @ViewBuilder
    private func lyricsLayout(size: CGSize, height: CGFloat) -> some View {
        switch settings.lyricsPosition {
        case .right, .left:
            let art = max(90, min(min(height * 0.45, size.width * 0.2) * settings.activeArtworkScale,
                                  height - 170, size.width * 0.3))
            let column = VStack(spacing: 18) {
                artwork(side: art)
                controls(width: max(art, 300))
            }
            .frame(width: max(art, 300))

            let lyricsColumn = lyricsView(alignment: .leading)
                .frame(width: size.width * 0.4, height: height)
                .transition(.opacity.combined(with: .offset(x: settings.lyricsPosition == .right ? 40 : -40)))

            HStack(alignment: .center, spacing: size.width * 0.05) {
                if settings.lyricsPosition == .right {
                    column
                    lyricsColumn
                } else {
                    lyricsColumn
                    column
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)

        case .center:
            VStack(spacing: 16) {
                lyricsView(alignment: .center)
                    .frame(width: min(size.width * 0.62, 900))
                    .frame(maxHeight: .infinity)
                    .transition(.opacity)
                HStack(spacing: 16) {
                    artwork(side: 76 * settings.activeArtworkScale)
                    controls(width: 340)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottom)
        }
    }

    private func lyricsView(alignment: LyricsAlignment) -> some View {
        LyricsView(lyrics: lyrics, player: player, overlay: overlay,
                   alignment: alignment, wordByWord: settings.wordByWord, lead: settings.lyricsLead)
            .shadow(color: drawsBackground ? .clear : .black.opacity(0.45), radius: 10)
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

            if showsLyrics {
                ToolbarButton(systemName: lyricsPositionIcon, isOn: false,
                              help: "Lyrics position: \(settings.lyricsPosition.title)") {
                    cycleLyricsPosition()
                }
                .transition(.scale.combined(with: .opacity))
            }

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
        .animation(.spring(response: 0.4, dampingFraction: 0.8), value: showsLyrics)
    }

    private var lyricsPositionIcon: String {
        switch settings.lyricsPosition {
        case .right: return "rectangle.righthalf.inset.filled"
        case .left: return "rectangle.lefthalf.inset.filled"
        case .center: return "rectangle.center.inset.filled"
        }
    }

    private func cycleLyricsPosition() {
        let all = LyricsPosition.allCases
        let next = all[(all.firstIndex(of: settings.lyricsPosition)! + 1) % all.count]
        withAnimation(OverlayState.modeAnimation) { settings.lyricsPosition = next }
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
