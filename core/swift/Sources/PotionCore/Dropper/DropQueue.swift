import Combine
import Foundation

/// Potion Dropper's download queue: links wait their turn, up to `maxParallel` run at once, each can be stopped,
/// retried (resuming when the server allows) or removed. The app shows `items`; the actual work is the `run`
/// closure (a `DropJobRunner` in the app, a fake in tests).
@MainActor
public final class DropQueue: ObservableObject {
    public typealias Run = @Sendable (DropLink, URL, DropOptions, ResumeState?, @escaping @Sendable (DropStage) -> Void) async throws -> DropOutcome

    public struct Item: Identifiable, Equatable, Sendable {
        public enum Status: Equatable, Sendable {
            case waiting
            case downloading(DownloadProgress)
            case inspecting
            case converting(Double)
            case saving
            case done([URL])
            case failed(String)
            case stopped
        }

        public let id: UUID
        public let link: DropLink
        /// Where the download is saved (fixed when it's queued).
        public let folder: URL
        public let options: DropOptions
        /// What the row shows: the file name from Fetch, or the link's own file name or site.
        public let title: String
        /// The format the row's tile shows: the downloaded one until the result is known.
        public internal(set) var formatID: String?
        public internal(set) var status: Status
        /// A failed or stopped download that Retry continues instead of starting over.
        public internal(set) var canResume = false

        public var isRunning: Bool {
            switch status {
            case .downloading, .inspecting, .converting, .saving: true
            default: false
            }
        }
        public var isFinished: Bool {
            switch status {
            case .done, .failed, .stopped: true
            default: false
            }
        }
        public var canRetry: Bool {
            switch status {
            case .failed, .stopped: true
            default: false
            }
        }
    }

    @Published public private(set) var items: [Item] = []
    /// Downloads at once (1–4).
    @Published public var maxParallel: Int {
        didSet {
            // @Published re-runs this observer on assignment, so only reassign when out of range (once).
            let clamped = min(max(maxParallel, 1), 4)
            if clamped != maxParallel { maxParallel = clamped } else { pump() }
        }
    }

    /// After each download finishes, fails or stops.
    public var onItemFinished: ((Item) -> Void)?
    /// When nothing is left to run: the items of that batch that finished and that failed (stopped ones are neither).
    public var onBatchFinished: ((_ done: [Item], _ failed: [Item]) -> Void)?

    private let run: Run
    private let registry: SourceRegistry
    private var tasks: [UUID: Task<Void, Never>] = [:]
    private var resumes: [UUID: ResumeState] = [:]
    private var stopping: Set<UUID> = []
    private var batch: [UUID] = []
    private var lastProgress: [UUID: Date] = [:]

    public init(maxParallel: Int = 2, registry: SourceRegistry = .shared, run: @escaping Run) {
        self.maxParallel = min(max(maxParallel, 1), 4)
        self.registry = registry
        self.run = run
    }

    public var isBusy: Bool { !tasks.isEmpty || items.contains { $0.status == .waiting } }

    /// The current batch: finished items, all items, and the overall fraction (running downloads count by progress).
    public var overall: (finished: Int, total: Int, fraction: Double) {
        let current = items.filter { batch.contains($0.id) }
        guard !current.isEmpty else { return (0, 0, 0) }
        let finished = current.filter(\.isFinished).count
        let partial = current.reduce(0.0) { sum, item in
            switch item.status {
            case .downloading(let p): sum + (p.total.map { Double(p.bytes) / Double(max($0, 1)) } ?? 0) * 0.8
            case .inspecting: sum + 0.8
            case .converting(let f): sum + 0.8 + f * 0.2
            case .saving: sum + 0.99
            default: sum
            }
        }
        return (finished, current.count, (Double(finished) + partial) / Double(current.count))
    }

    /// Adds a link. A link that is already waiting or downloading isn't added twice; its id is returned instead.
    @discardableResult
    public func add(_ link: DropLink, into folder: URL, options: DropOptions, title: String? = nil, formatID: String? = nil) -> UUID {
        if let existing = items.first(where: { $0.link == link && ($0.status == .waiting || $0.isRunning) }) { return existing.id }
        let name = title ?? (link.parts.fileName.isEmpty ? link.host : link.parts.fileName)
        let item = Item(id: UUID(), link: link, folder: folder, options: options, title: name, formatID: formatID, status: .waiting)
        items.append(item)
        batch.append(item.id)
        pump()
        return item.id
    }

    public func stop(_ id: UUID) {
        guard let index = items.firstIndex(where: { $0.id == id }) else { return }
        if let task = tasks[id] {
            stopping.insert(id)
            task.cancel()
        } else if items[index].status == .waiting {
            items[index].status = .stopped
            finishBatchIfIdle()
        }
    }

