import PotionCore
import SwiftUI

/// The main window (05-layout.md, "Potion Dropper: main window"): link bar, media card with output options,
/// and the current download. The queue and history arrive in M6.2.
struct ContentView: View {
    @EnvironmentObject private var model: DropperModel

    var body: some View {
        VStack(alignment: .leading, spacing: PotionSpacing.x4) {
            LinkBar()
            switch model.fetch {
            case .idle, .failed:
                EmptyView()
            case .fetching:
                MediaCardSkeleton()
            case .ready(let media):
                MediaCard(media: media)
                OutputOptions()
                DownloadBar()
            }
            Spacer(minLength: 0)
        }
        .padding(PotionSpacing.x6)
        .background(PotionColor.background)
        .animation(.easeInOut(duration: PotionMotion.base), value: model.fetch)
    }
}

// MARK: - Link bar

/// Sunken well with the link glyph, the field and Fetch inside its trailing edge (06-components.md, "Dropper: URL bar").
struct LinkBar: View {
    @EnvironmentObject private var model: DropperModel
    @FocusState private var focused: Bool
    @State private var offerPaste = false

    private var error: String? { if case .failed(let message) = model.fetch { message } else { nil } }

    var body: some View {
        VStack(alignment: .leading, spacing: PotionSpacing.x2) {
            HStack(spacing: PotionSpacing.x2) {
                Image(systemName: "link")
                    .foregroundStyle(PotionColor.textTertiary)
                    .accessibilityHidden(true)
                TextField("Paste a video link", text: $model.text)
                    .textFieldStyle(.plain)
                    .focused($focused)
                    .onSubmit { model.fetchLink() }
                    .accessibilityLabel("Link")
                if offerPaste && model.text.isEmpty {
                    Button("Paste Link") { model.pasteAndFetch() }
                        .controlSize(.small)
                }
                Button("Fetch") { model.fetchLink() }
                    .buttonStyle(.borderedProminent)
                    .controlSize(.small)
                    .keyboardShortcut(.defaultAction)
                    .disabled(model.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || model.fetch == .fetching)
            }
            .padding(.leading, PotionSpacing.x3)
            .padding(.trailing, PotionSpacing.x1)
            .frame(height: 36)
            .background(PotionColor.surfaceSunken, in: RoundedRectangle(cornerRadius: PotionRadius.md))
            .overlay(RoundedRectangle(cornerRadius: PotionRadius.md)
                .strokeBorder(error == nil ? PotionColor.borderStrong : PotionColor.error, lineWidth: 1))

            if let error {
                Label(error, systemImage: "exclamationmark.circle")
                    .font(PotionFont.secondary)
                    .foregroundStyle(PotionColor.error)
                    .accessibilityElement(children: .combine)
            }
        }
        .onChange(of: focused) { isFocused in offerPaste = isFocused && model.clipboardHasLink }
        .onAppear { focused = true }
    }
}

// MARK: - Media card

/// 06-components.md, "Dropper: media card". Direct links have no thumbnail, so the 16:9 well shows the category tile.
struct MediaCard: View {
    let media: DropperModel.Media

    private var category: PotionCategory { media.source.category == .audio ? .audio : .video }

    private var meta: String {
        let size = media.preview.size.map { ByteCountFormatter.string(fromByteCount: $0, countStyle: .file) } ?? "Size unknown"
        return "\(media.source.name) · \(size) · Direct link from \(media.preview.finalLink.host)"
    }

    var body: some View {
        HStack(alignment: .top, spacing: PotionSpacing.x3) {
            RoundedRectangle(cornerRadius: PotionRadius.sm)
                .fill(PotionColor.surfaceRaised)
                .frame(width: 96, height: 54)
                .overlay(PotionCategoryTile(category))
            VStack(alignment: .leading, spacing: PotionSpacing.x1) {
                Text(media.preview.fileName)
                    .font(PotionFont.fileName)
                    .lineLimit(2)
                    .truncationMode(.middle)
                    .help(media.preview.fileName)
                Text(meta)
                    .font(PotionFont.secondary)
                    .foregroundStyle(PotionColor.textSecondary)
                    .monospacedDigit()
            }
            Spacer(minLength: 0)
        }
        .accessibilityElement(children: .combine)
    }
}

