import AppKit
import SwiftUI

// Small wrappers so LockTune runs on macOS 12+ while using newer APIs
// where they exist.

extension NSApplication {
    /// `activate()` is macOS 14+; older systems use `activate(ignoringOtherApps:)`.
    func activateCompat() {
        if #available(macOS 14.0, *) {
            activate()
        } else {
            activate(ignoringOtherApps: true)
        }
    }
}

extension View {
    /// Symbol cross-fade (macOS 14+).
    @ViewBuilder func symbolReplaceTransition() -> some View {
        if #available(macOS 14.0, *) {
            contentTransition(.symbolEffect(.replace))
        } else {
            self
        }
    }

    /// Rolling digits (macOS 13+).
    @ViewBuilder func numericTextTransition() -> some View {
        if #available(macOS 13.0, *) {
            contentTransition(.numericText())
        } else {
            self
        }
    }

    @ViewBuilder func focusEffectDisabledCompat() -> some View {
        if #available(macOS 14.0, *) {
            focusEffectDisabled()
        } else {
            self
        }
    }

    /// Lets the window's glass show through forms/lists (macOS 13+).
    @ViewBuilder func hiddenScrollBackground() -> some View {
        if #available(macOS 13.0, *) {
            scrollContentBackground(.hidden)
        } else {
            self
        }
    }

    /// Grouped settings look (macOS 13+); plain form before that.
    @ViewBuilder func groupedFormStyle() -> some View {
        if #available(macOS 13.0, *) {
            formStyle(.grouped)
        } else {
            padding()
        }
    }
}

/// `LabeledContent` replacement (that one is macOS 13+).
struct LabeledRow<Content: View>: View {
    let title: String
    let content: Content

    init(_ title: String, @ViewBuilder content: () -> Content) {
        self.title = title
        self.content = content()
    }

    var body: some View {
        HStack(alignment: .firstTextBaseline) {
            Text(title)
            Spacer(minLength: 12)
            content
        }
    }
}
