import SwiftUI

@main
struct PotionApp: App {
    var body: some Scene {
        WindowGroup("Potion") {
            ContentView()
                .frame(minWidth: PotionSize.windowMin.width, minHeight: PotionSize.windowMin.height)
        }
        .defaultSize(width: PotionSize.windowDefault.width, height: PotionSize.windowDefault.height)
        .windowToolbarStyle(.unifiedCompact)
    }
}
