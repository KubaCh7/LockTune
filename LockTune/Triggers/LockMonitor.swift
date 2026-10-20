import AppKit

/// Observes screen lock and display sleep.
///
/// Nothing can be drawn above the real macOS lock screen, so these events are
/// used to put the overlay up *behind* it: when the user wakes/unlocks the Mac
/// they land on the now-playing screen instead of their desktop.
@MainActor
final class LockMonitor {
    enum Event { case screenLocked, screenUnlocked, displaySlept, displayWoke }

    var onEvent: ((Event) -> Void)?
    private var observers: [(NotificationCenter, NSObjectProtocol)] = []

    func start() {
        let distributed = DistributedNotificationCenter.default()
        observe(distributed, "com.apple.screenIsLocked", .screenLocked)
        observe(distributed, "com.apple.screenIsUnlocked", .screenUnlocked)

        let workspace = NSWorkspace.shared.notificationCenter
        observe(workspace, NSWorkspace.screensDidSleepNotification.rawValue, .displaySlept)
        observe(workspace, NSWorkspace.screensDidWakeNotification.rawValue, .displayWoke)
    }

    private func observe(_ center: NotificationCenter, _ name: String, _ event: Event) {
        let token = center.addObserver(forName: Notification.Name(name), object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.onEvent?(event) }
        }
        observers.append((center, token))
    }
}
