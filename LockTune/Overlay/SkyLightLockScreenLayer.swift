import AppKit

/// `LockScreenLayer` backed by the private SkyLight window-server framework:
/// windows are moved into a space with an absolute level above the lock
/// screen, which is how lock-screen widget apps put content there.
///
/// Everything is resolved at runtime with `dlsym`; if a symbol is missing
/// (e.g. a future macOS changes SkyLight) `isAvailable` is false and LockTune
/// falls back to showing the widget right after unlocking.
///
/// The window only *displays* above the lock screen. It never receives the
/// keyboard: it's a non-activating panel that can't become key while locked,
/// so typing still goes to the system password field.
final class SkyLightLockScreenLayer: LockScreenLayer {
    private typealias MainConnectionID = @convention(c) () -> Int32
    private typealias SpaceCreate = @convention(c) (Int32, Int32, Int32) -> Int32
    private typealias SpaceSetAbsoluteLevel = @convention(c) (Int32, Int32, Int32) -> Int32
    private typealias ShowSpaces = @convention(c) (Int32, CFArray) -> Int32
    private typealias SpaceAddWindows = @convention(c) (Int32, Int32, CFArray, Int32) -> Int32

    /// kCGSSpaceAbsoluteLevelNotificationCenterAtScreenLock: the level macOS
    /// itself uses for notifications shown on the lock screen (lock screen = 300).
    private static let aboveLockScreenLevel: Int32 = 400

    let isAvailable: Bool
    private var connection: Int32 = 0

    private static let frameworkPath = "/System/Library/PrivateFrameworks/SkyLight.framework/Versions/A/SkyLight"
    private static let requiredSymbols = ["SLSMainConnectionID", "SLSSpaceCreate", "SLSSpaceSetAbsoluteLevel",
                                          "SLSShowSpaces", "SLSSpaceAddWindowsAndRemoveFromSpaces"]

    /// Whether this macOS still has every function we need (used by `--self-test`).
    static var missingSymbols: [String] {
        guard let handle = dlopen(frameworkPath, RTLD_NOW) else { return ["SkyLight.framework"] }
        return requiredSymbols.filter { dlsym(handle, $0) == nil }
    }
    private var space: Int32 = 0
    private var addWindows: SpaceAddWindows?

    init() {
        guard let handle = dlopen(Self.frameworkPath, RTLD_NOW),
              let mainSymbol = dlsym(handle, "SLSMainConnectionID"),
              let createSymbol = dlsym(handle, "SLSSpaceCreate"),
              let levelSymbol = dlsym(handle, "SLSSpaceSetAbsoluteLevel"),
              let showSymbol = dlsym(handle, "SLSShowSpaces"),
              let addSymbol = dlsym(handle, "SLSSpaceAddWindowsAndRemoveFromSpaces")
        else {
            isAvailable = false
            return
        }

        let mainConnection = unsafeBitCast(mainSymbol, to: MainConnectionID.self)
        let create = unsafeBitCast(createSymbol, to: SpaceCreate.self)
        let setLevel = unsafeBitCast(levelSymbol, to: SpaceSetAbsoluteLevel.self)
        let show = unsafeBitCast(showSymbol, to: ShowSpaces.self)

        connection = mainConnection()
        space = create(connection, 1, 0)
        _ = setLevel(connection, space, Self.aboveLockScreenLevel)
        _ = show(connection, [space] as CFArray)
        addWindows = unsafeBitCast(addSymbol, to: SpaceAddWindows.self)
        isAvailable = space != 0
    }

    func attach(_ window: NSWindow) -> Bool {
        guard isAvailable, let addWindows, window.windowNumber > 0 else { return false }
        _ = addWindows(connection, space, [window.windowNumber] as CFArray, 7)
        return true
    }
}
