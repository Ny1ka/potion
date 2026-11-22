import AppKit
import PotionCore
import SwiftUI

/// The queue (06-components.md, "Queue row"): native list with multi-select, Delete to remove, and a
/// context menu with Change Format, Show in Finder and Remove.
struct QueueListView: View {
    @EnvironmentObject var queue: QueueModel

    var body: some View {
        List(selection: $queue.selection) {
            ForEach(queue.items) { item in
                QueueRowView(item: item) { outputID in
                    // Changing the format of a selected row changes every selected row that can take it.
                    let targets = queue.selection.contains(item.id) ? queue.selection : [item.id]
                    queue.setOutput(outputID, for: targets)
                }
                .tag(item.id)
            }
        }
        .listStyle(.inset)
        .onDeleteCommand { queue.remove(queue.selection) }
        .contextMenu(forSelectionType: QueueItem.ID.self) { ids in
            Button("Options…") { queue.showOptions(for: ids) }
                .disabled(queue.items.allSatisfy { !ids.contains($0.id) || $0.isBusy })
            let formats = queue.commonOutputs(for: ids)
            if !formats.isEmpty {
                Menu("Change Format") {
                    FormatMenuItems(formats: formats) { queue.setOutput($0, for: ids) }
                }
            }
            let retryable = queue.items.filter { ids.contains($0.id) && $0.isRetryable }
            if !retryable.isEmpty {
                Button("Retry") { queue.retry(Set(retryable.map(\.id))) }
            }
            Button("Show in Finder") { queue.reveal(ids) }
            Divider()
            Button("Remove") { queue.remove(ids) }
        } primaryAction: { ids in
            queue.showOptions(for: ids)   // double-click opens the convert sheet
        }
    }
}

/// Formats grouped by category, with the category as a section header.
struct FormatMenuItems: View {
    let formats: [FileFormat]
    let choose: (String) -> Void

    var body: some View {
        ForEach(FormatGroups.grouped(formats), id: \.category) { group in
            Section(group.category.label) {
                ForEach(group.formats) { format in Button(format.name) { choose(format.id) } }
            }
        }
    }
}

enum FormatGroups {
    struct Group { let category: PotionCategory; let formats: [FileFormat] }

    /// Keeps the matrix order (the input's own category first) and groups by category.
    static func grouped(_ formats: [FileFormat]) -> [Group] {
        var order: [PotionCategory] = []
        var byCategory: [PotionCategory: [FileFormat]] = [:]
        for format in formats {
            let category = PotionCategory(rawValue: format.category.rawValue) ?? .docs
            if byCategory[category] == nil { order.append(category) }
            byCategory[category, default: []].append(format)
        }
        return order.map { Group(category: $0, formats: byCategory[$0] ?? []) }
    }
}

struct QueueRowView: View {
    /// Fixed so popups line up from row to row; fits the longest format name ("Markdown") on the 4-pt grid.
    static let formatColumnWidth: CGFloat = 112

    let item: QueueItem
    let setOutput: (String) -> Void

    var body: some View {
        HStack(spacing: PotionSpacing.x3) {
            PotionCategoryTile(item.category)
                .accessibilityHidden(true)   // the row says the format
            VStack(alignment: .leading, spacing: PotionSpacing.xHalf) {
                Text(item.url.lastPathComponent)
                    .font(PotionFont.fileName)
                    .foregroundStyle(PotionColor.textPrimary)
                    .lineLimit(1)
                    .truncationMode(.middle)
                Text(secondaryLine)
                    .font(PotionFont.secondary)
                    .foregroundStyle(isFailed ? PotionColor.error : PotionColor.textSecondary)
                    .lineLimit(1)
                    .truncationMode(.tail)
                if case let .converting(fraction) = item.status {
                    RowProgressBar(fraction: fraction, fill: item.category.fill)
                        .padding(.top, PotionSpacing.x1)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .help(item.url.path)
            .alignmentGuide(.listRowSeparatorLeading) { $0[.leading] }   // separators start at the file name

            Image(systemName: "arrow.right")
                .font(.system(size: 14))
                .foregroundStyle(PotionColor.textTertiary)
                .accessibilityHidden(true)
            Picker("Convert to", selection: Binding(get: { item.outputID }, set: setOutput)) {
                ForEach(FormatGroups.grouped(item.outputs), id: \.category) { group in
                    Section(group.category.label) {
                        ForEach(group.formats) { Text($0.name).tag($0.id) }
                    }
                }
            }
            .labelsHidden()
            .pickerStyle(.menu)
            .frame(width: Self.formatColumnWidth)
            .disabled(isConverting)
            .help("Convert to")
            StatusView(status: item.status)
                .frame(width: 72, alignment: .trailing)
        }
        .frame(minHeight: PotionSize.queueRow)
        .accessibilityElement(children: .contain)
    }

    private var isFailed: Bool { if case .failed = item.status { return true } else { return false } }
    private var isConverting: Bool { if case .converting = item.status { return true } else { return false } }

    private var secondaryLine: String {
        if case let .failed(reason) = item.status { return reason }
        let size = item.size.map { ByteCountFormatter.string(fromByteCount: $0, countStyle: .file) }
        return [item.format.name, size].compactMap { $0 }.joined(separator: " · ")
    }
}

/// Right-hand status column: icon plus words, tabular digits.
struct StatusView: View {
    let status: QueueItem.Status

    var body: some View {
        Group {
            switch status {
            case .waiting:
                Text("Waiting").foregroundStyle(PotionColor.textTertiary)
            case let .converting(fraction):
                Text(fraction.formatted(.percent.precision(.fractionLength(0)))).foregroundStyle(PotionColor.textSecondary)
            case .done:
                Label("Done", systemImage: "checkmark").foregroundStyle(PotionColor.success)
            case .failed:
                Label("Failed", systemImage: "exclamationmark.circle").foregroundStyle(PotionColor.error)
            case .canceled:
                Text("Canceled").foregroundStyle(PotionColor.textTertiary)
            }
        }
        .font(PotionFont.secondary)
        .monospacedDigit()
        .lineLimit(1)
    }
}

/// Per-row progress (06-components.md): 4 pt, the file's category fill on a raised track.
struct RowProgressBar: View {
    let fraction: Double
    let fill: Color

    var body: some View {
        GeometryReader { geometry in
            ZStack(alignment: .leading) {
                Capsule().fill(PotionColor.surfaceRaised)
                Capsule().fill(fill).frame(width: geometry.size.width * min(max(fraction, 0), 1))
            }
        }
        .frame(height: PotionSize.progressBar)
        .accessibilityElement()
        .accessibilityLabel("Progress")
        .accessibilityValue(fraction.formatted(.percent.precision(.fractionLength(0))))
    }
}

extension QueueItem {
    var isRetryable: Bool {
        switch status {
        case .failed, .canceled: return true
        default: return false
        }
    }
}
