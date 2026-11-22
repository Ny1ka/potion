import SwiftUI

/// The first-run acknowledgment (spec Part 6.3.5), shown before anything can be downloaded. The text is the spec's,
/// word for word. Accepting is remembered; the version lets a changed text ask again.
struct AcknowledgmentView: View {
    static let version = 1
    static let defaultsKey = "acknowledgedSourcePolicyVersion"

    static var isAccepted: Bool { UserDefaults.standard.integer(forKey: defaultsKey) >= version }

    #if DEBUG
    /// Personal builds with external sources ask again, with the extra line (spec Part 6.6.11).
    static let personalKey = "acknowledgedPersonalSourcesVersion"
    static func isAccepted(personal: Bool) -> Bool {
        isAccepted && (!personal || UserDefaults.standard.integer(forKey: personalKey) >= version)
    }
    #endif

    var personal = false
    let onAccept: () -> Void
    @State private var understood = false

    var body: some View {
        VStack(alignment: .leading, spacing: PotionSpacing.x4) {
            Text("Before You Download")
                .font(PotionFont.title)
            Text("Only download media you own, media licensed for download (such as Creative Commons or public domain), or media the website's terms allow you to download. Many sites prohibit downloading. You're responsible for what you download and how you use it.")
                .fixedSize(horizontal: false, vertical: true)
            #if DEBUG
            if personal {
                Text("Experimental, personal build. Downloading from this site may violate its Terms of Service.")
                    .fontWeight(.semibold)
                    .fixedSize(horizontal: false, vertical: true)
            }
            #endif
            Toggle("I understand", isOn: $understood)
                .toggleStyle(.checkbox)
            AgreementLine()
            HStack {
                Spacer()
                Button("Quit") { NSApp.terminate(nil) }
                    .keyboardShortcut(.cancelAction)
                Button("Continue") {
                    UserDefaults.standard.set(Self.version, forKey: Self.defaultsKey)
                    #if DEBUG
                    if personal { UserDefaults.standard.set(Self.version, forKey: Self.personalKey) }
                    #endif
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
