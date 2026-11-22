import PotionCore
import SwiftUI

/// Which Advanced controls a conversion gets (06-components.md, "Format picker (Advanced options)").
enum OptionsKind: Equatable {
    case video(container: String)
    case gif
    case audio(format: String, lossless: Bool)
    case image(lossy: Bool)
    case imagesToPDF
    case pdfToImage(lossy: Bool)
    case pdfPages
    case none

    static func of(_ operation: ConversionOperation, output: FileFormat) -> OptionsKind {
        switch operation {
        case .transcodeVideo, .gifToVideo: return .video(container: output.id)
        case .videoToGif: return .gif
        case .transcodeAudio, .extractAudio:
            return .audio(format: output.id, lossless: AudioPresets.shared.formats[output.id]?.isLossless ?? false)
        case .convertImage: return .image(lossy: ImagePresets.shared.lossy.contains(output.id))
        case .imageToPdf: return .imagesToPDF
        case .pdfToImage: return .pdfToImage(lossy: ImagePresets.shared.lossy.contains(output.id))
        case .pdfToDocument: return .pdfPages
        case .convertDocument, .documentToPdf: return .none
        }
    }

    var usesPages: Bool {
        switch self {
        case .pdfToImage, .pdfPages: return true
        default: return false
        }
    }
}

/// The Advanced controls. Every choice comes from core/spec/presets.json.
struct AdvancedOptionsView: View {
    let kind: OptionsKind
    @Binding var settings: ConversionSettings
    /// Page selection text for PDF input ("1-3, 5"); nil when several PDFs are being set at once.
    @Binding var pagesText: String
    let pageCount: Int?

    private let video = VideoPresets.shared
    private let audio = AudioPresets.shared
    private let images = ImagePresets.shared
    private let pdf = PDFPresets.shared

    var body: some View {
        Form {
            switch kind {
            case let .video(container): videoControls(container: container)
            case .gif: gifControls
            case let .audio(format, lossless): audioControls(format: format, lossless: lossless)
            case let .image(lossy): imageControls(lossy: lossy)
            case .imagesToPDF: sizeControls
            case let .pdfToImage(lossy): pdfToImageControls(lossy: lossy)
            case .pdfPages: pagesField
            case .none: EmptyView()
            }
        }
        .formStyle(.columns)
    }

    // MARK: Video

    @ViewBuilder private func videoControls(container: String) -> some View {
        Picker("Quality:", selection: videoQuality) {
            ForEach(video.presets) { Text($0.name).tag($0.id) }
            Divider()
            Text("Custom Bitrate").tag(Self.custom)
        }
        if settings.video.bitrateKbps != nil {
            LabeledContent("Bitrate:") {
                HStack(spacing: PotionSpacing.x1) {
                    TextField("Bitrate", value: videoBitrate, format: .number)
                        .labelsHidden()
                        .frame(width: 80)
                        .multilineTextAlignment(.trailing)
                    Text("kbps").foregroundStyle(PotionColor.textSecondary)
                }
            }
        }
        Picker("Resolution:", selection: $settings.video.maxHeight) {
            Text("Original").tag(Int?.none)
            ForEach(video.resolutions, id: \.self) { Text(Self.resolutionName($0)).tag(Int?.some($0)) }
        }
        Picker("Frame rate:", selection: $settings.video.frameRate) {
            Text("Original").tag(Double?.none)
            ForEach(video.frameRates, id: \.self) { Text("\($0.formatted()) fps").tag(Double?.some($0)) }
        }
        if let codecs = video.formats[container]?.videoCodecs, codecs.count > 1 {
            Picker("Codec:", selection: $settings.video.codec) {
                ForEach(Array(codecs.enumerated()), id: \.element) { index, id in
                    // The container's first codec is the default (nil).
                    Text(video.codecs[id]?.name ?? id.uppercased()).tag(index == 0 ? String?.none : String?.some(id))
                }
            }
        }
        Picker("Aspect ratio:", selection: $settings.video.aspectRatio) {
            Text("Original").tag(String?.none)
            ForEach(video.aspectRatios, id: \.self) { Text($0).tag(String?.some($0)) }
        }
    }

