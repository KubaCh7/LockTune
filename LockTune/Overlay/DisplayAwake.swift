import Foundation
import IOKit.pwr_mgt

/// Holds a "prevent display sleep" power assertion (public IOKit API) — used
/// while the widget is on the lock screen and music plays, so the screen
/// doesn't go black a few seconds after locking.
final class DisplayAwake {
    private var assertionID: IOPMAssertionID = 0
    private(set) var isHeld = false

    func set(_ hold: Bool) {
        hold ? acquire() : release()
    }

    private func acquire() {
        guard !isHeld else { return }
        let result = IOPMAssertionCreateWithName(
            kIOPMAssertionTypePreventUserIdleDisplaySleep as CFString,
            IOPMAssertionLevel(kIOPMAssertionLevelOn),
            "LockTune is showing Now Playing on the lock screen" as CFString,
            &assertionID)
        isHeld = result == kIOReturnSuccess
    }

    private func release() {
        guard isHeld else { return }
        IOPMAssertionRelease(assertionID)
        isHeld = false
    }

    deinit { release() }
}
