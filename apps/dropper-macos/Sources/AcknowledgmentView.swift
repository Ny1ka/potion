import SwiftUI

/// The first-run acknowledgment (spec Part 6.3.5), shown before anything can be downloaded. The text is the spec's,
/// word for word. Accepting is remembered; the version lets a changed text ask again.
struct AcknowledgmentView: View {
    static let version = 1
    static let defaultsKey = "acknowledgedSourcePolicyVersion"

    static var isAccepted: Bool { UserDefaults.standard.integer(forKey: defaultsKey) >= version }

    let onAccept: () -> Void
    @State private var understood = false

    var body: some View {
        VStack(alignment: .leading, spacing: PotionSpacing.x4) {
            Text("Before You Download")
                .font(PotionFont.title)
            Text("Only download media you own, media licensed for download (such as Creative Commons or public domain), or media the website's terms allow you to download. Many sites prohibit downloading. You're responsible for what you download and how you use it.")
                .fixedSize(horizontal: false, vertical: true)
            Toggle("I understand", isOn: $understood)
                .toggleStyle(.checkbox)
            HStack {
                Spacer()
                Button("Quit") { NSApp.terminate(nil) }
                    .keyboardShortcut(.cancelAction)
                Button("Continue") {
                    UserDefaults.standard.set(Self.version, forKey: Self.defaultsKey)
                    onAccept()
                }
                .keyboardShortcut(.defaultAction)
                .disabled(!understood)
            }
        }
        .padding(PotionSpacing.x6)
        .frame(width: PotionSize.convertSheetWidth)
        .interactiveDismissDisabled()
    }
}