/// Loading state: skeleton blocks in the card's shape (no shimmer, so nothing to turn off for reduce motion).
struct MediaCardSkeleton: View {
    var body: some View {
        HStack(alignment: .top, spacing: PotionSpacing.x3) {
            RoundedRectangle(cornerRadius: PotionRadius.sm).fill(PotionColor.surfaceRaised).frame(width: 96, height: 54)
            VStack(alignment: .leading, spacing: PotionSpacing.x2) {
                RoundedRectangle(cornerRadius: PotionRadius.xs).fill(PotionColor.surfaceRaised).frame(width: 240, height: 14)
                RoundedRectangle(cornerRadius: PotionRadius.xs).fill(PotionColor.surfaceRaised).frame(width: 160, height: 12)
            }
            Spacer(minLength: 0)
        }
        .accessibilityElement()
        .accessibilityLabel("Fetching info…")
    }
}

// MARK: - Output options

/// 06-components.md, "Dropper: output mode": Video shows Format + Quality, Audio shows Format + Bitrate.
struct OutputOptions: View {
    @EnvironmentObject private var model: DropperModel

    var body: some View {
        HStack(spacing: PotionSpacing.x4) {
            Picker("Output", selection: $model.mode) {
                ForEach(DropMode.allCases, id: \.self) { mode in
                    Label(mode == .video ? "Video" : "Audio",
                          systemImage: (mode == .video ? PotionCategory.video : PotionCategory.audio).sfSymbol)
                        .tag(mode)
                }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .fixedSize()
            .disabled(model.modes.count < 2 || model.isBusy)
            .accessibilityLabel("Video or audio")

            Picker("Format", selection: $model.formatID) {
                ForEach(model.formats, id: \.id) { format in
                    Text(format.id == model.media?.source.id ? "\(format.name) (as downloaded)" : format.name).tag(Optional(format.id))
                }
            }
            .fixedSize()
            .disabled(model.isBusy)

            if model.mode == .video {
                Picker("Quality", selection: $model.videoPresetID) {
                    ForEach(VideoPresets.shared.presets) { preset in Text(preset.name).tag(preset.id) }
                }
                .fixedSize()
                .disabled(model.keepsOriginal || model.isBusy)
            } else {
                Picker("Bitrate", selection: $model.audioPresetID) {
                    ForEach(AudioPresets.shared.presets) { preset in Text("\(preset.name) (\(preset.bitrateKbps) kbps)").tag(preset.id) }
                }
                .fixedSize()
                .disabled(model.keepsOriginal || model.isBusy)
            }
            Spacer(minLength: 0)
        }
    }
}

// MARK: - Download

struct DownloadBar: View {
    @EnvironmentObject private var model: DropperModel

    var body: some View {
        VStack(alignment: .leading, spacing: PotionSpacing.x2) {
            HStack(spacing: PotionSpacing.x3) {
                Text("Saves to \(FileManager.default.displayName(atPath: model.outputFolder.path))")
                    .font(PotionFont.secondary)
                    .foregroundStyle(PotionColor.textSecondary)
                Spacer()
                if model.isBusy {
                    Button("Stop") { model.stop() }
                        .keyboardShortcut(.cancelAction)
                } else {
                    Button("Download") { model.download() }
                        .buttonStyle(.borderedProminent)
                        .keyboardShortcut(.return, modifiers: .command)
                        .disabled(model.formatID == nil)
                }
            }

            switch model.job {
            case .idle:
                EmptyView()
            case .running(let status, let fraction, let detail):
                VStack(alignment: .leading, spacing: PotionSpacing.x1) {
                    if let fraction {
                        ProgressView(value: fraction) { Text(status).font(PotionFont.secondary) }
                    } else {
                        ProgressView { Text(status).font(PotionFont.secondary) }.progressViewStyle(.linear)
                    }
                    if let detail {
                        Text(detail).font(PotionFont.caption).foregroundStyle(PotionColor.textSecondary).monospacedDigit()
                    }
                }
            case .done(let files):
                HStack(spacing: PotionSpacing.x2) {
                    Label(files.count == 1 ? "Saved “\(files[0].lastPathComponent)”" : "Saved \(files.count) files", systemImage: "checkmark.circle")
                        .foregroundStyle(PotionColor.success)
                        .lineLimit(1)
                        .truncationMode(.middle)
                    Button("Show in Finder") { model.showInFinder(files) }
                        .buttonStyle(.link)
                }
                .font(PotionFont.secondary)
            case .failed(let message):
                Label(message, systemImage: "exclamationmark.circle")
                    .font(PotionFont.secondary)
                    .foregroundStyle(PotionColor.error)
            }
        }
        .padding(.top, PotionSpacing.x2)
    }
}
