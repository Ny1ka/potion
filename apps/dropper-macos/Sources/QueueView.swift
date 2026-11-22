import PotionCore
import SwiftUI

/// The download queue (05-layout.md: same row component as Potion): overall progress, then one row per link.
struct QueueView: View {
    @EnvironmentObject private var model: DropperModel
    @EnvironmentObject private var queue: DropQueue

    private var summary: String {
        let overall = queue.overall
        guard queue.isBusy, overall.total > 0 else { return "" }
        return "\(overall.finished) of \(overall.total) · \(Int((overall.fraction * 100).rounded()))%"
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: PotionSpacing.x3) {
                Text("Downloads").font(PotionFont.sectionHeader)
                Text(summary)
                    .font(PotionFont.secondary)
                    .foregroundStyle(PotionColor.textSecondary)
                    .monospacedDigit()
                Spacer()
                if queue.isBusy {
                    Button("Stop All") { queue.stopAll() }
                }
                Button("Clear Finished") { queue.clearFinished() }
                    .disabled(!queue.items.contains { if case .done = $0.status { true } else { false } })
            }
            .padding(.horizontal, PotionSpacing.x6)
            .padding(.vertical, PotionSpacing.x3)
            if queue.isBusy {
                ProgressView(value: queue.overall.fraction)
                    .padding(.horizontal, PotionSpacing.x6)
                    .accessibilityLabel("Overall progress")
            }

            List {
                ForEach(queue.items) { item in
                    QueueRow(item: item)
                        .contextMenu { RowActions(item: item, labelsOnly: true) }
                }
                .onMove { queue.move(fromOffsets: $0, toOffset: $1) }
            }
            .listStyle(.inset)
        }
    }
}

/// One download: category tile, name, status line, per-row progress in the category color, and its actions.
struct QueueRow: View {
    let item: DropQueue.Item

    private var format: FileFormat? { item.formatID.flatMap { FormatRegistry.shared.format(id: $0) } }
    private var category: PotionCategory {
        switch format?.category {
        case .audio: .audio
        case .video: .video
        default: item.options.outputFormatID.flatMap { FormatRegistry.shared.format(id: $0) }?.category == .audio ? .audio : .video
        }
    }

    private var isFailed: Bool { if case .failed = item.status { true } else { false } }

    private var statusLine: String {
        switch item.status {
        case .waiting: "Waiting"
        case .downloading(let progress): "Downloading · \(DropperModel.detail(progress))"
        case .inspecting: "Checking the file…"
        case .converting(let fraction): "Converting · \(Int((fraction * 100).rounded()))%"
        case .saving: "Saving…"
        case .done(let files): files.count == 1 ? "Saved as “\(files[0].lastPathComponent)”" : "Saved \(files.count) files"
        case .failed(let message): message
        case .stopped: item.canResume ? "Stopped · Retry continues where it stopped" : "Stopped"
        }
    }

    private var fraction: Double? {
        switch item.status {
        case .downloading(let progress): progress.total.map { Double(progress.bytes) / Double(max($0, 1)) }
        case .converting(let fraction): fraction
        default: nil
        }
    }

    var body: some View {
        HStack(spacing: PotionSpacing.x3) {
            PotionCategoryTile(category)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: PotionSpacing.xHalf) {
                Text(item.title)
                    .font(PotionFont.fileName)
                    .lineLimit(1)
                    .truncationMode(.middle)
                Text(statusLine)
                    .font(PotionFont.secondary)
                    .foregroundStyle(isFailed ? PotionColor.error : PotionColor.textSecondary)
                    .lineLimit(1)
                    .truncationMode(.tail)
                    .monospacedDigit()
                if item.isRunning {
                    Group {
                        if let fraction { ProgressView(value: fraction) } else { ProgressView() }
                    }
                    .progressViewStyle(.linear)
                    .tint(category.fill)
                    .padding(.top, PotionSpacing.x1)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .help(item.link.string)
            RowActions(item: item, labelsOnly: false)
        }
        .frame(minHeight: PotionSize.queueRow)
        .accessibilityElement(children: .contain)
    }
}

/// Stop, Retry, Show in Finder and Remove, as icon buttons in the row or as context-menu items.
struct RowActions: View {
    @EnvironmentObject private var model: DropperModel
    @EnvironmentObject private var queue: DropQueue
    let item: DropQueue.Item
    let labelsOnly: Bool

    var body: some View {
        if item.isRunning || item.status == .waiting {
            action("Stop", "xmark.circle") { queue.stop(item.id) }
        }
        if item.canRetry {
            action("Retry", "arrow.clockwise") { queue.retry(item.id) }
        }
        if case .done(let files) = item.status {
            action("Show in Finder", "magnifyingglass") { model.showInFinder(files) }
        }
        if labelsOnly && !item.isRunning {
            Divider()
            Button("Remove") { queue.remove(item.id) }
        }
    }

    @ViewBuilder
    private func action(_ title: String, _ symbol: String, _ perform: @escaping () -> Void) -> some View {
        if labelsOnly {
            Button(title, action: perform)
        } else {
            Button(action: perform) { Image(systemName: symbol) }
                .buttonStyle(.borderless)
                .help(title)
                .accessibilityLabel(title)
        }
    }
}
