#if DEBUG
import Foundation

// External sources: a generic, Debug-only extension point (Release builds compile none of this, and the Release
// artifact scan fails if any of it is found). An external source lives outside this repository, in its own folder
// under `ExternalSources.defaultFolder`:
//
//   <folder>/<name>/source.json   {"version": 1, "id": "…", "name": "…", "hosts": ["…"],
//                                  "executable": "…", "arguments": ["…"]}
//
// Potion Dropper runs `<executable> <arguments…> download <link> <output folder>` (an argument list, never a shell).
// The program prints JSON lines on stdout: {"progress": 0.4} (optional), {"title": "…"} (optional) and finally
// {"file": "<path inside the output folder>"}; it exits with 0, or non-zero with a message as the last stderr line.
// The downloaded file then goes through the same content check, duration limit and conversion as any download.

/// A source added outside this repository (Debug builds only).
public protocol ExternalSourceAdapter: Sendable {
    var id: String { get }
    var name: String { get }
    func handles(_ link: DropLink) -> Bool
    func download(_ link: DropLink, into folder: URL, progress: @escaping @Sendable (Double) -> Void) async throws -> ExternalDownload
}

public struct ExternalDownload: Sendable {
    public let file: URL
    public let title: String?
}

/// Why an external source failed, in its own words (shown in the queue row).
public struct ExternalSourceError: Error, Equatable, Sendable {
    public let message: String
}

public enum ExternalSources {
    /// ~/Library/Application Support/Potion Dropper/External Sources
    public static var defaultFolder: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Potion Dropper/External Sources", isDirectory: true)
    }

    /// Every valid source in `folder`; folders without a readable `source.json` are skipped.
    public static func load(from folder: URL = defaultFolder) -> [any ExternalSourceAdapter] {
        let children = (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil)) ?? []
        return children.sorted { $0.lastPathComponent < $1.lastPathComponent }.compactMap { child in
            guard let data = try? Data(contentsOf: child.appendingPathComponent("source.json")),
                  let manifest = try? JSONDecoder().decode(ProcessSourceAdapter.Manifest.self, from: data),
                  manifest.version == 1, !manifest.hosts.isEmpty else { return nil }
            return ProcessSourceAdapter(manifest: manifest, folder: child)
        }
    }
}

/// An external source that runs a program (see the comment at the top of this file).
struct ProcessSourceAdapter: ExternalSourceAdapter {
    struct Manifest: Decodable, Sendable {
        let version: Int
        let id: String
        let name: String
        let hosts: [String]
        let executable: String
        let arguments: [String]?
    }

    let manifest: Manifest
    let folder: URL

    var id: String { manifest.id }
    var name: String { manifest.name }

    func handles(_ link: DropLink) -> Bool {
        let host = link.host
        return manifest.hosts.contains { host == $0.lowercased() || host.hasSuffix("." + $0.lowercased()) }
    }

    /// Absolute paths as given; a path with a slash is relative to the source's folder; a bare name is found on PATH.
    var executableURL: URL {
        let path = manifest.executable
        if path.hasPrefix("/") { return URL(fileURLWithPath: path) }
        if path.contains("/") { return folder.appendingPathComponent(path) }
        let paths = (ProcessInfo.processInfo.environment["PATH"] ?? "/usr/bin:/bin").split(separator: ":")
        for dir in paths {
            let candidate = URL(fileURLWithPath: String(dir)).appendingPathComponent(path)
            if FileManager.default.isExecutableFile(atPath: candidate.path) { return candidate }
        }
        return URL(fileURLWithPath: "/usr/bin").appendingPathComponent(path)
    }

    func download(_ link: DropLink, into output: URL, progress: @escaping @Sendable (Double) -> Void) async throws -> ExternalDownload {
        let state = Lines()
        let result = try await ProcessRunner.run(executableURL, (manifest.arguments ?? []) + ["download", link.string, output.path]) { line in
            guard let data = line.data(using: .utf8),
                  let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return }
            if let fraction = object["progress"] as? Double { progress(min(max(fraction, 0), 1)) }
            if let title = object["title"] as? String { state.set(title: title) }
            if let file = object["file"] as? String { state.set(file: file) }
        }
        guard result.status == 0 else {
            let last = result.stderr.split(whereSeparator: \.isNewline).last.map(String.init)?.trimmingCharacters(in: .whitespaces)
            throw ExternalSourceError(message: String((last?.isEmpty == false ? last! : "\(name) stopped with an error.").prefix(200)))
        }
        guard let path = state.file else { throw ExternalSourceError(message: "\(name) didn't name a downloaded file.") }
        let file = URL(fileURLWithPath: path).standardizedFileURL
        guard file.path.hasPrefix(output.standardizedFileURL.path + "/"), FileManager.default.fileExists(atPath: file.path) else {
            throw ExternalSourceError(message: "\(name) didn't save its file in the download folder.")
        }
        return ExternalDownload(file: file, title: state.title)
    }

    private final class Lines: @unchecked Sendable {
        private let lock = NSLock()
        private var _title: String?
        private var _file: String?
        var title: String? { lock.withLock { _title } }
        var file: String? { lock.withLock { _file } }
        func set(title: String) { lock.withLock { _title = title } }
        func set(file: String) { lock.withLock { _file = file } }
    }
}
#endif
