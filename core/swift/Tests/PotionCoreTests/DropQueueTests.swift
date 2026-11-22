import XCTest
@testable import PotionCore

/// Jobs that wait until the test finishes them, so the queue's scheduling can be checked step by step.
final class ControlledRuns: @unchecked Sendable {
    private let lock = NSLock()
    private var waiting: [String: CheckedContinuation<DropOutcome, Error>] = [:]
    private var _started: [String] = []
    private var _resumes: [String: ResumeState?] = [:]
    var resumeOnCancel: ResumeState?

    var started: [String] { lock.withLock { _started } }
    func resumeSeen(_ link: String) -> ResumeState?? { lock.withLock { _resumes[link] } }
    var running: Int { lock.withLock { waiting.count } }

    func run(_ link: DropLink, _ folder: URL, _ options: DropOptions, _ resume: ResumeState?, _ stage: @escaping @Sendable (DropStage) -> Void) async throws -> DropOutcome {
        lock.withLock {
            _started.append(link.string)
            _resumes[link.string] = resume
        }
        let onCancel = resumeOnCancel
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                lock.withLock { waiting[link.string] = continuation }
            }
        } onCancel: {
            lock.withLock { waiting.removeValue(forKey: link.string) }?.resume(throwing: DropError.download(.interrupted, resume: onCancel))
        }
    }

    func succeed(_ link: String) {
        let outcome = DropOutcome(files: [URL(fileURLWithPath: "/tmp/\(UUID().uuidString).mp4")], sourceFormatID: "mp4", outputFormatID: "mp4",
                                  bytes: 10, finalLink: try! SourceRegistry.shared.link(from: link).get())
        lock.withLock { waiting.removeValue(forKey: link) }?.resume(returning: outcome)
    }

    func fail(_ link: String, _ error: DropError) {
        lock.withLock { waiting.removeValue(forKey: link) }?.resume(throwing: error)
    }
}

final class DropQueueTests: XCTestCase {
    func link(_ n: Int) -> DropLink { try! SourceRegistry.shared.link(from: "https://example.com/\(n).mp4").get() }

    @MainActor
    func make(_ maxParallel: Int = 2) -> (ControlledRuns, DropQueue) {
        let runs = ControlledRuns()
        return (runs, DropQueue(maxParallel: maxParallel) { try await runs.run($0, $1, $2, $3, $4) })
    }

    /// Waits (briefly) for asynchronous queue work to settle.
    @MainActor
    func until(_ condition: () -> Bool, file: StaticString = #filePath, line: UInt = #line) async {
        for _ in 0..<200 where !condition() { try? await Task.sleep(nanoseconds: 10_000_000) }
        XCTAssertTrue(condition(), "timed out", file: file, line: line)
    }

    @MainActor func testRunsAtMostMaxParallelInOrder() async {
        let (runs, queue) = make()
        func status(_ id: UUID) -> DropQueue.Item.Status? { queue.items.first { $0.id == id }?.status }
        let ids = (1...4).map { queue.add(link($0), into: URL(fileURLWithPath: "/tmp"), options: DropOptions()) }
        await until { runs.running == 2 }
        XCTAssertEqual(Set(runs.started), [link(1).string, link(2).string], "the first two start (their jobs may begin in either order)")
        XCTAssertEqual(status(ids[2]), .waiting)

        runs.succeed(link(1).string)
        await until { runs.started.count == 3 }
        XCTAssertEqual(runs.started.last, link(3).string)
        if case .done = status(ids[0]) {} else { XCTFail("first should be done") }

        queue.maxParallel = 3
        await until { runs.started.count == 4 }
    }

    @MainActor func testTheSameLinkIsNotQueuedTwice() {
        let (_, queue) = make()
        let a = queue.add(link(1), into: URL(fileURLWithPath: "/tmp"), options: DropOptions())
        let b = queue.add(link(1), into: URL(fileURLWithPath: "/tmp"), options: DropOptions())
        XCTAssertEqual(a, b)
        XCTAssertEqual(queue.items.count, 1)
    }

