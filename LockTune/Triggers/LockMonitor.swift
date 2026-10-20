import AppKit
import CoreGraphics

/// Tracks whether the Mac is locked.
///
/// Uses the `com.apple.screenIsLocked/Unlocked` distributed notifications and,
/// as a safety net (they are occasionally late or missing), polls the session
/// dictionary's `CGSSessionScreenIsLocked` flag.
@MainActor
final class LockMonitor {
    var onChange: ((Bool) -> Void)?
    private(set) var isLocked = false

    private var observers: [NSObjectProtocol] = []
    private var timer: Timer?
    private var lastPolled = false

    func start() {
        let center = DistributedNotificationCenter.default()
        observers.append(center.addObserver(forName: Notification.Name("com.apple.screenIsLocked"),
                                            object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.set(true) }
        })
        observers.append(center.addObserver(forName: Notification.Name("com.apple.screenIsUnlocked"),
                                            object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.set(false) }
        })

        lastPolled = Self.sessionIsLocked
        set(lastPolled)

        let timer = Timer(timeInterval: 1.5, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.poll() }
        }
        timer.tolerance = 0.3
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    static var sessionIsLocked: Bool {
        guard let dict = CGSessionCopyCurrentDictionary() as? [String: Any] else { return false }
        if let flag = dict["CGSSessionScreenIsLocked"] as? Bool { return flag }
        return (dict["CGSSessionScreenIsLocked"] as? Int) == 1
    }

    private func poll() {
        let polled = Self.sessionIsLocked
        // Only react to transitions of the polled value, so a slightly late
        // flag never overrides a fresh notification.
        guard polled != lastPolled else { return }
        lastPolled = polled
        set(polled)
    }

    private func set(_ locked: Bool) {
        guard locked != isLocked else { return }
        isLocked = locked
        onChange?(locked)
    }
}
