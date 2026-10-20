import AppKit
import CoreGraphics

/// Fires `onIdle` once per idle period when the user has not touched the
/// keyboard/mouse/trackpad for `threshold` seconds. Needs no permissions.
@MainActor
final class IdleMonitor {
    var onIdle: (() -> Void)?
    var threshold: TimeInterval = 300
    var isEnabled = false

    private var timer: Timer?
    private var firedForCurrentIdlePeriod = false

    func start() {
        timer?.invalidate()
        let timer = Timer(timeInterval: 5, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick() }
        }
        timer.tolerance = 1
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    static var secondsSinceLastInput: TimeInterval {
        // kCGAnyInputEventType == ~0
        guard let anyInput = CGEventType(rawValue: ~0) else { return 0 }
        return CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: anyInput)
    }

    private func tick() {
        let idle = Self.secondsSinceLastInput
        if idle < 5 {
            firedForCurrentIdlePeriod = false
            return
        }
        guard isEnabled, !firedForCurrentIdlePeriod, idle >= threshold else { return }
        firedForCurrentIdlePeriod = true
        onIdle?()
    }
}