    private static let custom = "custom"

    private var videoQuality: Binding<String> {
        Binding(
            get: { settings.video.bitrateKbps != nil ? Self.custom : (settings.video.presetID ?? video.defaultPresetID) },
            set: { choice in
                if choice == Self.custom {
                    settings.video.bitrateKbps = settings.video.bitrateKbps ?? 5_000
                } else {
                    settings.video.presetID = choice
                    settings.video.bitrateKbps = nil
                }
            })
    }

    private var videoBitrate: Binding<Int> {
        Binding(
            get: { settings.video.bitrateKbps ?? 5_000 },
            set: { settings.video.bitrateKbps = min(max($0, video.minBitrateKbps), video.maxBitrateKbps) })
    }

    static func resolutionName(_ height: Int) -> String {
        height == 2160 ? "4K (2160p)" : "\(height)p"
    }

    // MARK: GIF

    @ViewBuilder private var gifControls: some View {
        Picker("Quality:", selection: Binding(get: { settings.video.presetID ?? video.defaultPresetID },
                                              set: { settings.video.presetID = $0 })) {
            ForEach(video.presets.filter { video.gif[$0.id] != nil }) { preset in
                if let gif = video.gif[preset.id] {
                    Text("\(preset.name) (\(gif.fps.formatted()) fps, up to \(gif.maxWidth) px wide)").tag(preset.id)
                }
            }
        }
    }

    // MARK: Audio

    @ViewBuilder private func audioControls(format: String, lossless: Bool) -> some View {
        let settingsForFormat = audio.formats[format]
        if !lossless {
            Picker("Quality:", selection: audioQuality) {
                ForEach(audio.presets) { Text("\($0.name) (\($0.bitrateKbps) kbps)").tag($0.id) }
                if let bitrates = settingsForFormat?.bitrates, !bitrates.isEmpty {
                    Divider()
                    Text("Custom Bitrate").tag(Self.custom)
                }
            }
            if settings.audio.bitrateKbps != nil, let bitrates = settingsForFormat?.bitrates {
                Picker("Bitrate:", selection: Binding(get: { settings.audio.bitrateKbps ?? bitrates[0] },
                                                      set: { settings.audio.bitrateKbps = $0 })) {
                    ForEach(bitrates, id: \.self) { Text("\($0) kbps").tag($0) }
                }
            }
        }
        Picker("Sample rate:", selection: $settings.audio.sampleRate) {
            Text("Original").tag(Int?.none)
            ForEach(settingsForFormat?.sampleRates ?? audio.sampleRates, id: \.self) { rate in
                Text("\((Double(rate) / 1000).formatted()) kHz").tag(Int?.some(rate))
            }
        }
    }

    private var audioQuality: Binding<String> {
        Binding(
            get: { settings.audio.bitrateKbps != nil ? Self.custom : (settings.audio.presetID ?? audio.defaultPresetID) },
            set: { choice in
                if choice == Self.custom {
                    settings.audio.bitrateKbps = audio.preset(id: settings.audio.presetID).bitrateKbps
                } else {
                    settings.audio.presetID = choice
                    settings.audio.bitrateKbps = nil
                }
            })
    }

    // MARK: Images

