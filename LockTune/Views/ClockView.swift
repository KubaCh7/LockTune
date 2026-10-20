import AppKit
import SwiftUI

/// Date + big clock, iPhone lock-screen style.
struct ClockView: View {
    let size: CGFloat
    let style: ClockFontStyle
    let weight: ClockWeight

    var body: some View {
        TimelineView(.everyMinute) { context in
            let time = Self.timeString(context.date)
            VStack(spacing: -size * 0.04) {
                Text(Self.dateString(context.date))
                    .font(.system(size: size * 0.15, weight: .semibold, design: style.design))
                    .opacity(0.92)
                Text(time)
                    .font(.system(size: size, weight: weight.fontWeight, design: style.design))
                    .numericTextTransition()
                    .animation(.easeInOut(duration: 0.5), value: time)
            }
            .lineLimit(1)
            .fixedSize()
            .shadow(color: .black.opacity(0.12), radius: 14, y: 2)
        }
    }

    // MARK: Formatting

    private static let dateFormatter: DateFormatter = {
        let f = DateFormatter()
        f.setLocalizedDateFormatFromTemplate("EEEMMMd") // "Mon, Oct 19"
        return f
    }()

    private static let timeFormatter: DateFormatter = {
        let f = DateFormatter()
        // Like iOS: no AM/PM marker, no leading zero in 12-hour mode.
        let template = DateFormatter.dateFormat(fromTemplate: "j", options: 0, locale: .current) ?? "h"
        f.dateFormat = template.contains("a") || template.contains("h") ? "h:mm" : "H:mm"
        return f
    }()

    static func dateString(_ date: Date) -> String {
        dateFormatter.string(from: date).replacingOccurrences(of: ",", with: "")
    }

    static func timeString(_ date: Date) -> String {
        timeFormatter.string(from: date)
    }
}
