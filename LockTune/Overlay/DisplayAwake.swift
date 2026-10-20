import Foundation
import IOKit.pwr_mgt

/// Keeps the display on (public IOKit power APIs) while the widget is on the
/// lock screen: a "prevent display sleep" assertion plus a periodic "user is
/// active" ping, which also resets the lock screen's own short dim timer.
@MainActor
final class DisplayAwake {
    private var preventID: IOPMAssertionID = 0
    private var activityID: IOPMAssertionID = 0
    private var holdsPrevent = false
    private var heartbeat: Timer?
    private var deadline: Date?

    var isHeld: Bool { holdsPrevent }

    /// Keeps the display on until `deadline` (nil = until `release()`).
    func hold(until deadline: Date?) {
        self.deadline = deadline
        if let deadline, deadline <= Date() {
            release()
            return
        }
        if !holdsPrevent {
            let result = IOPMAssertionCreateWithName(
                kIOPMAssertionTypePreventUserIdleDisplaySleep as CFString,
                IOPMAssertionLevel(kIOPMAssertionLevelOn),
                "LockTune is showing Now Playing on the lock screen" as CFString,
                &preventID)
            holdsPrevent = result == kIOReturnSuccess
        }
        ping()
        if heartbeat == nil {
            let timer = Timer(timeInterval: 4, repeats: true) { [weak self] _ in
                MainActor.assumeIsolated { self?.tick() }
            }
            RunLoop.main.add(timer, forMode: .common)
            heartbeat = timer
        }
    }

    func release() {
        heartbeat?.invalidate()
        heartbeat = nil
        deadline = nil
        if holdsPrevent {
            IOPMAssertionRelease(preventID)
            holdsPrevent = false
        }
        if activityID != 0 {
            IOPMAssertionRelease(activityID)
            activityID = 0
        }
    }

    private func tick() {
        if let deadline, Date() >= deadline {
            release()
        } else {
            ping()
        }
    }

    private func ping() {
        _ = IOPMAssertionDeclareUserActivity("LockTune Now Playing" as CFString, kIOPMUserActiveLocal, &activityID)
    }
}
