import AppKit
import PotionCore
import SwiftUI

/// The main window: the drop zone when the queue is empty, otherwise the queue with its footer.
/// The whole window accepts dropped files and folders.
struct ContentView: View {
    @EnvironmentObject var queue: QueueModel
    @State private var drop: DropState = .idle
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        VStack(spacing: 0) {
            if let message = bannerMessage {
                Banner(message: message) { queue.unsupported = []; queue.truncatedCount = nil }
                    .padding([.horizontal, .top], PotionSpacing.x4)
            }
            if queue.items.isEmpty {
                DropZoneView(state: drop, includeSubfolders: queue.includeSubfolders, isAdding: queue.isAdding) {
                    queue.showImporter = true
                }
                .padding(PotionSpacing.x5)
            } else {
                QueueListView()
                    .overlay {
                        if drop.isTargeted {
                            Rectangle()
                                .strokeBorder(drop == .invalid ? PotionColor.error : PotionColor.accent, lineWidth: 2)
                                .allowsHitTesting(false)
                        }
                    }
                Divider()
                FooterView()
            }
        }
        .animation(reduceMotion ? nil : PotionMotion.standard(PotionMotion.fast), value: drop)
        .onDrop(of: [.fileURL], delegate: FileDropDelegate(state: $drop) { queue.add($0) })
        .fileImporter(isPresented: $queue.showImporter, allowedContentTypes: [.item, .folder],
                      allowsMultipleSelection: true) { result in
            if case let .success(urls) = result { queue.add(urls) }
        }
        .toolbar {
            ToolbarItem {
                Button { queue.showImporter = true } label: { Label("Add Files", systemImage: "plus") }
                    .help("Add files or folders")
            }
        }
    }

    private var bannerMessage: String? {
        if let count = queue.truncatedCount {
            return "Added the first \(count.formatted()) files. Add the rest in another batch."
        }
        let names = queue.unsupported.map(\.lastPathComponent)
        guard !names.isEmpty else { return nil }
        let listed: String
        switch names.count {
        case 1: return "“\(names[0])” can't be converted."
        case 2...3: listed = ListFormatter.localizedString(byJoining: names.map { "“\($0)”" })
        default: listed = names.prefix(2).map { "“\($0)”" }.joined(separator: ", ") + " and \(names.count - 2) more"
        }
        return "\(names.count) files can't be converted: \(listed)."
    }
}

// MARK: - Drop zone

/// Empty-state drop zone (design-system/docs/06-components.md, "Drop zone").
struct DropZoneView: View {
    let state: DropState
    let includeSubfolders: Bool
    let isAdding: Bool
    let choose: () -> Void

    var body: some View {
        VStack(spacing: 6) {
            Image("MarkMono")
                .resizable()
                .renderingMode(.template)
                .frame(width: 48, height: 48)
                .foregroundStyle(PotionColor.textTertiary)
                .accessibilityHidden(true)
            Text(state.isTargeted ? state.label : "Drop files or folders")
                .font(.body.weight(.semibold))
                .foregroundStyle(state == .invalid ? PotionColor.error : PotionColor.textPrimary)
            Text(includeSubfolders ? "Video, audio, images, documents and PDFs. Folders include their subfolders."
                                   : "Video, audio, images, documents and PDFs")
                .font(PotionFont.secondary)
                .foregroundStyle(PotionColor.textSecondary)
                .multilineTextAlignment(.center)
            Group {
                if isAdding {
                    ProgressView().controlSize(.small)
                } else {
                    Button("Choose Files…", action: choose)
                }
            }
            .padding(.top, 10 - 6)   // 10 below the caption (06-components), minus the stack spacing
        }
        .padding(PotionSpacing.x4)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .frame(minHeight: PotionSize.dropZoneMinHeight)
        .background(
            RoundedRectangle(cornerRadius: PotionRadius.lg, style: .continuous)
                .fill(state == .invalid || !state.isTargeted ? Color.clear : PotionColor.accent.opacity(0.08))
        )
        .overlay(border)
        .accessibilityElement(children: .contain)
    }

    @ViewBuilder private var border: some View {
        let shape = RoundedRectangle(cornerRadius: PotionRadius.lg, style: .continuous)
        switch state {
        case .idle: shape.strokeBorder(PotionColor.borderStrong, style: StrokeStyle(lineWidth: 1.5, dash: [6, 4]))
        case .valid: shape.strokeBorder(PotionColor.accent, lineWidth: 2)
        case .invalid: shape.strokeBorder(PotionColor.error, style: StrokeStyle(lineWidth: 1.5, dash: [6, 4]))
        }
    }
}

// MARK: - Banner

/// Inline banner above the list for batch-level messages (06-components.md, "Errors").
struct Banner: View {
    let message: String
    let dismiss: () -> Void

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: PotionSpacing.x2) {
            Image(systemName: "exclamationmark.circle")
                .foregroundStyle(PotionColor.warning)
                .accessibilityHidden(true)
            Text(message)
                .font(PotionFont.secondary)
                .foregroundStyle(PotionColor.textPrimary)
                .frame(maxWidth: .infinity, alignment: .leading)
            Button(action: dismiss) { Image(systemName: "xmark") }
                .buttonStyle(.borderless)
                .help("Dismiss")
                .accessibilityLabel("Dismiss")
        }
        .padding(PotionSpacing.x3)
        .background(PotionColor.surfaceRaised, in: RoundedRectangle(cornerRadius: PotionRadius.md, style: .continuous))
    }
}

// MARK: - Footer

/// Footer bar (06-components.md, "Footer bar"). Saving options and Convert All arrive with conversion (M3.2).
struct FooterView: View {
    @EnvironmentObject var queue: QueueModel

    var body: some View {
        HStack(spacing: PotionSpacing.x2) {
            Text(queue.items.count == 1 ? "1 file" : "\(queue.items.count) files")
                .font(PotionFont.secondary)
                .foregroundStyle(PotionColor.textSecondary)
                .monospacedDigit()
            Spacer()
            Button("Clear") { queue.clear() }
        }
        .padding(.horizontal, PotionSpacing.x4)
        .frame(height: 52)
    }
}

#Preview {
    ContentView()
        .environmentObject(QueueModel())
        .frame(width: PotionSize.windowDefault.width, height: PotionSize.windowDefault.height)
}
