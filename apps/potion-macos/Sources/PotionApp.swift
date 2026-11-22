import AppKit
import SwiftUI
import UserNotifications

@main
struct PotionApp: App {
    @NSApplicationDelegateAdaptor private var delegate: AppDelegate

    var body: some Scene {
        // One window with one queue (a utility, not a document app).
        Window("Potion", id: "main") {
            ContentView()
                .environmentObject(delegate.queue)
                .frame(minWidth: PotionSize.windowMin.width, minHeight: PotionSize.windowMin.height)
        }
        .defaultSize(width: PotionSize.windowDefault.width, height: PotionSize.windowDefault.height)
        .windowToolbarStyle(.unifiedCompact)
        .commands {
            CommandGroup(replacing: .newItem) {
                QueueCommands(queue: delegate.queue)
            }
        }

        Settings {
            SettingsView()
                .environmentObject(delegate.queue)
        }
    }
}

/// Owns the queue so files opened with Potion (dropped on the Dock icon, Open With, `open -a Potion`) land in it.
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, UNUserNotificationCenterDelegate {
    let queue = QueueModel()

    func applicationDidFinishLaunching(_ notification: Notification) {
        UNUserNotificationCenter.current().delegate = self
        Notifications.registerCategory()
    }

    func application(_ application: NSApplication, open urls: [URL]) {
        queue.add(urls.filter(\.isFileURL))
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse,
                                            withCompletionHandler completionHandler: @escaping () -> Void) {
        Task { @MainActor in
            Notifications.reveal(response)
            completionHandler()
        }
    }
}

/// File menu items that follow the queue's state.
struct QueueCommands: View {
    @ObservedObject var queue: QueueModel

    var body: some View {
        Button("Add Files…") { queue.showImporter = true }
            .keyboardShortcut("o")
        Button("Options…") { queue.showOptions(for: queue.selection) }
            .keyboardShortcut("i")
            .disabled(queue.selection.isEmpty)
        Divider()
        Button("Convert All") { queue.convertAll() }
            .keyboardShortcut(.return)
            .disabled(!queue.canConvert)
        Button("Stop") { queue.stop() }
            .keyboardShortcut(".")
            .disabled(!queue.isConverting)
        Divider()
        Button("Show in Finder") { queue.reveal(queue.selection) }
            .keyboardShortcut("r", modifiers: [.command, .shift])
            .disabled(queue.selection.isEmpty)
    }
}
