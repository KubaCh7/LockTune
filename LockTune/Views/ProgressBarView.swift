import AppKit
import SwiftUI

/// Thin scrubbable progress bar with elapsed / remaining time.
struct ProgressBarView: View {
    @ObservedObject var player: NowPlayingController
    @State private var scrubPosition: TimeInterval?
    @State private var isHovering = false
    @Environment(\.frameInterval) private var frameInterval

    private var isActive: Bool { isHovering || scrubPosition != nil }

    var body: some View {
        TimelineView(.animation(minimumInterval: frameInterval, paused: !player.isPlaying || scrubPosition != nil)) { context in
            let duration = player.duration
            let position = scrubPosition ?? player.position(at: context.date)
            let fraction = duration > 0 ? min(max(position / duration, 0), 1) : 0

            VStack(spacing: 5) {
                GeometryReader { geo in
                    ZStack(alignment: .leading) {
                        Capsule().fill(.primary.opacity(0.22))
                        Capsule()
                            .fill(.primary.opacity(isActive ? 0.95 : 0.7))
                            .frame(width: max(0, geo.size.width * fraction))
                    }
                    .frame(height: isActive ? 9 : 5)
                    .frame(maxHeight: .infinity)
                    .contentShape(Rectangle())
                    .gesture(
                        DragGesture(minimumDistance: 0)
                            .onChanged { value in
                                guard duration > 0, geo.size.width > 0 else { return }
                                let f = min(max(value.location.x / geo.size.width, 0), 1)
                                scrubPosition = f * duration
                            }
                            .onEnded { _ in
                                if let target = scrubPosition { player.seek(to: target) }
                                scrubPosition = nil
                            }
                    )
                }
                .frame(height: 14)
                .onHover { isHovering = $0 }
                .animation(.spring(response: 0.3, dampingFraction: 0.75), value: isActive)

                HStack {
                    Text(Self.format(position))
                    Spacer()
                    Text(duration > 0 ? "-" + Self.format(max(0, duration - position)) : "LIVE")
                }
                .font(.system(size: 11, weight: .medium, design: .rounded).monospacedDigit())
                .opacity(0.6)
            }
        }
    }

    static func format(_ seconds: TimeInterval) -> String {
        guard seconds.isFinite else { return "0:00" }
        let total = Int(seconds.rounded(.down))
        let h = total / 3600, m = (total % 3600) / 60, s = total % 60
        return h > 0 ? String(format: "%d:%02d:%02d", h, m, s) : String(format: "%d:%02d", m, s)
    }
}