    @ViewBuilder private func imageControls(lossy: Bool) -> some View {
        sizeControls
        if lossy {
            LabeledContent("Quality:") {
                HStack {
                    Slider(value: imageQuality, in: 0.1...1)
                    Text(imageQuality.wrappedValue.formatted(.percent.precision(.fractionLength(0))))
                        .monospacedDigit()
                        .foregroundStyle(PotionColor.textSecondary)
                        .frame(width: 44, alignment: .trailing)
                }
            }
        }
        LabeledContent("Rotate:") {
            HStack(spacing: PotionSpacing.x2) {
                Button { rotate(by: 270) } label: { Image(systemName: "rotate.left") }
                    .help("Rotate Left").accessibilityLabel("Rotate Left")
                Button { rotate(by: 90) } label: { Image(systemName: "rotate.right") }
                    .help("Rotate Right").accessibilityLabel("Rotate Right")
                Text(settings.image.rotation == 0 ? "Not rotated" : "\(settings.image.rotation)°")
                    .foregroundStyle(PotionColor.textSecondary)
                    .monospacedDigit()
            }
        }
        LabeledContent("Flip:") {
            HStack(spacing: PotionSpacing.x3) {
                Toggle("Horizontal", isOn: $settings.image.flipHorizontal)
                Toggle("Vertical", isOn: $settings.image.flipVertical)
            }
            .toggleStyle(.checkbox)
        }
    }

    private func rotate(by degrees: Int) {
        settings.image.rotation = (settings.image.rotation + degrees) % 360
    }

    private var imageQuality: Binding<Double> {
        Binding(get: { settings.image.quality ?? images.quality(for: settings.image) },
                set: { settings.image.quality = ($0 * 100).rounded() / 100 })
    }

    enum SizeMode: Hashable { case original, percent(Double), fit, exact }

    @ViewBuilder private var sizeControls: some View {
        Picker("Size:", selection: sizeMode) {
            Text("Original").tag(SizeMode.original)
            ForEach([75.0, 50.0, 25.0], id: \.self) { Text("\(Int($0))%").tag(SizeMode.percent($0)) }
            Text("Fit Within…").tag(SizeMode.fit)
            Text("Exact Size…").tag(SizeMode.exact)
        }
        switch settings.image.resize {
        case let .fit(width, height):
            dimensionFields(width: width, height: height) { settings.image.resize = .fit(width: $0, height: $1) }
        case let .exact(width, height):
            dimensionFields(width: width, height: height) { settings.image.resize = .exact(width: $0 ?? width, height: $1 ?? height) }
        default:
            EmptyView()
        }
    }

    private func dimensionFields(width: Int?, height: Int?, set: @escaping (Int?, Int?) -> Void) -> some View {
        LabeledContent("Pixels:") {
            HStack(spacing: PotionSpacing.x1) {
                TextField("Width", value: Binding(get: { width }, set: { set($0.map { max($0, 1) }, height) }), format: .number,
                          prompt: Text("Any"))
                    .labelsHidden()
                    .frame(width: 64)
                    .accessibilityLabel("Width in pixels")
                Text("×").foregroundStyle(PotionColor.textTertiary)
                TextField("Height", value: Binding(get: { height }, set: { set(width, $0.map { max($0, 1) }) }), format: .number,
                          prompt: Text("Any"))
                    .labelsHidden()
                    .frame(width: 64)
                    .accessibilityLabel("Height in pixels")
            }
            .multilineTextAlignment(.trailing)
        }
    }

    private var sizeMode: Binding<SizeMode> {
        Binding(
            get: {
                switch settings.image.resize {
                case .original: return .original
                case let .percent(p): return .percent(p)
                case .fit: return .fit
                case .exact: return .exact
                }
            },
            set: { mode in
                switch mode {
                case .original: settings.image.resize = .original
                case let .percent(p): settings.image.resize = .percent(p)
                case .fit: settings.image.resize = .fit(width: 1920, height: 1920)
                case .exact: settings.image.resize = .exact(width: 1920, height: 1080)
                }
            })
    }

    // MARK: PDF