    @MainActor func testStopKeepsTheResumeStateAndRetryUsesIt() async throws {
        let (runs, queue) = make()
        func status(_ id: UUID) -> DropQueue.Item.Status? { queue.items.first { $0.id == id }?.status }
        let resume = ResumeState(file: URL(fileURLWithPath: "/tmp/part"), bytes: 500, validator: "\"v\"", link: link(1))
        runs.resumeOnCancel = resume
        let id = queue.add(link(1), into: URL(fileURLWithPath: "/tmp"), options: DropOptions())
        await until { runs.running == 1 }
        queue.stop(id)
        await until { status(id) == .stopped }
        XCTAssertEqual(queue.items.first?.canResume, true)

        queue.retry(id)
        await until { runs.started.count == 2 }
        XCTAssertEqual(runs.resumeSeen(link(1).string), .some(resume))
    }

    @MainActor func testFailureShowsTheMessageAndStopsWaitingItems() async {
        let (runs, queue) = make()
        func status(_ id: UUID) -> DropQueue.Item.Status? { queue.items.first { $0.id == id }?.status }
        let first = queue.add(link(1), into: URL(fileURLWithPath: "/tmp"), options: DropOptions())
        queue.maxParallel = 1
        let second = queue.add(link(2), into: URL(fileURLWithPath: "/tmp"), options: DropOptions())
        await until { runs.running == 1 }
        queue.stop(second)
        XCTAssertEqual(status(second), .stopped)
        runs.fail(link(1).string, .download(.notFound, resume: nil))
        await until { status(first) == .failed("This file isn't on the site anymore. Check the link.") }
        XCTAssertEqual(runs.started, [link(1).string], "a stopped waiting item never starts")
    }

    @MainActor func testBatchCallbackAndOverallProgress() async {
        let (runs, queue) = make()
        var batches: [(Int, Int)] = []
        var finished: [UUID] = []
        queue.onBatchFinished = { done, failed in batches.append((done.count, failed.count)) }
        queue.onItemFinished = { finished.append($0.id) }
        let a = queue.add(link(1), into: URL(fileURLWithPath: "/tmp"), options: DropOptions())
        let b = queue.add(link(2), into: URL(fileURLWithPath: "/tmp"), options: DropOptions())
        await until { runs.running == 2 }
        XCTAssertEqual(queue.overall.total, 2)
        runs.succeed(link(1).string)
        await until { queue.overall.finished == 1 }
        runs.fail(link(2).string, .download(.notFound, resume: nil))
        await until { batches.count == 1 }
        XCTAssertEqual(batches.first?.0, 1)
        XCTAssertEqual(batches.first?.1, 1)
        XCTAssertEqual(finished, [a, b])
        XCTAssertFalse(queue.isBusy)
    }

    @MainActor func testRemoveAndClearFinished() async {
        let (runs, queue) = make()
        let a = queue.add(link(1), into: URL(fileURLWithPath: "/tmp"), options: DropOptions())
        let b = queue.add(link(2), into: URL(fileURLWithPath: "/tmp"), options: DropOptions())
        await until { runs.running == 2 }
        queue.remove(a)
        XCTAssertEqual(queue.items.count, 2, "a running item can't be removed")
        runs.succeed(link(1).string)
        runs.fail(link(2).string, .download(.notFound, resume: nil))
        await until { !queue.isBusy }
        queue.clearFinished()
        XCTAssertEqual(queue.items.map(\.id), [b], "failed items stay so they can be retried")
        queue.remove(b)
        XCTAssertTrue(queue.items.isEmpty)
    }

    @MainActor func testMove() {
        let (_, queue) = make()
        queue.maxParallel = 1
        let ids = (1...3).map { queue.add(link($0), into: URL(fileURLWithPath: "/tmp"), options: DropOptions()) }
        queue.move(fromOffsets: IndexSet(integer: 2), toOffset: 1)
        XCTAssertEqual(queue.items.map(\.id), [ids[0], ids[2], ids[1]])
    }
}
