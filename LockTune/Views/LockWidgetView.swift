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
        .environment(\.frameInterval, settings.frameInterval)
        .environment(\.glassLevel, settings.glassLevel)
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
    //
    // Vertical zones (fractions of the screen height):
    //   0.00–0.22  clock (system or ours)
    //   0.22–0.70  lyrics — never lower: the user name and "Touch ID or Enter
    //              Password" sit around 0.75–0.85 and macOS draws them above
    //              everything, even our wallpaper
    //   artwork + controls may go down to the height chosen in Settings

    private static let topFraction: CGFloat = 0.22
    private static let lyricsBottomFraction: CGFloat = 0.70

    private func top(_ size: CGSize) -> CGFloat { size.height * Self.topFraction }
    /// Bottom edge for artwork + controls (user setting).
    private func widgetBottom(_ size: CGSize) -> CGFloat {
        max(size.height * (1 - settings.activeBottomInset), top(size) + size.height * 0.25)
    }
    /// Bottom edge for lyrics (fixed, safe).
    private func lyricsBottom(_ size: CGSize) -> CGFloat {
        min(widgetBottom(size), size.height * Self.lyricsBottomFraction)
    }

    @ViewBuilder
    private func content(size: CGSize) -> some View {
        Group {
            if player.hasTrack {
                if showsLyrics {
                    switch settings.lyricsPosition {
                    case .right, .left: sideBySideLayout(size: size)
                    case .center: centerLyricsLayout(size: size)
                    }
                } else {
                    playerLayout(size: size)
                }
            }
        }
        .frame(width: size.width, height: size.height, alignment: .topLeading)
        .shadow(color: drawsBackground ? .clear : .black.opacity(0.35), radius: 18, y: 6)
    }

    /// Artwork + controls, centred, ending at the chosen height.
    private func playerLayout(size: CGSize) -> some View {
        let height = widgetBottom(size) - top(size)
        let controlsHeight: CGFloat = 150
        // 100 % is a medium size; the slider grows it up to the free space.
        let base = min(size.height * 0.2, size.width * 0.2)
        let art = max(90, min(base * settings.activeArtworkScale, height - controlsHeight - 24, size.width * 0.5))
        return VStack(spacing: 20) {
            artwork(side: art)
            controls(width: max(art * 0.95, 300))
        }
        .frame(width: size.width, height: height, alignment: .bottom)
        .offset(y: top(size))
    }

    /// Screen split in two halves: artwork + controls fill one half (as big as
    /// the size slider allows), lyrics sit in the other half above the user name.
    private func sideBySideLayout(size: CGSize) -> some View {
        let half = size.width / 2
        let columnHeight = widgetBottom(size) - top(size)
        let lyricsHeight = lyricsBottom(size) - top(size)
        let base = min(size.height * 0.2, size.width * 0.16)
        let art = max(90, min(base * settings.activeArtworkScale, columnHeight - 170, half * 0.86))
        let lyricsOnRight = settings.lyricsPosition == .right

        let artColumn = VStack(spacing: 18) {
            artwork(side: art)
            controls(width: max(min(art, half * 0.86), 300))
        }
        .frame(width: half, height: columnHeight)

        let lyricsColumn = lyricsView(alignment: .leading)
            .frame(width: half * 0.82, height: lyricsHeight)
            .frame(width: half, height: columnHeight, alignment: lyricsOnRight ? .topLeading : .topTrailing)
            .transition(.opacity.combined(with: .offset(x: lyricsOnRight ? 40 : -40)))

        return HStack(spacing: 0) {
            if lyricsOnRight {
                artColumn
                lyricsColumn
            } else {
                lyricsColumn
                artColumn
            }
        }
        .frame(width: size.width, height: columnHeight, alignment: .top)
        .offset(y: top(size))
    }

    /// Big centred lyrics, a small artwork + controls row underneath — all
    /// above the user name.
    private func centerLyricsLayout(size: CGSize) -> some View {
        let rowHeight: CGFloat = 150
        let bottom = min(widgetBottom(size), size.height * 0.74)
        let lyricsHeight = max(160, bottom - top(size) - rowHeight - 16)
        return VStack(spacing: 16) {
            lyricsView(alignment: .center)
                .frame(width: min(size.width * 0.62, 900), height: lyricsHeight)
                .transition(.opacity)
            HStack(spacing: 16) {
                artwork(side: min(76 * settings.activeArtworkScale, rowHeight))
                controls(width: 340)
            }
        }
        .frame(width: size.width, alignment: .top)
        .offset(y: top(size))
    }

    private func lyricsView(alignment: LyricsAlignment) -> some View {
        let mega = settings.glassLevel == .mega
        return LyricsView(lyrics: lyrics, player: player, overlay: overlay,
                   alignment: alignment, wordByWord: settings.wordByWord, lead: settings.lyricsLead,
                   textScale: settings.lyricsScale)
            .shadow(color: drawsBackground || mega ? .clear : .black.opacity(0.45), radius: 10)
            // Mega Liquid Glass: lyrics float on a big glass panel.
            .padding(mega ? 14 : 0)
            .glassBackground(RoundedRectangle(cornerRadius: 30, style: .continuous), onlyInMega: true)
    }

    private func artwork(side: CGFloat) -> some View {
        let mega = settings.glassLevel == .mega
        return ArtworkView(image: player.artwork, token: player.artworkToken, isPlaying: player.isPlaying)
            // Mega Liquid Glass: the artwork sits in a thin glass frame.
            .padding(mega ? min(10, max(5, side * 0.025)) : 0)
            .glassBackground(RoundedRectangle(cornerRadius: side * 0.09, style: .continuous), onlyInMega: true)
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
                .glassBackground(Circle())
                .overlay(Circle().strokeBorder(.white.opacity(isOn ? 0.6 : 0), lineWidth: 1))
                .contentTransition(.symbolEffect(.replace))
        }
        .buttonStyle(PressableButtonStyle())
        .help(help)
    }
}
