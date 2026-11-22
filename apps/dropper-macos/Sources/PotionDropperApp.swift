import AppKit
import PotionCore
import SwiftUI
import UserNotifications

@main
struct PotionDropperApp: App {
    @NSApplicationDelegateAdaptor private var delegate: AppDelegate

    var body: some Scene {
        // One window (a utility, not a document app).
        Window("Potion Dropper", id: "main") {
            MainWindow()
                .environmentObject(delegate.model)
                .environmentObject(delegate.model.queue)
                .frame(minWidth: PotionSize.windowMin.width, minHeight: PotionSize.windowMin.height)
        }
        .defaultSize(width: PotionSize.windowDefault.width, height: PotionSize.windowDefault.height)
        .windowToolbarStyle(.unifiedCompact)
        .commands { DropperCommands() }

        Window("Download History", id: "history") {
            HistoryView()
                .environmentObject(delegate.model)
        }
        .defaultSize(width: PotionSize.windowDefault.width, height: PotionSize.windowDefault.height)
    }
}

struct DropperCommands: Commands {
    @Environment(\.openWindow) private var openWindow

    var body: some Commands {
        CommandGroup(after: .sidebar) {
            Button("Download History") { openWindow(id: "history") }
                .keyboardShortcut("y", modifiers: .command)
        }
    }
}

/// The window content, behind the first-run acknowledgment until it's accepted.
struct MainWindow: View {
    @State private var acknowledging = !AcknowledgmentView.isAccepted

    var body: some View {
        ContentView()
            .sheet(isPresented: $acknowledging) {
                AcknowledgmentView { acknowledging = false }
            }
    }
}

/// Owns the model, so downloads keep running while the window is closed and reopened from the Dock.
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, UNUserNotificationCenterDelegate {
    let model = DropperModel()

    func applicationDidFinishLaunching(_ notification: Notification) {
        UNUserNotificationCenter.current().delegate = self
        Notifications.registerCategory()
    }

    /// One window: closing it quits, like other single-window utilities (with a question if downloads are running).
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        let remaining = model.queue.items.filter { $0.status == .waiting || $0.isRunning }.count
        guard remaining > 0 else { return .terminateNow }
        let alert = NSAlert()
        alert.messageText = remaining == 1 ? "Potion Dropper is still downloading 1 link." : "Potion Dropper is still downloading \(remaining) links."
        alert.informativeText = "If you quit now, downloads that haven't finished won't be saved."
        alert.addButton(withTitle: "Keep Downloading")
        alert.addButton(withTitle: "Quit")
        guard alert.runModal() == .alertSecondButtonReturn else { return .terminateCancel }
        model.queue.stopAll()
        return .terminateNow
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse,
                                            withCompletionHandler completionHandler: @escaping () -> Void) {
        Task { @MainActor in
            Notifications.reveal(response)
            completionHandler()
        }
    }
}
