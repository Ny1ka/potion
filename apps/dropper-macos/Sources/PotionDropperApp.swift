import AppKit
import PotionCore
import SwiftUI

@main
struct PotionDropperApp: App {
    @NSApplicationDelegateAdaptor private var delegate: AppDelegate

    var body: some Scene {
        // One window (a utility, not a document app).
        Window("Potion Dropper", id: "main") {
            ContentView()
                .frame(minWidth: PotionSize.windowMin.width, minHeight: PotionSize.windowMin.height)
        }
        .defaultSize(width: PotionSize.windowDefault.width, height: PotionSize.windowDefault.height)
        .windowToolbarStyle(.unifiedCompact)
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    /// One window: closing it quits, like other single-window utilities.
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
}

/// The link bar (05-layout.md, "Potion Dropper: main window"). Fetch checks the links against the source allowlist;
/// media info, the output options and the download queue arrive with the M6 interface.
struct ContentView: View {
    @State private var text = ""
    @State private var check: LinkCheck?

    var body: some View {
        VStack(alignment: .leading, spacing: PotionSpacing.x3) {
            HStack(spacing: PotionSpacing.x2) {
                HStack(spacing: PotionSpacing.x2) {
                    Image(systemName: "link")
                        .foregroundStyle(PotionColor.textTertiary)
                        .accessibilityHidden(true)
                    TextField("Paste a video link", text: $text)
                        .textFieldStyle(.plain)
                        .onSubmit(fetch)
                }
                .padding(.horizontal, PotionSpacing.x3)
                .padding(.vertical, PotionSpacing.x2)
                .background(PotionColor.surfaceSunken, in: RoundedRectangle(cornerRadius: PotionRadius.md))
                .overlay(RoundedRectangle(cornerRadius: PotionRadius.md).strokeBorder(PotionColor.border))

                Button("Paste", action: paste)
                Button("Fetch", action: fetch)
                    .keyboardShortcut(.defaultAction)
                    .disabled(text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }

            if let check {
                Label(check.text, systemImage: check.ok ? "checkmark.circle" : "exclamationmark.circle")
                    .font(PotionFont.secondary)
                    .foregroundStyle(check.ok ? PotionColor.textSecondary : PotionColor.error)
                    .accessibilityElement(children: .combine)
            }
            Spacer(minLength: 0)
        }
        .padding(PotionSpacing.x6)
        .background(PotionColor.background)
    }

    private func paste() {
        guard let string = NSPasteboard.general.string(forType: .string) else { return }
        text = string
        fetch()
    }

    private func fetch() {
        check = LinkCheck(text: text)
    }
}

/// What the link bar says about its text: the first usable link, or why there's none.
struct LinkCheck {
    let ok: Bool
    let text: String

    init(text input: String, registry: SourceRegistry = .shared) {
        let list = registry.links(in: input)
        if let first = list.links.first {
            ok = true
            let format = first.formatID.flatMap { FormatRegistry.shared.format(id: $0)?.name }
            let what = format.map { "\($0) from \(first.host)" } ?? "Link to \(first.host)"
            text = list.links.count == 1 ? "\(what)." : "\(what), and \(list.links.count - 1) more."
        } else {
            ok = false
            switch registry.link(from: input) {
            case .failure(let error): text = registry.message(for: error)
            case .success: text = registry.message(for: list.problems.first?.error ?? .notALink)
            }
        }
    }
}
