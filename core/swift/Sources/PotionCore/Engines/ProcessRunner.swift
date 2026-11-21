import Foundation

/// Output of a finished child process.
struct ProcessResult: Sendable {
    let status: Int32
    let stdout: Data
    /// The last part of stderr (capped), for error mapping and logs.
    let stderr: String
}

/// Runs a program with an argument array (never through a shell), streams stdout line by line,
/// and terminates the process when the calling task is cancelled.
enum ProcessRunner {
    static let stderrLimit = 64 * 1024

    static func run(_ executable: URL, _ arguments: [String],
                    onStdoutLine: (@Sendable (String) -> Void)? = nil) async throws -> ProcessResult {
        try Task.checkCancellation()
        let process = Process()
        process.executableURL = executable
        process.arguments = arguments
        process.standardInput = FileHandle.nullDevice
        let outPipe = Pipe(), errPipe = Pipe()
        process.standardOutput = outPipe
        process.standardError = errPipe
        let collector = OutputCollector(onLine: onStdoutLine)

        outPipe.fileHandleForReading.readabilityHandler = { handle in
            let data = handle.availableData
            if !data.isEmpty { collector.appendStdout(data) }
        }
        errPipe.fileHandleForReading.readabilityHandler = { handle in
            let data = handle.availableData
            if !data.isEmpty { collector.appendStderr(data) }
        }

        let status: Int32 = try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Int32, Error>) in
                process.terminationHandler = { p in continuation.resume(returning: p.terminationStatus) }
                do { try process.run() } catch {
                    process.terminationHandler = nil
                    continuation.resume(throwing: error)
                }
            }
        } onCancel: {
            if process.isRunning { process.terminate() }
        }

        // Drain anything still buffered after exit.
        outPipe.fileHandleForReading.readabilityHandler = nil
        errPipe.fileHandleForReading.readabilityHandler = nil
        collector.appendStdout(outPipe.fileHandleForReading.readDataToEndOfFile())
        collector.appendStderr(errPipe.fileHandleForReading.readDataToEndOfFile())
        collector.finish()

        try Task.checkCancellation()
        return ProcessResult(status: status, stdout: collector.stdout, stderr: collector.stderrText)
    }
}

/// Thread-safe accumulation of a child's output.
private final class OutputCollector: @unchecked Sendable {
    private let lock = NSLock()
    private var out = Data()
    private var err = Data()
    private var pendingLine = Data()
    private let onLine: (@Sendable (String) -> Void)?

    init(onLine: (@Sendable (String) -> Void)?) { self.onLine = onLine }

    func appendStdout(_ data: Data) {
        guard !data.isEmpty else { return }
        var lines: [String] = []
        lock.lock()
        out.append(data)
        if onLine != nil {
            pendingLine.append(data)
            while let newline = pendingLine.firstIndex(of: 0x0A) {
                lines.append(String(decoding: pendingLine[pendingLine.startIndex..<newline], as: UTF8.self))
                pendingLine.removeSubrange(pendingLine.startIndex...newline)
            }
        }
        lock.unlock()
        lines.forEach { onLine?($0) }
    }

    func appendStderr(_ data: Data) {
        guard !data.isEmpty else { return }
        lock.lock()
        err.append(data)
        if err.count > ProcessRunner.stderrLimit { err = err.suffix(ProcessRunner.stderrLimit) }
        lock.unlock()
    }

    func finish() {
        lock.lock()
        let rest = pendingLine
        pendingLine = Data()
        lock.unlock()
        if !rest.isEmpty { onLine?(String(decoding: rest, as: UTF8.self)) }
    }

    var stdout: Data { lock.lock(); defer { lock.unlock() }; return out }
    var stderrText: String { lock.lock(); defer { lock.unlock() }; return String(decoding: err, as: UTF8.self) }
}
