import AppKit
import PotionCore

struct BatchProgress: Equatable {
    var total = 0
    var finished = 0
    var converted = 0
    var failed = 0
    var outputs: [URL] = []
    /// Progress of the files converting right now, by row.
    var partial: [QueueItem.ID: Double] = [:]

    /// 0…1 across the whole batch, counting files in progress by how far along they are.
    var fraction: Double {
        guard total > 0 else { return 0 }
        return min((Double(finished) + partial.values.reduce(0, +)) / Double(total), 1)
    }

    /// The 1-based number of the first file still in progress, for "Converting 3 of 12…".
    var current: Int { min(finished + 1, total) }
}

struct Completion: Equatable {
    let message: String
    let outputs: [URL]
}

/// Converting the queue: up to `maxParallel` files at once, each through `ConversionService`.
/// Stop cancels the running files (FFmpeg processes are terminated) and leaves the rest waiting.
extension QueueModel {
    /// Two at a time: FFmpeg already uses every core for one video, and documents and images are quick.
    static let maxParallel = 2

    var canConvert: Bool { !isConverting && items.contains { $0.isConvertible } }

    /// Converts every row that isn't done yet (waiting, failed or canceled).
    func convertAll() {
        convert(Set(items.filter(\.isConvertible).map(\.id)))
    }

    /// Converts these rows; while a batch is running, they join it.
    func convert(_ ids: Set<QueueItem.ID>) {
        let chosen = items.indices.filter { ids.contains(items[$0].id) && items[$0].isConvertible && running[items[$0].id] == nil }
        guard !chosen.isEmpty else { return }
        for index in chosen { items[index].status = .waiting }
        if !isConverting {
            batch = BatchProgress()
            batchIDs = []
            stopping = false
            completion = nil
            completionTask?.cancel()
            isConverting = true
        }
        batchIDs.formUnion(chosen.map { items[$0].id })
        batch.total += chosen.count
        pump()
    }

    func stop() {
        guard isConverting else { return }
        stopping = true
        for task in running.values { task.cancel() }
        if running.isEmpty { finishBatch() }
    }

    /// Starts waiting rows of this batch until `maxParallel` are running; ends the batch when nothing is left.
    private func pump() {
        while !stopping, running.count < Self.maxParallel,
              let next = items.first(where: { batchIDs.contains($0.id) && $0.status == .waiting && running[$0.id] == nil }) {
            start(next)
        }
        if running.isEmpty, isConverting { finishBatch() }
    }

    private func start(_ item: QueueItem) {
        guard let output = item.output, let index = items.firstIndex(where: { $0.id == item.id }) else { return }
        items[index].status = .converting(0)
        batch.partial[item.id] = 0
        let id = item.id
        let directory = saveFolder ?? item.url.deletingLastPathComponent()
        let moveToTrash = moveOriginalsToTrash
        let service = self.service
        let relay = ProgressRelay(model: self, id: id)

        running[id] = Task { [weak self] in
            let status: QueueItem.Status
            do {
                let saved = try await service.convert(item.url, from: item.format, to: output, outputDirectory: directory,
                                                      settings: item.settings) { fraction in
                    relay.send(fraction)
                }
                if moveToTrash, !saved.isEmpty, !saved.contains(item.url) {
                    // The converted copy is verified and saved, so the original can go. A failure here keeps it.
                    try? FileManager.default.trashItem(at: item.url, resultingItemURL: nil)
                }
                status = .done(saved)
            } catch {
                if Task.isCancelled || error is CancellationError {
                    status = .canceled
                } else {
                    status = .failed((error as? LocalizedError)?.errorDescription ?? "Couldn't convert “\(item.url.lastPathComponent)”.")
                }
            }
            self?.finish(id, status: status)
        }
    }

    fileprivate func report(_ fraction: Double, for id: QueueItem.ID) {
        guard running[id] != nil, let index = items.firstIndex(where: { $0.id == id }) else { return }
        items[index].status = .converting(fraction)
        batch.partial[id] = fraction
    }

    private func finish(_ id: QueueItem.ID, status: QueueItem.Status) {
        running[id] = nil
        batch.partial[id] = nil
        batch.finished += 1
        switch status {
        case let .done(urls): batch.outputs += urls; batch.converted += 1
        case .failed: batch.failed += 1
        default: break
        }
        if let index = items.firstIndex(where: { $0.id == id }) { items[index].status = status }
        pump()
    }

    private func finishBatch() {
        isConverting = false
        let converted = batch.converted
        // Rows that never started because of Stop stay waiting, ready for the next Convert All.
        batchIDs = []
        stopping = false
        guard converted > 0 || batch.failed > 0 else { return }
        let files = converted == 1 ? "1 file converted" : "\(converted) files converted"
        let message = batch.failed == 0 ? files : "\(files), \(batch.failed) failed"
        completion = Completion(message: message, outputs: batch.outputs)
        completionTask?.cancel()
        completionTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 6_000_000_000)
            guard !Task.isCancelled else { return }
            self?.completion = nil
        }
    }

    func retry(_ ids: Set<QueueItem.ID>) { convert(ids) }

    // MARK: Save location

    func chooseSaveFolder() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.canCreateDirectories = true
        panel.allowsMultipleSelection = false
        panel.prompt = "Choose"
        panel.message = "Choose where Potion saves converted files."
        guard panel.runModal() == .OK, let url = panel.url else { return }
        useSaveFolder(url)
    }

    func useSaveFolder(_ url: URL?) {
        saveFolder = url
        guard let url else { return }
        recentFolders = [url] + recentFolders.filter { $0.standardizedFileURL != url.standardizedFileURL }.prefix(4)
    }

    /// Shows converted files (or the originals) in Finder.
    func reveal(_ ids: Set<QueueItem.ID>) {
        let urls = items.filter { ids.contains($0.id) }.flatMap { item -> [URL] in
            if case let .done(outputs) = item.status { return outputs.filter { FileManager.default.fileExists(atPath: $0.path) } }
            return [item.url]
        }
        if !urls.isEmpty { NSWorkspace.shared.activateFileViewerSelecting(urls) }
    }
}

extension QueueItem {
    /// Can be (re)converted: not done and not converting.
    var isConvertible: Bool {
        switch status {
        case .waiting, .failed, .canceled: return output != nil
        case .converting, .done: return false
        }
    }
}

/// Carries progress from a conversion (any thread) to the queue on the main actor.
private final class ProgressRelay: @unchecked Sendable {
    private weak var model: QueueModel?
    private let id: QueueItem.ID

    @MainActor init(model: QueueModel, id: QueueItem.ID) {
        self.model = model
        self.id = id
    }

    func send(_ fraction: Double) {
        Task { @MainActor in self.model?.report(fraction, for: self.id) }
    }
}
