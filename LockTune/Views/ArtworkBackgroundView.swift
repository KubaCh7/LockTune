import AppKit
import SwiftUI

/// Full-screen background built from the artwork: either a blurred, saturated,
/// slowly drifting version (Apple Music style) or the artwork as a wallpaper.
struct ArtworkBackgroundView: View {
    @Environment(\.liteMode) private var lite
    let artwork: NSImage?
    let blurred: NSImage?
    let palette: ArtworkPalette
    let style: BackgroundStyle
    let token: Int

    var body: some View {
        GeometryReader { geo in
            ZStack {
                // Base: dominant-colour gradient (also the "nothing playing" look).
                LinearGradient(colors: [palette.primary.color, palette.secondary.color, palette.tertiary.color],
                               startPoint: .topLeading, endPoint: .bottomTrailing)

                Group {
                    if style == .artwork, let artwork {
                        Image(nsImage: artwork)
                            .resizable()
                            .interpolation(.high)
                            .aspectRatio(contentMode: .fill)
                            .frame(width: geo.size.width, height: geo.size.height)
                            .clipped()
                    } else if let blurred {
                        if lite {
                            // Static: the pre-blurred image, no animation.
                            Image(nsImage: blurred)
                                .resizable()
                                .interpolation(.medium)
                                .aspectRatio(contentMode: .fill)
                                .frame(width: geo.size.width, height: geo.size.height)
                                .clipped()
                        } else {
                            DriftingArtwork(image: blurred, size: geo.size)
                        }
                    }
                }
                .id("\(token)-\(style.rawValue)")
                .transition(.opacity)

                // Tint with the dominant colours.
                LinearGradient(colors: [palette.primary.color.opacity(0.35), .clear, palette.secondary.color.opacity(0.4)],
                               startPoint: .top, endPoint: .bottom)

                // Legibility scrim + vignette.
                (palette.isLight ? Color.white.opacity(0.06) : Color.black.opacity(style == .artwork ? 0.26 : 0.18))
                RadialGradient(colors: [.clear, .black.opacity(palette.isLight ? 0.08 : 0.3)],
                               center: .center,
                               startRadius: min(geo.size.width, geo.size.height) * 0.35,
                               endRadius: max(geo.size.width, geo.size.height) * 0.75)
            }
            .frame(width: geo.size.width, height: geo.size.height)
            .animation(.easeInOut(duration: 1.0), value: token)
            .animation(.easeInOut(duration: 0.6), value: style)
        }
    }
}

/// Three copies of the blurred artwork rotating and drifting at different
/// speeds; because the source is tiny and pre-blurred this is cheap to draw.
struct DriftingArtwork: View {
    let image: NSImage
    let size: CGSize
    @Environment(\.frameInterval) private var frameInterval

    var body: some View {
        TimelineView(.animation(minimumInterval: frameInterval)) { context in
            let t = context.date.timeIntervalSinceReferenceDate
            let side = max(size.width, size.height)
            ZStack {
                layer(t: t, side: side * 1.9, speed: 3, phase: 0, drift: 0.06, soft: false)
                layer(t: t, side: side * 1.1, speed: -5, phase: 2.1, drift: 0.2, soft: true)
                    .opacity(0.8)
                layer(t: t, side: side * 0.8, speed: 7, phase: 4.2, drift: 0.28, soft: true)
                    .opacity(0.65)
            }
            .frame(width: size.width, height: size.height)
            .clipped()
        }
        .saturation(1.15)
        .drawingGroup()
    }

    private func layer(t: Double, side: CGFloat, speed: Double, phase: Double, drift: CGFloat, soft: Bool) -> some View {
        Image(nsImage: image)
            .resizable()
            .interpolation(.medium)
            .frame(width: side, height: side)
            .mask(
                RadialGradient(colors: soft ? [.black, .black.opacity(0.6), .clear] : [.black, .black],
                               center: .center, startRadius: 0, endRadius: side * 0.5)
            )
            .rotationEffect(.degrees(t * speed + phase * 57.3))
            .offset(x: CGFloat(sin(t * 0.07 + phase)) * size.width * drift,
                    y: CGFloat(cos(t * 0.05 + phase)) * size.height * drift)
    }
}
