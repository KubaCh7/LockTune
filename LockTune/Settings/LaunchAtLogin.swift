import ServiceManagement

/// Launch at login via SMAppService (macOS 13+). On macOS 12 the user adds
/// LockTune under System Preferences → Users & Groups → Login Items.
enum LaunchAtLogin {
    static var isSupported: Bool {
        if #available(macOS 13.0, *) { return true }
        return false
    }

    static var isEnabled: Bool {
        if #available(macOS 13.0, *) {
            return SMAppService.mainApp.status == .enabled
        }
        return false
    }

    static func set(_ enabled: Bool) throws {
        guard #available(macOS 13.0, *) else { throw CocoaError(.featureUnsupported) }
        if enabled {
            try SMAppService.mainApp.register()
        } else {
            try SMAppService.mainApp.unregister()
        }
    }
}
