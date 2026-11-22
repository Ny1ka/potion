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

    private let matrix: ConversionMatrix
    private let defaults: UserDefaults

    init(matrix: ConversionMatrix = .shared, defaults: UserDefaults = .standard) {
        self.matrix = matrix
        self.defaults = defaults
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

    func remove(_ ids: Set<QueueItem.ID>) {
        items.removeAll { ids.contains($0.id) }
        selection.subtract(ids)
    }

    func clear() {
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
    static func lastOutput(_ category: FormatCategory) -> String { "lastOutput.\(category.rawValue)" }
}
