import AppKit
import SwiftUI

/// The glass "now playing" card: title, artist, progress and transport controls.
struct PlayerControlsView: View {
    @ObservedObject var player: NowPlayingController
    var showsTitle = true
    var isLyricsActive = false
    var onToggleLyrics: (() -> Void)?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            if showsTitle, let track = player.track {
                VStack(alignment: .leading, spacing: 2) {
                    Text(track.title)
                        .font(.system(size: 14, weight: .semibold))
                    Text(track.artist)
                        .font(.system(size: 13))
                        .opacity(0.65)
                }
                .lineLimit(1)
                .frame(maxWidth: .infinity, alignment: .center)
            }

            ProgressBarView(player: player)

            HStack(spacing: 0) {
                sourceIcon
                    .frame(width: 28, alignment: .leading)
                Spacer(minLength: 8)
                HStack(spacing: 30) {
                    TransportButton(systemName: "backward.fill", size: 20) { player.previous() }
                    TransportButton(systemName: player.isPlaying ? "pause.fill" : "play.fill", size: 28) {
                        player.playPause()
                    }
                    TransportButton(systemName: "forward.fill", size: 20) { player.next() }
                }
                Spacer(minLength: 8)
                lyricsButton
                    .frame(width: 28, alignment: .trailing)
            }
        }
        .padding(.horizontal, 18)
        .padding(.top, showsTitle ? 14 : 12)
        .padding(.bottom, 14)
        .background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: 24, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 24, style: .continuous)
                .strokeBorder(.white.opacity(0.14), lineWidth: 0.5)
        )
        .shadow(color: .black.opacity(0.18), radius: 24, y: 10)
    }

    @ViewBuilder private var sourceIcon: some View {
        if let id = player.sourceBundleID,
           let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: id) {
            Image(nsImage: NSWorkspace.shared.icon(forFile: url.path))
                .resizable()
                .frame(width: 18, height: 18)
                .opacity(0.85)
                .help("Playing in \(player.sourceName ?? "")")
        } else {
            Color.clear.frame(width: 18, height: 18)
        }
    }

    @ViewBuilder private var lyricsButton: some View {
        if let onToggleLyrics {
            Button(action: onToggleLyrics) {
                Image(systemName: isLyricsActive ? "quote.bubble.fill" : "quote.bubble")
                    .font(.system(size: 15, weight: .medium))
                    .frame(width: 28, height: 28)
            }
            .buttonStyle(PressableButtonStyle())
            .help("Lyrics (L)")
        } else {
            Color.clear.frame(width: 28, height: 28)
        }
    }
}

struct TransportButton: View {
    let systemName: String
    let size: CGFloat
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: systemName)
                .font(.system(size: size, weight: .semibold))
                .contentTransition(.symbolEffect(.replace))
                .frame(width: size * 1.6, height: size * 1.6)
        }
        .buttonStyle(PressableButtonStyle())
    }
}

/// Plain button that shrinks while pressed, like iOS media controls.
struct PressableButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .contentShape(Rectangle())
            .scaleEffect(configuration.isPressed ? 0.8 : 1)
            .opacity(configuration.isPressed ? 0.55 : 1)
            .animation(.spring(response: 0.25, dampingFraction: 0.6), value: configuration.isPressed)
    }
}
