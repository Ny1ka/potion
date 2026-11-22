import AppKit
import SwiftUI

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
                Button("Add Files…") { delegate.queue.showImporter = true }
                    .keyboardShortcut("o")
            }
        }
    }
}

/// Owns the queue so files opened with Potion (dropped on the Dock icon, Open With, `open -a Potion`) land in it.
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    let queue = QueueModel()

    func application(_ application: NSApplication, open urls: [URL]) {
        queue.add(urls.filter(\.isFileURL))
    }
}
