import AppKit
import PotionCore
import UserNotifications

/// "3 downloads finished" notifications when a batch ends while Potion Dropper is in the background, with a Show
/// action that reveals the files in Finder (06-components.md, "Completion feedback").
@MainActor
enum Notifications {
    static let category = "downloadsFinished"
    static let showAction = "show"
    static let pathsKey = "paths"

    /// Called when the first download is queued, so the permission prompt appears in context, never at launch.
    static func requestPermissionIfNeeded() {
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

    static func batchFinished(done: [DropQueue.Item], failed: [DropQueue.Item]) {
        guard !NSApp.isActive else { return }
        let content = UNMutableNotificationContent()
        let finished = done.count == 1 ? "1 download finished" : "\(done.count) downloads finished"
        switch (done.isEmpty, failed.isEmpty) {
        case (false, true): content.title = finished
        case (false, false): content.title = "\(finished), \(failed.count) failed"
        default: content.title = failed.count == 1 ? "1 download failed" : "\(failed.count) downloads failed"
        }
        content.sound = .default
        content.categoryIdentifier = category
        let files = done.flatMap { item -> [URL] in if case .done(let urls) = item.status { urls } else { [] } }
        content.userInfo = [pathsKey: files.prefix(200).map(\.path)]
        UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
    }

    /// Clicking the notification (or Show) reveals the downloaded files.
    static func reveal(_ response: UNNotificationResponse) {
        let paths = response.notification.request.content.userInfo[pathsKey] as? [String] ?? []
        let urls = paths.map(URL.init(fileURLWithPath:)).filter { FileManager.default.fileExists(atPath: $0.path) }
        if !urls.isEmpty { NSWorkspace.shared.activateFileViewerSelecting(urls) }
    }
}
