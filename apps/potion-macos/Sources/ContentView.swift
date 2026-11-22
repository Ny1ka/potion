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
        .sheet(item: $queue.sheet) { request in
            let chosen = queue.items.filter { request.ids.contains($0.id) }
            if !chosen.isEmpty { ConvertSheet(items: chosen) }
        }
        .toolbar {
            ToolbarItem {
                Button { queue.showImporter = true } label: { Label("Add Files", systemImage: "plus") }
                    .help("Add files or folders")
            }
            ToolbarItem {
                SettingsButton()
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

/// Footer bar (06-components.md, "Footer bar"): save options and Convert All; overall progress and Stop
/// while converting; a short summary afterwards.
struct FooterView: View {
    @EnvironmentObject var queue: QueueModel

    var body: some View {
        HStack(spacing: PotionSpacing.x3) {
            if queue.isConverting {
                VStack(alignment: .leading, spacing: PotionSpacing.x1) {
                    Text("Converting \(queue.batch.current) of \(queue.batch.total)…")
                        .font(PotionFont.secondary)
                        .foregroundStyle(PotionColor.textSecondary)
                        .monospacedDigit()
                    ProgressView(value: queue.batch.fraction)
                        .progressViewStyle(.linear)
                        .accessibilityLabel("Overall progress")
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                Button("Stop") { queue.stop() }
                    .keyboardShortcut(.cancelAction)
            } else {
                if let completion = queue.completion {
                    CompletionView(completion: completion)
                } else {
                    // Narrow windows drop the "Save to:" label, then shorten the checkbox label.
                    // Narrow windows drop "Convert all to" (Select All + Change Format does the same), then the
                    // "Save to:" label, then shorten the checkbox label.
                    ViewThatFits(in: .horizontal) {
                        HStack(spacing: PotionSpacing.x3) { ConvertAllToMenu(); saveOptions(showLabel: true, trashLabel: "Move originals to Trash") }
                        HStack(spacing: PotionSpacing.x3) { ConvertAllToMenu(); saveOptions(showLabel: false, trashLabel: "Move to Trash") }
                        saveOptions(showLabel: true, trashLabel: "Move originals to Trash")
                        saveOptions(showLabel: false, trashLabel: "Move originals to Trash")
                        saveOptions(showLabel: false, trashLabel: "Move to Trash")
                    }
                }
                Spacer(minLength: PotionSpacing.x2)
                Button("Clear") { queue.clear() }
                Button("Convert All") { queue.convertAll() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(!queue.canConvert)
            }
        }
        .padding(.horizontal, PotionSpacing.x4)
        .frame(height: 52)
    }
}

extension FooterView {
    func saveOptions(showLabel: Bool, trashLabel: String) -> some View {
        HStack(spacing: PotionSpacing.x3) {
            SaveToPicker(showLabel: showLabel)
            Toggle(trashLabel, isOn: $queue.moveOriginalsToTrash)
                .toggleStyle(.checkbox)
                .help("After a file converts, move the original to the Trash")
                .accessibilityLabel("Move originals to Trash")
                .fixedSize()
        }
    }
}

/// "Convert all to [MP3 ▾]": shown when every row is the same kind of file (05-layout.md).
struct ConvertAllToMenu: View {
    @EnvironmentObject var queue: QueueModel

    var body: some View {
        let ids = Set(queue.items.map(\.id))
        let formats = queue.commonOutputs(for: ids)
        let sameCategory = Set(queue.items.map(\.format.category)).count == 1
        if sameCategory, queue.items.count > 1, !formats.isEmpty {
            let current = Set(queue.items.map(\.outputID))
            let title = current.count == 1 ? formats.first { $0.id == current.first }?.name ?? "Choose" : "Choose"
            Menu {
                FormatMenuItems(formats: formats) { queue.setOutput($0, for: ids) }
            } label: {
                Text("Convert all to: \(title)")
            }
            .fixedSize()
            .help("Set the same format for every file")
        }
    }
}

struct CompletionView: View {
    @EnvironmentObject var queue: QueueModel
    let completion: Completion

    var body: some View {
        HStack(spacing: PotionSpacing.x1) {
            Image(systemName: "checkmark").foregroundStyle(PotionColor.success).accessibilityHidden(true)
            Text(completion.message)
                .font(PotionFont.secondary)
                .foregroundStyle(PotionColor.textPrimary)
                .monospacedDigit()
                .lineLimit(1)
            if !completion.outputs.isEmpty {
                Text("·").foregroundStyle(PotionColor.textTertiary).accessibilityHidden(true)
                Button("Show in Finder") {
                    NSWorkspace.shared.activateFileViewerSelecting(
                        completion.outputs.filter { FileManager.default.fileExists(atPath: $0.path) })
                }
                .buttonStyle(.link)
                .font(PotionFont.secondary)
            }
        }
    }
}

/// "Save to:" popup: next to each original, a recent folder, or a folder the user chooses.
struct SaveToPicker: View {
    @EnvironmentObject var queue: QueueModel
    var showLabel = true

    enum Choice: Hashable { case original, folder(URL), choose }

    var body: some View {
        Picker("Save to:", selection: selection) {
            Text("Same Folder as Original").tag(Choice.original)
            if !folders.isEmpty {
                Divider()
                ForEach(folders, id: \.self) { url in
                    Label(FileManager.default.displayName(atPath: url.path), systemImage: "folder").tag(Choice.folder(url))
                }
            }
            Divider()
            Text("Choose Folder…").tag(Choice.choose)
        }
        .pickerStyle(.menu)
        .labelsHidden(!showLabel)
        .fixedSize()
        .help(queue.saveFolder?.path ?? "Converted files are saved next to the originals")
    }

    private var folders: [URL] {
        var list = queue.recentFolders
        if let current = queue.saveFolder, !list.contains(current) { list.insert(current, at: 0) }
        return list
    }

    private var selection: Binding<Choice> {
        Binding(
            get: { queue.saveFolder.map(Choice.folder) ?? .original },
            set: { choice in
                switch choice {
                case .original: queue.useSaveFolder(nil)
                case let .folder(url): queue.useSaveFolder(url)
                case .choose: DispatchQueue.main.async { queue.chooseSaveFolder() }   // after the menu closes
                }
            })
    }
}

#Preview {
    ContentView()
        .environmentObject(QueueModel())
        .frame(width: PotionSize.windowDefault.width, height: PotionSize.windowDefault.height)
}

private extension View {
    @ViewBuilder func labelsHidden(_ hidden: Bool) -> some View {
        if hidden { labelsHidden() } else { self }
    }
}

/// Toolbar gear that opens Settings (09-platforms.md, "Toolbar").
struct SettingsButton: View {
    var body: some View {
        if #available(macOS 14, *) {
            SettingsLink { Label("Settings", systemImage: "gearshape") }
                .help("Settings")
        } else {
            Button {
                NSApp.sendAction(Selector(("showSettingsWindow:")), to: nil, from: nil)
            } label: { Label("Settings", systemImage: "gearshape") }
                .help("Settings")
        }
    }
}