    public func stopAll() {
        for item in items where item.status == .waiting || item.isRunning { stop(item.id) }
    }

    /// Runs a failed or stopped download again, continuing the partial file when possible.
    public func retry(_ id: UUID) {
        guard let index = items.firstIndex(where: { $0.id == id }), items[index].canRetry else { return }
        items[index].status = .waiting
        if !batch.contains(id) { batch.append(id) }
        pump()
    }

    /// Removes an item that isn't running, and deletes any partial file it kept.
    public func remove(_ id: UUID) {
        guard let index = items.firstIndex(where: { $0.id == id }), !items[index].isRunning else { return }
        resumes.removeValue(forKey: id)?.discard()
        items.remove(at: index)
        batch.removeAll { $0 == id }
        finishBatchIfIdle()
    }

    /// Removes every finished download from the list (the files stay where they were saved).
    public func clearFinished() {
        for item in items where item.status.isDone { remove(item.id) }
    }

    public func move(fromOffsets source: IndexSet, toOffset destination: Int) {
        var moved = items
        let picked = source.sorted().map { moved[$0] }
        for index in source.sorted(by: >) { moved.remove(at: index) }
        let before = source.filter { $0 < destination }.count
        moved.insert(contentsOf: picked, at: destination - before)
        items = moved
    }

    // MARK: - Running

    private func pump() {
        while tasks.count < maxParallel, let next = items.first(where: { $0.status == .waiting }) {
            start(next)
        }
    }

    private func start(_ item: Item) {
        let id = item.id
        set(id) { $0.status = .downloading(DownloadProgress(bytes: 0, total: nil, bytesPerSecond: 0)) }
        let resume = resumes.removeValue(forKey: id)
        let run = self.run
        let owner = WeakQueue(self)
        let relay: @Sendable (DropStage) -> Void = { stage in
            Task { @MainActor in owner.queue?.update(id, stage) }
        }
        tasks[id] = Task { [weak self] in
            do {
                let outcome = try await run(item.link, item.folder, item.options, resume, relay)
                self?.finish(id, outcome: outcome, error: nil)
            } catch {
                self?.finish(id, outcome: nil, error: error)
            }
        }
    }

    private func update(_ id: UUID, _ stage: DropStage) {
        guard let item = items.first(where: { $0.id == id }), item.isRunning, tasks[id] != nil else { return }
        switch stage {
        case .downloading(let progress):
            // Progress arrives per network chunk; ten updates a second per row is plenty.
            let now = Date()
            if let last = lastProgress[id], now.timeIntervalSince(last) < 0.1, progress.bytes != progress.total { return }
            lastProgress[id] = now
            set(id) { $0.status = .downloading(progress) }
        case .inspecting: set(id) { $0.status = .inspecting }
        case .converting(let fraction): set(id) { $0.status = .converting(fraction) }
        case .saving: set(id) { $0.status = .saving }
        }
    }

    private func finish(_ id: UUID, outcome: DropOutcome?, error: Error?) {
        tasks.removeValue(forKey: id)
        lastProgress.removeValue(forKey: id)
        let wasStopped = stopping.remove(id) != nil
        if let outcome {
            set(id) {
                $0.status = .done(outcome.files)
                $0.formatID = outcome.outputFormatID
                $0.canResume = false
            }
        } else {
            var resume: ResumeState?
            let message: String
            switch error {
            case let error as DropError:
                if case .download(_, let state) = error { resume = state }
                message = error.message(registry)
            default:
                message = registry.message(for: .interrupted)
            }
            if let resume { resumes[id] = resume }
            set(id) {
                $0.status = wasStopped ? .stopped : .failed(message)
                $0.canResume = resume != nil
            }
        }
        if let item = items.first(where: { $0.id == id }) { onItemFinished?(item) }
        pump()
        finishBatchIfIdle()
    }

    private func finishBatchIfIdle() {
        guard tasks.isEmpty, !items.contains(where: { $0.status == .waiting }), !batch.isEmpty else { return }
        let current = items.filter { batch.contains($0.id) }
        batch = []
        let done = current.filter { $0.status.isDone }
        let failed = current.filter { if case .failed = $0.status { true } else { false } }
        if !done.isEmpty || !failed.isEmpty { onBatchFinished?(done, failed) }
    }

    private func set(_ id: UUID, _ change: (inout Item) -> Void) {
        guard let index = items.firstIndex(where: { $0.id == id }) else { return }
        change(&items[index])
    }
}

/// Lets progress callbacks reach the queue without keeping it alive.
private final class WeakQueue: @unchecked Sendable {
    weak var queue: DropQueue?
    init(_ queue: DropQueue) { self.queue = queue }
}

extension DropQueue.Item.Status {
    var isDone: Bool { if case .done = self { true } else { false } }
}
