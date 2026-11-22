import PDFKit
import PotionCore
import SwiftUI

/// Convert sheet (05-layout.md, "Potion: convert sheet"): pick the format and Advanced options for one or more
/// rows, then convert them. Return converts, Esc cancels.
struct ConvertSheet: View {
    @EnvironmentObject var queue: QueueModel
    @Environment(\.dismiss) private var dismiss

    let items: [QueueItem]
    @State private var outputID: String
    @State private var settings: ConversionSettings
    @State private var pagesText: String
    /// Remembered: people who use Advanced keep seeing it.
    @AppStorage("convertSheet.showAdvanced") private var showAdvanced = false
    private let outputs: [FileFormat]
    private let pageCount: Int?

    init(items: [QueueItem], matrix: ConversionMatrix = .shared) {
        self.items = items
        let first = items[0]
        let common = first.outputs.filter { output in items.allSatisfy { $0.outputs.contains { $0.id == output.id } } }
        outputs = common
        _outputID = State(initialValue: common.contains { $0.id == first.outputID } ? first.outputID : (common.first?.id ?? first.outputID))
        _settings = State(initialValue: first.settings)
        _pagesText = State(initialValue: first.settings.pdf.pages.map(PageSelection.describe) ?? "")
        // Page ranges only make sense for one PDF.
        pageCount = items.count == 1 && first.format.id == "pdf" ? PDFDocument(url: first.url)?.pageCount : nil
    }

    private var output: FileFormat? { outputs.first { $0.id == outputID } }

    /// Every row converts with the same operation, so one set of Advanced options fits all.
    private var kind: OptionsKind {
        guard let output else { return .none }
        let kinds = Set(items.compactMap { ConversionMatrix.shared.conversion(from: $0.format.id, to: output.id)?.operation })
        guard kinds.count == 1, let operation = kinds.first else { return .none }
        let kind = OptionsKind.of(operation, output: output)
        if case .pdfPages = kind, pageCount == nil { return .none }
        return kind
    }

    private var notes: [String] {
        guard let output else { return [] }
        var seen = Set<String>()
        return items.flatMap { ConversionMatrix.shared.conversion(from: $0.format.id, to: output.id)?.notes ?? [] }
            .filter { seen.insert($0.id).inserted }.map(\.text)
    }

    private var pages: [Int]?? {
        guard let pageCount, kind.usesPages else { return .some(nil) }
        let trimmed = pagesText.trimmingCharacters(in: .whitespaces)
        if trimmed.isEmpty { return .some(nil) }
        guard let pages = try? PageSelection.pages(trimmed, pageCount: pageCount) else { return nil }   // invalid
        return .some(pages)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: PotionSpacing.x3) {
                header
                VStack(alignment: .leading, spacing: PotionSpacing.x2) {
                    HStack(spacing: PotionSpacing.x2) {
                        Text("Convert to")
                        Picker("Convert to", selection: $outputID) {
                            ForEach(FormatGroups.grouped(outputs), id: \.category) { group in
                                Section(group.category.label) {
                                    ForEach(group.formats) { Text($0.name).tag($0.id) }
                                }
                            }
                        }
                        .labelsHidden()
                        .frame(width: QueueRowView.formatColumnWidth + PotionSpacing.x8)
                    }
                    ForEach(notes, id: \.self) { note in
                        Text(note)
                            .font(PotionFont.caption)
                            .foregroundStyle(PotionColor.textSecondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    if kind != .none, let output {
                        DisclosureGroup("Advanced", isExpanded: $showAdvanced) {
                            VStack(alignment: .leading, spacing: PotionSpacing.x3) {
                                Text(OptionsSummary.line(kind: kind, output: output, settings: settings,
                                                         pageCount: pageCount, pagesText: pagesText))
                                    .font(PotionFont.secondary)
                                    .foregroundStyle(PotionColor.textSecondary)
                                AdvancedOptionsView(kind: kind, settings: $settings, pagesText: $pagesText, pageCount: pageCount)
                            }
                            .padding(.top, PotionSpacing.x2)
                        }
                    }
                }
                .padding(.leading, PotionSize.categoryTileLarge + PotionSpacing.x3)   // aligned with the file name
            }
            .padding(PotionSpacing.x4)

            Divider()

            HStack(spacing: PotionSpacing.x2) {
                Toggle(items.count == 1 ? "Move original to Trash" : "Move originals to Trash", isOn: $queue.moveOriginalsToTrash)
                    .toggleStyle(.checkbox)
                Spacer()
                Button("Cancel") { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("Convert") { convert() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(output == nil || pages == nil)
            }
            .padding(PotionSpacing.x4)
        }
        .frame(width: PotionSize.convertSheetWidth)
        .onChange(of: outputID) { _ in keepCodecValid() }
    }

    @ViewBuilder private var header: some View {
        HStack(spacing: PotionSpacing.x3) {
            if items.count == 1, let item = items.first {
                PotionCategoryTile(item.category, size: PotionSize.categoryTileLarge)
                    .accessibilityHidden(true)
                VStack(alignment: .leading, spacing: PotionSpacing.xHalf) {
                    Text(item.url.lastPathComponent)
                        .font(PotionFont.fileName)
                        .lineLimit(1)
                        .truncationMode(.middle)
                    Text([item.category.label, item.size.map { ByteCountFormatter.string(fromByteCount: $0, countStyle: .file) }]
                        .compactMap { $0 }.joined(separator: " · "))
                        .font(PotionFont.secondary)
                        .foregroundStyle(PotionColor.textSecondary)
                }
            } else {
                // Up to three tiles, stacked.
                ZStack(alignment: .topLeading) {
                    ForEach(Array(Set(items.map(\.category))).sorted { $0.rawValue < $1.rawValue }.prefix(3).enumerated().reversed(), id: \.offset) { index, category in
                        PotionCategoryTile(category, size: PotionSize.categoryTile)
                            .offset(x: CGFloat(index) * PotionSpacing.x1 * 1.5, y: CGFloat(index) * PotionSpacing.x1 * 1.5)
                    }
                }
                .frame(width: PotionSize.categoryTileLarge, height: PotionSize.categoryTileLarge, alignment: .topLeading)
                .accessibilityHidden(true)
                Text("\(items.count) files")
                    .font(PotionFont.fileName)
            }
        }
    }

    /// A codec chosen for one container may not exist in another; fall back to the default.
    private func keepCodecValid() {
        guard let codec = settings.video.codec else { return }
        if !(VideoPresets.shared.formats[outputID]?.videoCodecs.contains(codec) ?? false) { settings.video.codec = nil }
    }

    private func convert() {
        guard let output, let chosenPages = pages else { return }
        var final = settings
        final.pdf.pages = chosenPages
        let ids = Set(items.map(\.id))
        queue.apply(outputID: output.id, settings: final, to: ids)
        dismiss()
        queue.convert(ids)
    }
}