    @ViewBuilder private func pdfToImageControls(lossy: Bool) -> some View {
        pagesField
        Picker("Resolution:", selection: Binding(get: { settings.pdf.dpi ?? pdf.defaultDPI }, set: { settings.pdf.dpi = $0 })) {
            ForEach(pdf.dpiChoices, id: \.self) { Text("\(Int($0)) dpi").tag($0) }
        }
        if lossy {
            Picker("Quality:", selection: Binding(get: { settings.pdf.imagePresetID ?? images.defaultPresetID },
                                                  set: { settings.pdf.imagePresetID = $0 })) {
                ForEach(images.presets) { Text($0.name).tag($0.id) }
            }
        }
    }

    @ViewBuilder private var pagesField: some View {
        if let pageCount {
            LabeledContent("Pages:") {
                VStack(alignment: .leading, spacing: PotionSpacing.xHalf) {
                    TextField("Pages", text: $pagesText, prompt: Text("All pages"))
                        .labelsHidden()
                        .frame(width: 160)
                        .accessibilityLabel("Pages")
                    Text(pagesError ?? (pageCount == 1 ? "This PDF has 1 page" : "1–\(pageCount), for example “1–3, 5”"))
                        .font(PotionFont.caption)
                        .foregroundStyle(pagesError == nil ? PotionColor.textSecondary : PotionColor.error)
                }
            }
        }
    }

    private var pagesError: String? {
        guard let pageCount, (try? PageSelection.pages(pagesText, pageCount: pageCount)) == nil else { return nil }
        return pageCount == 1 ? "This PDF has only 1 page" : "Use pages 1–\(pageCount), for example “1–3, 5”"
    }
}

/// One plain line of what will happen, above the controls ("MP4 · HEVC (H.265) · 1080p · 30 fps").
enum OptionsSummary {
    static func line(kind: OptionsKind, output: FileFormat, settings: ConversionSettings, pageCount: Int?, pagesText: String) -> String {
        var parts = [output.name]
        switch kind {
        case let .video(container):
            let codecs = VideoPresets.shared.formats[container]?.videoCodecs ?? []
            if let codec = settings.video.codec ?? codecs.first, let name = VideoPresets.shared.codecs[codec]?.name { parts.append(name) }
            parts.append(settings.video.maxHeight.map(AdvancedOptionsView.resolutionName) ?? "original size")
            if let fps = settings.video.frameRate { parts.append("\(fps.formatted()) fps") }
            if let kbps = settings.video.bitrateKbps { parts.append("\(kbps.formatted()) kbps") }
            else { parts.append(VideoPresets.shared.preset(id: settings.video.presetID).name.lowercased()) }
        case .gif:
            parts.append(VideoPresets.shared.preset(id: settings.video.presetID).name.lowercased())
        case let .audio(_, lossless):
            if lossless { parts.append("lossless") }
            else { parts.append("\(settings.audio.bitrateKbps ?? AudioPresets.shared.preset(id: settings.audio.presetID).bitrateKbps) kbps") }
            if let rate = settings.audio.sampleRate { parts.append("\((Double(rate) / 1000).formatted()) kHz") }
        case .image, .imagesToPDF:
            switch settings.image.resize {
            case .original: parts.append("original size")
            case let .percent(p): parts.append("\(Int(p))% size")
            case let .fit(w, h): parts.append("fit within \(w.map(String.init) ?? "any") × \(h.map(String.init) ?? "any")")
            case let .exact(w, h): parts.append("\(w) × \(h)")
            }
            if settings.image.rotation != 0 { parts.append("rotated \(settings.image.rotation)°") }
        case .pdfToImage:
            parts.append("\(Int(settings.pdf.dpi ?? PDFPresets.shared.defaultDPI)) dpi")
            parts.append(pagesPhrase(pagesText))
        case .pdfPages:
            parts.append(pagesPhrase(pagesText))
        case .none:
            break
        }
        return parts.joined(separator: " · ")
    }

    private static func pagesPhrase(_ text: String) -> String {
        let trimmed = text.trimmingCharacters(in: .whitespaces)
        return trimmed.isEmpty ? "all pages" : "pages \(trimmed)"
    }
}
