import AppKit
import SwiftUI

// About window and legal documents, shared by Potion and Potion Dropper for macOS (spec Part 6.4). The documents are
// bundled from legal/ (referenced in place by each app's project.yml) and shown in the app, never fetched.

/// The bundled legal documents.
enum LegalDocument: String, Codable, Hashable, CaseIterable, Identifiable {
    case terms, privacy, acknowledgements, license

    var id: String { rawValue }

    var title: String {
        switch self {
        case .terms: "Terms of Use"
        case .privacy: "Privacy Policy"
        case .acknowledgements: "Acknowledgements"
        case .license: "License"
        }
    }

    /// Resource name in the app bundle.
    private var resource: (name: String, ext: String?) {
        switch self {
        case .terms: ("TERMS", "md")
        case .privacy: ("PRIVACY", "md")
        case .acknowledgements: ("THIRD_PARTY_NOTICES", "md")
        case .license: ("LICENSE", nil)
        }
    }

    var text: String {
        guard let url = Bundle.main.url(forResource: resource.name, withExtension: resource.ext),
              let text = try? String(contentsOf: url, encoding: .utf8) else {
            return "This document is missing from the app. It's also at https://github.com/Ny1ka/potion/tree/main/legal"
        }
        return text
    }
}

/// What the About window shows for each app.
struct AboutInfo {
    let productName: String
    /// The privacy line (spec Part 6.4).
    let privacyLine: String
    /// Extra product-specific notice (Potion's onboarding note, spec Part 6.2).
    var note: String?

    var version: String {
        let short = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "?"
        let build = Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "?"
        return "Version \(short) (\(build))"
    }

    var copyright: String {
        Bundle.main.object(forInfoDictionaryKey: "NSHumanReadableCopyright") as? String ?? ""
    }
}

/// About window: icon, name, version, copyright, privacy line, the legal documents, FFmpeg/LGPL attribution, source
/// code and contact (spec Part 6.4).
struct AboutView: View {
    let info: AboutInfo
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        VStack(spacing: PotionSpacing.x4) {
            // 06-components.md, "About window": mark at 96, product name in brandDisplay at 28, version in caption.
            Image("LogoMark")
                .resizable()
                .aspectRatio(contentMode: .fit)
                .frame(width: 96, height: 96)
                .accessibilityHidden(true)
            VStack(spacing: PotionSpacing.x1) {
                Text(info.productName)
                    .font(PotionFont.brand(28))
                Text(info.version)
                    .font(PotionFont.caption)
                    .foregroundStyle(PotionColor.textSecondary)
                    .textSelection(.enabled)
                Text(info.copyright)
                    .font(PotionFont.caption)
                    .foregroundStyle(PotionColor.textSecondary)
            }
            Text(info.privacyLine)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
            if let note = info.note {
                Text(note)
                    .font(PotionFont.secondary)
                    .foregroundStyle(PotionColor.textSecondary)
                    .multilineTextAlignment(.center)
                    .fixedSize(horizontal: false, vertical: true)
            }
            HStack(spacing: PotionSpacing.x2) {
                ForEach([LegalDocument.terms, .privacy, .acknowledgements]) { document in
                    Button(document.title) { openWindow(value: document) }
                }
            }
            VStack(spacing: PotionSpacing.x1) {
                Text("This app uses FFmpeg, licensed under the LGPL 2.1 or later, run as separate programs. ")
                    + Text("[Get the FFmpeg source and build scripts](https://github.com/Ny1ka/potion/tree/main/scripts/ffmpeg)")
                Text("[Source code (MIT License)](https://github.com/Ny1ka/potion) · [nyikabusiness@gmail.com](mailto:nyikabusiness@gmail.com)")
            }
            .font(PotionFont.caption)
            .foregroundStyle(PotionColor.textSecondary)
            .multilineTextAlignment(.center)
            .fixedSize(horizontal: false, vertical: true)
        }
        .padding(PotionSpacing.x6)
        .frame(width: PotionSize.convertSheetWidth)
    }
}

/// A legal document in a window: Markdown documents show their text as written (plain text keeps every license
/// text exact), in a scrollable, selectable view that handles the 1 MB of acknowledgements.
struct LegalDocumentView: View {
    let document: LegalDocument

    var body: some View {
        PlainTextView(text: document.text, monospaced: document == .acknowledgements || document == .license)
            .frame(minWidth: PotionSize.windowMin.width, minHeight: PotionSize.windowMin.height)
            .navigationTitle(document.title)
    }
}

/// NSTextView for large read-only text (SwiftUI's Text is too slow for the acknowledgements).
private struct PlainTextView: NSViewRepresentable {
    let text: String
    let monospaced: Bool

    func makeNSView(context: Context) -> NSScrollView {
        let scroll = NSTextView.scrollableTextView()
        if let view = scroll.documentView as? NSTextView {
            view.isEditable = false
            view.isSelectable = true
            view.textContainerInset = NSSize(width: PotionSpacing.x4, height: PotionSpacing.x4)
            view.font = monospaced ? .monospacedSystemFont(ofSize: NSFont.smallSystemFontSize, weight: .regular)
                                   : .systemFont(ofSize: NSFont.systemFontSize)
            view.textColor = .labelColor
            view.string = text
        }
        return scroll
    }

    func updateNSView(_ view: NSScrollView, context: Context) {}
}

/// App-menu About item and Help-menu links to the legal documents.
struct LegalCommands: Commands {
    let productName: String
    @Environment(\.openWindow) private var openWindow

    var body: some Commands {
        CommandGroup(replacing: .appInfo) {
            Button("About \(productName)") { openWindow(id: "about") }
        }
        CommandGroup(replacing: .help) {
            ForEach([LegalDocument.terms, .privacy, .acknowledgements, .license]) { document in
                Button(document.title) { openWindow(value: document) }
            }
        }
    }
}

/// "By continuing, you agree to the Terms of Use and Privacy Policy." with both opening in the app.
struct AgreementLine: View {
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        HStack(spacing: PotionSpacing.x1) {
            Text("By continuing, you agree to the")
            Button("Terms of Use") { openWindow(value: LegalDocument.terms) }.buttonStyle(.link)
            Text("and")
            Button("Privacy Policy") { openWindow(value: LegalDocument.privacy) }.buttonStyle(.link)
        }
        .font(PotionFont.caption)
        .foregroundStyle(PotionColor.textSecondary)
    }
}
