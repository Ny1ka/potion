import Foundation
import PotionCore

/// One file in the queue.
struct QueueItem: Identifiable, Equatable {
    enum Status: Equatable {
        case waiting
        case converting(Double)
        case done([URL])
        case failed(String)
        case canceled
    }

    let id = UUID()
    let url: URL
    let format: FileFormat
    let size: Int64?
    /// The formats this file can become, from the conversion matrix (input's own category first).
    let outputs: [FileFormat]
    var outputID: String
    /// Advanced options chosen in the convert sheet (each engine reads its own part).
    var settings = ConversionSettings()
    var status: Status = .waiting

    var category: PotionCategory { PotionCategory(rawValue: format.category.rawValue) ?? .docs }
    var output: FileFormat? { outputs.first { $0.id == outputID } }
}

/// The main window's file list. Adding files runs detection off the main thread.
@MainActor
final class QueueModel: ObservableObject {
    @Published var items: [QueueItem] = []
    @Published var selection = Set<QueueItem.ID>()
    /// Files from the last add that Potion can't convert (shown in a banner until dismissed).
    @Published var unsupported: [URL] = []
    @Published var truncatedCount: Int?
    @Published var isAdding = false
    @Published var showImporter = false
    /// Rows whose convert sheet is open.
    @Published var sheet: SheetRequest?

    // Converting (see QueueRunner.swift)
    @Published var isConverting = false
    @Published var batch = BatchProgress()
    /// "12 files converted", shown in the footer for a few seconds after a batch.
    @Published var completion: Completion?
    /// Where converted files go: nil = next to each original.
    @Published var saveFolder: URL? { didSet { defaults.set(saveFolder?.path, forKey: SettingsKey.saveFolder) } }
    @Published var recentFolders: [URL] { didSet { defaults.set(recentFolders.map(\.path), forKey: SettingsKey.recentFolders) } }
    @Published var moveOriginalsToTrash: Bool { didSet { defaults.set(moveOriginalsToTrash, forKey: SettingsKey.moveToTrash) } }

    let matrix: ConversionMatrix
    let defaults: UserDefaults
    let service: ConversionService
    var running: [QueueItem.ID: Task<Void, Never>] = [:]
    var batchIDs = Set<QueueItem.ID>()
    var stopping = false
    var completionTask: Task<Void, Never>?

    init(matrix: ConversionMatrix = .shared, defaults: UserDefaults = .standard,
         service: ConversionService = .withBundledTools()) {
        self.matrix = matrix
        self.defaults = defaults
        self.service = service
        let fm = FileManager.default
        func existingFolder(_ path: String?) -> URL? {
            var isDir: ObjCBool = false
            guard let path, fm.fileExists(atPath: path, isDirectory: &isDir), isDir.boolValue else { return nil }
            return URL(fileURLWithPath: path, isDirectory: true)
        }
        saveFolder = existingFolder(defaults.string(forKey: SettingsKey.saveFolder))
        recentFolders = (defaults.stringArray(forKey: SettingsKey.recentFolders) ?? []).compactMap(existingFolder)
        moveOriginalsToTrash = defaults.bool(forKey: SettingsKey.moveToTrash)
    }

    var includeSubfolders: Bool { defaults.object(forKey: SettingsKey.includeSubfolders) as? Bool ?? true }

    func add(_ urls: [URL]) {
        guard !urls.isEmpty else { return }
        let existing = Set(items.map(\.url))
        let subfolders = includeSubfolders
        isAdding = true
        Task {
            let result = await Task.detached(priority: .userInitiated) {
                FileCollector().collect(urls, includeSubfolders: subfolders, excluding: existing)
            }.value
            append(result)
        }
    }

    private func append(_ result: FileCollector.Result) {
        isAdding = false
        for file in result.files {
            let outputs = matrix.outputs(for: file.format.id).map(\.output)
            guard let output = defaultOutput(for: file.format, outputs: outputs) else { continue }
            items.append(QueueItem(url: file.url, format: file.format, size: file.size, outputs: outputs, outputID: output))
        }
        unsupported = result.unsupported
        truncatedCount = result.truncated ? result.files.count : nil
    }

    /// The last format chosen for this category, if this file can become it; otherwise the first output in the
    /// file's own category that isn't its current format.
    private func defaultOutput(for format: FileFormat, outputs: [FileFormat]) -> String? {
        if let last = defaults.string(forKey: SettingsKey.lastOutput(format.category)), last != format.id,
           outputs.contains(where: { $0.id == last }) {
            return last
        }
        return (outputs.first { $0.id != format.id } ?? outputs.first)?.id
    }

    func setOutput(_ outputID: String, for ids: Set<QueueItem.ID>) {
        for index in items.indices where ids.contains(items[index].id) && items[index].outputs.contains(where: { $0.id == outputID }) {
            items[index].outputID = outputID
            items[index].status = .waiting
            defaults.set(outputID, forKey: SettingsKey.lastOutput(items[index].format.category))
        }
    }

    /// Applies the convert sheet's choices to these rows.
    func apply(outputID: String, settings: ConversionSettings, to ids: Set<QueueItem.ID>) {
        setOutput(outputID, for: ids)
        for index in items.indices where ids.contains(items[index].id) && items[index].outputID == outputID {
            items[index].settings = settings
        }
    }

    /// Opens the convert sheet for these rows (or the selection).
    func showOptions(for ids: Set<QueueItem.ID>) {
        let chosen = items.filter { ids.contains($0.id) && !$0.isBusy }
        guard !chosen.isEmpty else { return }
        sheet = SheetRequest(ids: Set(chosen.map(\.id)))
    }

    func remove(_ ids: Set<QueueItem.ID>) {
        for id in ids { running[id]?.cancel() }
        items.removeAll { ids.contains($0.id) }
        selection.subtract(ids)
    }

    func clear() {
        guard !isConverting else { return }
        items.removeAll()
        selection.removeAll()
        unsupported = []
        truncatedCount = nil
    }

    /// Formats offered for several rows at once: only those every one of them can become.
    func commonOutputs(for ids: Set<QueueItem.ID>) -> [FileFormat] {
        let chosen = items.filter { ids.contains($0.id) }
        guard let first = chosen.first else { return [] }
        return first.outputs.filter { output in chosen.allSatisfy { item in item.outputs.contains { $0.id == output.id } } }
    }
}

enum SettingsKey {
    static let includeSubfolders = "includeSubfolders"
    static let saveFolder = "saveFolder"
    static let recentFolders = "recentFolders"
    static let moveToTrash = "moveOriginalsToTrash"
    static let notifications = "notifyWhenDone"
    static let maxParallel = "maxParallelConversions"
    static func lastOutput(_ category: FormatCategory) -> String { "lastOutput.\(category.rawValue)" }
}

struct SheetRequest: Identifiable {
    let id = UUID()
    let ids: Set<QueueItem.ID>
}

extension QueueItem {
    var isBusy: Bool { if case .converting = status { return true } else { return false } }
}
