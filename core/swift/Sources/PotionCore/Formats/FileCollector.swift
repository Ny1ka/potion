import Foundation

/// Turns files and folders the user dropped or chose into a list of convertible files.
/// Folders are expanded (optionally with subfolders); hidden files, packages (apps, bundles) and Potion's own
/// temporary files are skipped; duplicates are dropped; order follows the input, then Finder-style name order
/// inside folders.
public struct FileCollector: Sendable {
    public struct File: Equatable, Sendable {
        public let url: URL
        public let format: FileFormat
        /// Size in bytes, if known.
        public let size: Int64?
    }

    public struct Result: Equatable, Sendable {
        public var files: [File] = []
        /// Files whose type is unknown or that can't be converted to anything.
        public var unsupported: [URL] = []
        /// True when more files were found than `limit`; the rest were left out.
        public var truncated = false
    }

    let registry: FormatRegistry
    let matrix: ConversionMatrix
    /// The most files one drop adds, so dropping a huge folder can't flood the queue.
    public let limit: Int

    public init(registry: FormatRegistry = .shared, matrix: ConversionMatrix = .shared, limit: Int = 5_000) {
        self.registry = registry
        self.matrix = matrix
        self.limit = limit
    }

    public func collect(_ urls: [URL], includeSubfolders: Bool, excluding existing: Set<URL> = [],
                        fileManager: FileManager = .default) -> Result {
        var result = Result()
        var seen = Set(existing.map(\.standardizedFileURL))
        for url in urls {
            for file in expand(url, includeSubfolders: includeSubfolders, fileManager: fileManager) {
                let key = file.standardizedFileURL
                guard seen.insert(key).inserted else { continue }
                guard result.files.count < limit else { result.truncated = true; return result }
                if let format = (try? registry.detect(fileAt: key))?.format, !matrix.outputs(for: format.id).isEmpty {
                    let size = (try? key.resourceValues(forKeys: [.fileSizeKey]).fileSize).map(Int64.init)
                    result.files.append(File(url: key, format: format, size: size))
                } else {
                    result.unsupported.append(key)
                }
            }
        }
        return result
    }

    /// Regular files at or under `url`.
    private func expand(_ url: URL, includeSubfolders: Bool, fileManager: FileManager) -> [URL] {
        let keys: Set<URLResourceKey> = [.isDirectoryKey, .isPackageKey, .isHiddenKey, .isRegularFileKey]
        guard let values = try? url.resourceValues(forKeys: keys) else { return [] }
        if values.isPackage == true { return [] }
        if values.isDirectory != true { return values.isRegularFile == true && !Self.isTemporary(url) ? [url] : [] }

        var options: FileManager.DirectoryEnumerationOptions = [.skipsHiddenFiles, .skipsPackageDescendants]
        if !includeSubfolders { options.insert(.skipsSubdirectoryDescendants) }
        guard let enumerator = fileManager.enumerator(at: url, includingPropertiesForKeys: Array(keys), options: options) else { return [] }
        var files: [URL] = []
        for case let child as URL in enumerator {
            guard let v = try? child.resourceValues(forKeys: keys), v.isRegularFile == true, v.isPackage != true,
                  !Self.isTemporary(child) else { continue }
            files.append(child)
        }
        return files.sorted { $0.path.localizedStandardCompare($1.path) == .orderedAscending }
    }

    private static func isTemporary(_ url: URL) -> Bool { url.lastPathComponent.hasPrefix(".potion-") }
}
