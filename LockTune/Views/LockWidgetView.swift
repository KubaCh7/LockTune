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
    // Three independent settings (per mode):
    //   • artwork size    — the artwork simply grows; if the block no longer
    //                        fits above the chosen height it extends lower
    //   • player height   — where the bottom of artwork + controls sits
    //   • lyrics position — moves the lyrics block up/down. By default it sits
    //                        between the clock and the user name.

    private static let topFraction: CGFloat = 0.22
    /// Lyrics block height (fraction of the screen height).
    private static let lyricsHeightFraction: CGFloat = 0.44
    private static let controlsHeight: CGFloat = 150

    private func top(_ size: CGSize) -> CGFloat { size.height * Self.topFraction }

    /// Bottom edge for artwork + controls (user setting).
    private func widgetBottom(_ size: CGSize) -> CGFloat {
        max(size.height * (1 - settings.activeBottomInset), top(size) + size.height * 0.25)
    }

    /// Top of the lyrics block: below the clock, moved down by the lyrics slider.
    private func lyricsTop(_ size: CGSize) -> CGFloat {
        max(size.height * 0.01, size.height * (0.26 + settings.activeLyricsOffset))
    }

    private func lyricsHeight(_ size: CGSize) -> CGFloat {
        min(size.height * Self.lyricsHeightFraction, size.height * 0.98 - lyricsTop(size))
    }

    /// Top of a block of `height` whose bottom should sit at the chosen
    /// height — but never above the clock area (then it extends lower).
    private func blockTop(height: CGFloat, size: CGSize) -> CGFloat {
        max(top(size), widgetBottom(size) - height)
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

    /// Largest artwork that still fits on screen with the controls below it.
    private func maxArtwork(_ size: CGSize, width: CGFloat) -> CGFloat {
        min(width, size.height * 0.97 - top(size) - Self.controlsHeight - 20)
    }

    /// Artwork + controls, centred.
    private func playerLayout(size: CGSize) -> some View {
        let base = min(size.height * 0.2, size.width * 0.2)
        let art = max(90, min(base * settings.activeArtworkScale, maxArtwork(size, width: size.width * 0.5)))
        let blockHeight = art + 20 + Self.controlsHeight
        return VStack(spacing: 20) {
            artwork(side: art)
            controls(width: max(art * 0.95, 300))
        }
        .frame(width: size.width, height: blockHeight, alignment: .top)
        .offset(y: blockTop(height: blockHeight, size: size))
    }

    /// Screen split in two halves: artwork + controls in one (as big as the
    /// size slider says), lyrics in the other at their own height.
    private func sideBySideLayout(size: CGSize) -> some View {
        let half = size.width / 2
        let base = min(size.height * 0.2, size.width * 0.16)
        let art = max(90, min(base * settings.activeArtworkScale, maxArtwork(size, width: half * 0.88)))
        let blockHeight = art + 18 + Self.controlsHeight
        let lyricsOnRight = settings.lyricsPosition == .right

        return ZStack(alignment: .topLeading) {
            VStack(spacing: 18) {
                artwork(side: art)
                controls(width: max(min(art, half * 0.88), 300))
            }
            .frame(width: half, height: blockHeight, alignment: .top)
            .offset(x: lyricsOnRight ? 0 : half, y: blockTop(height: blockHeight, size: size))

            lyricsView(alignment: .leading)
                .frame(width: half * 0.82, height: lyricsHeight(size))
                .frame(width: half, alignment: lyricsOnRight ? .leading : .trailing)
                .offset(x: lyricsOnRight ? half : 0, y: lyricsTop(size))
                .transition(.opacity.combined(with: .offset(x: lyricsOnRight ? 40 : -40)))
        }
        .frame(width: size.width, height: size.height, alignment: .topLeading)
    }

    /// Big centred lyrics, a small artwork + controls row underneath.
    private func centerLyricsLayout(size: CGSize) -> some View {
        let rowHeight: CGFloat = 150
        let textTop = lyricsTop(size)
        let textHeight = max(160, lyricsHeight(size) - rowHeight - 16)
        let rowTop = max(textTop + textHeight + 16, widgetBottom(size) - rowHeight)
        return ZStack(alignment: .topLeading) {
            lyricsView(alignment: .center)
                .frame(width: min(size.width * 0.62, 900), height: textHeight)
                .frame(width: size.width)
                .offset(y: textTop)
                .transition(.opacity)
            HStack(spacing: 16) {
                artwork(side: min(76 * settings.activeArtworkScale, rowHeight))
                controls(width: 340)
            }
            .frame(width: size.width)
            .offset(y: min(rowTop, size.height * 0.97 - rowHeight))
        }
        .frame(width: size.width, height: size.height, alignment: .topLeading)
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
