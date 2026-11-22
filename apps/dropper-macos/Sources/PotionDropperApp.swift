import AppKit
import PotionCore
import SwiftUI

@main
struct PotionDropperApp: App {
    @NSApplicationDelegateAdaptor private var delegate: AppDelegate
    @StateObject private var model = DropperModel()

    var body: some Scene {
        // One window (a utility, not a document app).
        Window("Potion Dropper", id: "main") {
            MainWindow()
                .environmentObject(model)
                .frame(minWidth: PotionSize.windowMin.width, minHeight: PotionSize.windowMin.height)
        }
        .defaultSize(width: PotionSize.windowDefault.width, height: PotionSize.windowDefault.height)
        .windowToolbarStyle(.unifiedCompact)
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

final class AppDelegate: NSObject, NSApplicationDelegate {
    /// One window: closing it quits, like other single-window utilities.
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
}
