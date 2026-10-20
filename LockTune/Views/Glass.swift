import AppKit
import SwiftUI

/// How much Liquid Glass the widget uses.
enum GlassLevel: String, CaseIterable, Identifiable {
    /// Classic frosted blur (the original look).
    case off
    /// Liquid Glass on the player card and toolbar.
    case normal
    /// Glass everywhere: player card, every button, a glass frame around the
    /// artwork and a glass panel behind the lyrics.
    case mega

    var id: String { rawValue }
    var title: String {
        switch self {
        case .off: return "Off"
        case .normal: return "Liquid Glass"
        case .mega: return "Mega Liquid Glass"
        }
    }
}

private struct GlassLevelKey: EnvironmentKey {
    static let defaultValue: GlassLevel = .normal
}

extension EnvironmentValues {
    var glassLevel: GlassLevel {
        get { self[GlassLevelKey.self] }
        set { self[GlassLevelKey.self] = newValue }
    }
}

extension View {
    /// Glass background in `shape`, following the `glassLevel` environment.
    /// `onlyInMega` pieces are left plain unless the level is Mega.
    func glassBackground<S: InsettableShape>(_ shape: S, onlyInMega: Bool = false) -> some View {
        modifier(GlassBackground(shape: shape, onlyInMega: onlyInMega))
    }
}

/// Real Liquid Glass (`glassEffect`) on macOS 26+, a hand-made imitation
/// (material + specular gradients) on older systems.
struct GlassBackground<S: InsettableShape>: ViewModifier {
    @Environment(\.glassLevel) private var level
    let shape: S
    var onlyInMega = false

    @ViewBuilder
    func body(content: Content) -> some View {
        if onlyInMega && level != .mega {
            content
        } else if level == .off {
            content
                .background(.ultraThinMaterial, in: shape)
                .overlay(shape.strokeBorder(.white.opacity(0.14), lineWidth: 0.5))
        } else {
            liquid(content)
        }
    }

    @ViewBuilder
    private func liquid(_ content: Content) -> some View {
        #if compiler(>=6.2)
        if #available(macOS 26.0, *) {
            content
                .glassEffect(level == .mega ? .clear.interactive() : .regular.interactive(), in: shape)
                .overlay { if level == .mega { rim(strength: 0.7) } }
        } else {
            imitation(content)
        }
        #else
        imitation(content)
        #endif
    }

    /// Frosted material + a soft top sheen + a bright, uneven rim — reads as
    /// glass on systems without the real effect.
    private func imitation(_ content: Content) -> some View {
        let mega = level == .mega
        return content
            .background {
                ZStack {
                    shape.fill(mega ? Material.ultraThinMaterial : Material.thinMaterial)
                    shape.fill(LinearGradient(colors: [.white.opacity(mega ? 0.22 : 0.14), .clear, .white.opacity(0.05)],
                                              startPoint: .top, endPoint: .bottom))
                }
            }
            .overlay(rim(strength: mega ? 1 : 0.7))
            .shadow(color: .black.opacity(mega ? 0.28 : 0.18), radius: mega ? 22 : 14, y: mega ? 10 : 6)
    }

    /// Light catching the edge of the glass: bright top-left, faint middle,
    /// a second highlight bottom-right.
    private func rim(strength: Double) -> some View {
        shape.strokeBorder(
            LinearGradient(colors: [.white.opacity(0.75 * strength), .white.opacity(0.08 * strength),
                                    .white.opacity(0.4 * strength)],
                           startPoint: .topLeading, endPoint: .bottomTrailing),
            lineWidth: 1.2)
    }
}
