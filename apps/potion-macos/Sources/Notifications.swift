import AppKit
import UserNotifications

/// "12 files converted" notifications when a batch finishes while Potion is in the background, with a Show
/// action that reveals the files in Finder (06-components.md, "Completion feedback").
enum Notifications {
    static let category = "batchFinished"
    static let showAction = "show"
    static let pathsKey = "paths"

    /// Called when the first batch starts, so the permission prompt appears in context, never at launch.
    static func requestPermissionIfNeeded(defaults: UserDefaults) {
        guard defaults.object(forKey: SettingsKey.notifications) as? Bool ?? true else { return }
        let center = UNUserNotificationCenter.current()
        center.getNotificationSettings { settings in
            guard settings.authorizationStatus == .notDetermined else { return }
            center.requestAuthorization(options: [.alert, .sound]) { _, _ in }
        }
    }

    static func registerCategory() {
        let show = UNNotificationAction(identifier: showAction, title: "Show", options: [.foreground])
        UNUserNotificationCenter.current().setNotificationCategories([
            UNNotificationCategory(identifier: category, actions: [show], intentIdentifiers: []),
        ])
    }

    static func post(message: String, outputs: [URL]) {
        let content = UNMutableNotificationContent()
        content.title = message
        content.sound = .default
        content.categoryIdentifier = category
        content.userInfo = [pathsKey: outputs.prefix(200).map(\.path)]
        UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
    }

    /// Clicking the notification (or Show) reveals the converted files.
    static func reveal(_ response: UNNotificationResponse) {
        let paths = response.notification.request.content.userInfo[pathsKey] as? [String] ?? []
        let urls = paths.map(URL.init(fileURLWithPath:)).filter { FileManager.default.fileExists(atPath: $0.path) }
        if !urls.isEmpty { NSWorkspace.shared.activateFileViewerSelecting(urls) }
    }
}
