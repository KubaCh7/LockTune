import AppKit
import SwiftUI

/// Rounded album art with a soft shadow that shrinks when paused (Apple Music style).
struct ArtworkView: View {
    let image: NSImage?
    let token: Int
    let isPlaying: Bool

    var body: some View {
        GeometryReader { geo in
            let side = min(geo.size.width, geo.size.height)
            let shape = RoundedRectangle(cornerRadius: max(6, side * 0.045), style: .continuous)

            ZStack {
                if let image {
                    Image(nsImage: image)
                        .resizable()
                        .interpolation(.high)
                        .aspectRatio(contentMode: .fill)
                        .id(token)
                        .transition(.opacity)
                } else {
                    placeholder(side: side)
                }
            }
            .frame(width: geo.size.width, height: geo.size.height)
            .clipShape(shape)
            .overlay(shape.strokeBorder(.white.opacity(0.1), lineWidth: 0.5))
            .shadow(color: .black.opacity(isPlaying ? 0.42 : 0.25),
                    radius: side * (isPlaying ? 0.09 : 0.05),
                    y: side * (isPlaying ? 0.05 : 0.025))
            .scaleEffect(isPlaying ? 1 : 0.84)
            .animation(.spring(response: 0.55, dampingFraction: 0.66), value: isPlaying)
            .animation(.easeInOut(duration: 0.6), value: token)
        }
    }

    private func placeholder(side: CGFloat) -> some View {
        ZStack {
            LinearGradient(colors: [.white.opacity(0.25), .white.opacity(0.08)],
                           startPoint: .topLeading, endPoint: .bottomTrailing)
            Image(systemName: "music.note")
                .font(.system(size: side * 0.32, weight: .medium))
                .opacity(0.55)
        }
    }
}
