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
            LegalCommands(productName: "Potion")
        }

        Window("About Potion", id: "about") {
            AboutView(info: .potion)
        }
        .windowResizability(.contentSize)

        WindowGroup(for: LegalDocument.self) { $document in
            if let document { LegalDocumentView(document: document) }
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
        NSApp.servicesProvider = self
        NSUpdateDynamicServices()
    }

    func application(_ application: NSApplication, open urls: [URL]) {
        queue.add(urls.filter(\.isFileURL))
    }

    /// One window: closing it quits, like other single-window utilities. The queue isn't a document.
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }

    /// Asks before quitting in the middle of a batch.
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard queue.isConverting else { return .terminateNow }
        let remaining = queue.batch.total - queue.batch.finished
        let alert = NSAlert()
        alert.messageText = remaining == 1 ? "Potion is still converting 1 file." : "Potion is still converting \(remaining) files."
        alert.informativeText = "If you quit now, files that haven't finished won't be saved."
        alert.addButton(withTitle: "Keep Converting")
        alert.addButton(withTitle: "Quit")
        guard alert.runModal() == .alertSecondButtonReturn else { return .terminateCancel }
        queue.stop()
        return .terminateNow
    }

    /// Finder › Services › Convert with Potion (Info.plist NSServices). Adds the files and opens the convert sheet.
    @objc func convertWithPotion(_ pasteboard: NSPasteboard, userData: String?, error: AutoreleasingUnsafeMutablePointer<NSString?>) {
        let urls = pasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL] ?? []
        guard !urls.isEmpty else { error.pointee = "No files were selected." as NSString; return }
        NSApp.activate(ignoringOtherApps: true)
        queue.add(urls, openOptions: true)
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

extension AboutInfo {
    /// Spec Part 6.2: the one-time onboarding note, also in About.
    static let potionNote = "Potion converts files on this computer. Nothing is uploaded. Converting a file doesn't change who owns it: only convert files you have the right to use. Potion doesn't remove copy protection."
    static let potion = AboutInfo(productName: "Potion", privacyLine: potionNote)
}
