import Foundation

/// Inspects and converts downloaded media. `ConversionService` is the real one; tests use a fake.
public protocol MediaConverting: Sendable {
    func probe(_ file: URL) async throws -> MediaInfo
    func convert(_ input: URL, from inputFormat: FileFormat, to outputFormat: FileFormat, outputDirectory: URL,
                 settings: ConversionSettings, progress: @escaping @Sendable (Double) -> Void) async throws -> [URL]
}

extension ConversionService: MediaConverting {
    public func probe(_ file: URL) async throws -> MediaInfo {
        guard let tools else { throw ConversionError.toolsMissing }
        return try await MediaProbe.probe(file, tools: tools)
    }
}

/// File names for finished downloads (core/spec/README.md, "Naming").
public enum DropNaming {
    /// Renders a naming template: `{title}`, `{site}`, `{date}` (YYYY-MM-DD) and `{format}`, case-insensitive;
    /// other `{…}` text stays as typed. The result is cleaned like any file name; if nothing usable is left, the
    /// title is used, then `fallback`.
    public static func render(_ template: String, fileName: String, host: String, date: String, formatName: String,
                              maxLength: Int, fallback: String, formats: FormatRegistry = .shared) -> String {
        let title = stem(fileName, formats: formats)
        let site = host.lowercased().hasPrefix("www.") ? String(host.lowercased().dropFirst(4)) : host.lowercased()
        let values = ["title": title, "site": site, "date": date, "format": formatName]
        var out = ""
        var rest = template[...]
        while let open = rest.firstIndex(of: "{") {
            out += rest[..<open]
            guard let close = rest[open...].firstIndex(of: "}") else { break }
            let token = rest[rest.index(after: open)..<close]
            out += values[token.lowercased()] ?? String(rest[open...close])
            rest = rest[rest.index(after: close)...]
        }
        out += rest
        return DownloadRules.sanitize(out, maxLength: maxLength) ?? DownloadRules.sanitize(title, maxLength: maxLength) ?? fallback
    }

    /// The file name without an extension that names a known format ("clip.mp4" → "clip", "My.Talk" stays).
    static func stem(_ fileName: String, formats: FormatRegistry) -> String {
        guard let dot = fileName.lastIndex(of: "."), dot != fileName.startIndex else { return fileName }
        let ext = String(fileName[fileName.index(after: dot)...])
        return !ext.isEmpty && formats.format(forExtension: ext) != nil ? String(fileName[..<dot]) : fileName
    }
}

/// What to do with one link.
public struct DropOptions: Sendable, Equatable {
    /// The format to convert to, or nil to keep the downloaded format.
    public var outputFormatID: String?
    public var settings: ConversionSettings
    public var nameTemplate: String
    public var maxBytes: Int64
    /// Nil means no limit.
    public var maxDurationMinutes: Int?
    /// Keep the downloaded original next to the converted file.
    public var keepSource: Bool

    public init(outputFormatID: String? = nil, settings: ConversionSettings = ConversionSettings(),
                nameTemplate: String = SourceRegistry.shared.naming.defaultTemplate,
                maxBytes: Int64 = SourceRegistry.shared.download.defaultMaxBytes,
                maxDurationMinutes: Int? = SourceRegistry.shared.download.defaultMaxDurationMinutes, keepSource: Bool = false) {
        self.outputFormatID = outputFormatID
        self.settings = settings
        self.nameTemplate = nameTemplate
        self.maxBytes = maxBytes
        self.maxDurationMinutes = maxDurationMinutes
        self.keepSource = keepSource
    }
}

/// Where a job is.
public enum DropStage: Equatable, Sendable {
    case downloading(DownloadProgress)
    case inspecting
    case converting(Double)
    case saving
}

/// Why a job failed.
public enum DropError: Error, Sendable {
    /// Downloading failed; `resume` continues it when the server allows.
    case download(DownloadError, resume: ResumeState?)
    case tooLong(minutes: Int, limit: Int)
    case conversion(ConversionError)
    #if DEBUG
    /// An external source (Debug builds only) failed; its own message.
    case external(String)
    #endif

    /// The text shown for this error.
    public func message(_ registry: SourceRegistry = .shared) -> String {
        switch self {
        case .download(let error, _): registry.message(for: error)
        case .tooLong(let minutes, let limit):
            registry.message(id: "tooLong").replacingOccurrences(of: "{minutes}", with: "\(minutes)")
                .replacingOccurrences(of: "{limit}", with: "\(limit)")
        case .conversion(let error): error.message(product: "Potion Dropper")
        #if DEBUG
        case .external(let message): message
        #endif
        }
    }
}

/// A finished job.
public struct DropOutcome: Sendable {
    /// Saved files: the converted file (or the download itself), plus the original when it was kept.
    public let files: [URL]
    public let sourceFormatID: String
    public let outputFormatID: String
    public let bytes: Int64
    public let finalLink: DropLink
}

/// Runs one link through download → inspect (duration limit, copy protection) → convert → save
/// (core/spec/README.md, "Jobs"). Work happens in a hidden folder inside the output folder, so the final move is
/// on the same volume; nothing is overwritten. core/dotnet's `DropJobRunner` behaves the same.
public final class DropJobRunner: Sendable {
    private let downloader: Downloader
    private let converter: any MediaConverting
    private let registry: SourceRegistry
    private let today: @Sendable () -> String
    #if DEBUG
    private let externalSources: [any ExternalSourceAdapter]
    #endif

    public init(downloader: Downloader, converter: any MediaConverting, registry: SourceRegistry = .shared,
                today: @escaping @Sendable () -> String = DropJobRunner.localDate) {
        self.downloader = downloader
        self.converter = converter
        self.registry = registry
        self.today = today
        #if DEBUG
        externalSources = []
        #endif
    }

    #if DEBUG
    /// Debug builds only: links an external source handles are downloaded by it instead of the Downloader.
    public init(downloader: Downloader, converter: any MediaConverting, externalSources: [any ExternalSourceAdapter],
                registry: SourceRegistry = .shared, today: @escaping @Sendable () -> String = DropJobRunner.localDate) {
        self.downloader = downloader
        self.converter = converter
        self.registry = registry
        self.today = today
        self.externalSources = externalSources
    }

    private func externalDownload(_ source: any ExternalSourceAdapter, link: DropLink, work: URL,
                                  stage: @escaping @Sendable (DropStage) -> Void) async throws -> DownloadResult {
        stage(.downloading(DownloadProgress(bytes: 0, total: nil, bytesPerSecond: 0)))
        let result: ExternalDownload
        do {
            result = try await source.download(link, into: work) { _ in }
        } catch let error as ExternalSourceError {
            throw DropError.external(error.message)
        } catch {
            throw DropError.download(.interrupted, resume: nil)
        }
        let detection = (try? registry.formats.detect(fileAt: result.file))?.format
        guard let format = detection, format.category == .audio || format.category == .video else {
            throw DropError.download(.link(.notMedia), resume: nil)
        }
        let size = (try? result.file.resourceValues(forKeys: [.fileSizeKey]).fileSize).map(Int64.init) ?? 0
        let name = result.title.flatMap { DownloadRules.sanitize($0, maxLength: registry.download.maxFileNameLength) }
            .map { "\($0).\(format.preferredExtension)" } ?? result.file.lastPathComponent
        return DownloadResult(file: result.file, formatID: format.id, fileName: name, bytes: size, finalLink: link)
    }
    #endif

    @Sendable public static func localDate() -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyy-MM-dd"
        return formatter.string(from: Date())
    }

    public func run(_ link: DropLink, into folder: URL, options: DropOptions, resume: ResumeState? = nil,
                    stage: @escaping @Sendable (DropStage) -> Void = { _ in }) async throws -> DropOutcome {
        let fm = FileManager.default
        let work = resume?.file.deletingLastPathComponent() ?? folder.appendingPathComponent(".potion-dropper-\(UUID().uuidString)")
        do { try fm.createDirectory(at: work, withIntermediateDirectories: true) } catch {
            throw DropError.conversion(.cannotWrite(folder: folder.lastPathComponent))
        }
        var keepWork = false
        defer { if !keepWork { try? fm.removeItem(at: work) } }

        func fromNetwork() async throws -> DownloadResult {
            do {
                return try await downloader.download(link, into: work, maxBytes: options.maxBytes, resume: resume,
                                                     progress: { stage(.downloading($0)) })
            } catch let failure as DownloadFailure {
                keepWork = failure.resume != nil
                throw DropError.download(failure.error, resume: failure.resume)
            }
        }
        let download: DownloadResult
        #if DEBUG
        if let external = externalSources.first(where: { $0.handles(link) }) {
            download = try await externalDownload(external, link: link, work: work, stage: stage)
        } else {
            download = try await fromNetwork()
        }
        #else
        download = try await fromNetwork()
        #endif

        guard let source = registry.formats.format(id: download.formatID) else { throw DropError.download(.link(.notMedia), resume: nil) }
        let target = options.outputFormatID.flatMap { registry.formats.format(id: $0) } ?? source
        stage(.inspecting)
        do {
            let info = try await converter.probe(download.file)
            if info.isCopyProtected { throw ConversionError.copyProtected }
            if let limit = options.maxDurationMinutes, let seconds = info.duration, seconds > Double(limit * 60) {
                throw DropError.tooLong(minutes: Int((seconds / 60).rounded(.up)), limit: limit)
            }
            let baseName = DropNaming.render(options.nameTemplate, fileName: download.fileName, host: download.finalLink.host,
                                             date: today(), formatName: target.name,
                                             maxLength: registry.download.maxFileNameLength - registry.naming.reserveForSuffix,
                                             fallback: registry.naming.fallbackName, formats: registry.formats)
            if target.id == source.id {
                stage(.saving)
                let saved = try OutputFiles.finalize(download.file, in: folder, baseName: baseName, fileExtension: source.preferredExtension)
                return DropOutcome(files: [saved], sourceFormatID: source.id, outputFormatID: source.id, bytes: download.bytes, finalLink: download.finalLink)
            }
            // The engines name their output after the input, so the download gets its final name first.
            let named = work.appendingPathComponent(baseName).appendingPathExtension(source.preferredExtension)
            try fm.moveItem(at: download.file, to: named)
            stage(.converting(0))
            var files = try await converter.convert(named, from: source, to: target, outputDirectory: folder,
                                                    settings: options.settings, progress: { stage(.converting($0)) })
            if options.keepSource {
                stage(.saving)
                files.append(try OutputFiles.finalize(named, in: folder, baseName: baseName, fileExtension: source.preferredExtension))
            }
            return DropOutcome(files: files, sourceFormatID: source.id, outputFormatID: target.id, bytes: download.bytes, finalLink: download.finalLink)
        } catch let error as DropError {
            throw error
        } catch let error as ConversionError {
            throw DropError.conversion(error)
        } catch is CancellationError {
            throw DropError.download(.interrupted, resume: nil)
        } catch {
            throw DropError.conversion(.cannotWrite(folder: folder.lastPathComponent))
        }
    }
}

/// Download history the user can see and clear (spec Part 6.1.4: nothing kept beyond that). Stored as JSON.
public final class DropHistory: @unchecked Sendable {
    public struct Entry: Codable, Equatable, Sendable, Identifiable {
        public var id: UUID
        public var date: Date
        public var link: String
        public var site: String
        public var name: String
        public var files: [String]
        public var formatID: String?
        public var bytes: Int64?
        /// Nil when the download succeeded; otherwise the error id.
        public var error: String?

        public init(id: UUID = UUID(), date: Date = Date(), link: String, site: String, name: String, files: [String] = [],
                    formatID: String? = nil, bytes: Int64? = nil, error: String? = nil) {
            self.id = id; self.date = date; self.link = link; self.site = site; self.name = name
            self.files = files; self.formatID = formatID; self.bytes = bytes; self.error = error
        }
    }

    private struct FileContents: Codable { var version = 1; var entries: [Entry] }

    public static let maxEntries = 500
    private let url: URL
    private let lock = NSLock()
    private var _entries: [Entry]

    /// Newest first.
    public var entries: [Entry] { lock.withLock { _entries } }

    /// Loads the history at `url` (a missing or unreadable file is an empty history).
    public init(url: URL) {
        self.url = url
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        _entries = (try? decoder.decode(FileContents.self, from: Data(contentsOf: url)).entries) ?? []
    }

    public func add(_ entry: Entry) throws {
        try lock.withLock {
            _entries.insert(entry, at: 0)
            if _entries.count > Self.maxEntries { _entries.removeLast(_entries.count - Self.maxEntries) }
            try save()
        }
    }

    public func remove(_ id: UUID) throws {
        try lock.withLock {
            _entries.removeAll { $0.id == id }
            try save()
        }
    }

    /// Forgets everything and deletes the file. Downloaded files are never touched.
    public func clear() throws {
        try lock.withLock {
            _entries = []
            if FileManager.default.fileExists(atPath: url.path) { try FileManager.default.removeItem(at: url) }
        }
    }

    private func save() throws {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try encoder.encode(FileContents(entries: _entries)).write(to: url, options: .atomic)
    }
}

/// Video or audio output (the segmented control on the media card).
public enum DropMode: String, CaseIterable, Sendable {
    case video, audio

    var category: FormatCategory { self == .video ? .video : .audio }
}

/// What the media card offers for a downloaded format (core/spec/README.md, "Output choices").
public enum DropChoices {
    /// Video sources can be saved as video or audio; audio sources only as audio.
    public static func modes(for source: FileFormat) -> [DropMode] {
        source.category == .video ? [.video, .audio] : source.category == .audio ? [.audio] : []
    }

    /// Formats offered in a mode: the source's own format first when it fits the mode ("keep as downloaded"), then
    /// every format of that kind the matrix can convert it to, in formats.json order.
    public static func formats(for source: FileFormat, mode: DropMode, matrix: ConversionMatrix = .shared) -> [FileFormat] {
        guard modes(for: source).contains(mode) else { return [] }
        let converted = matrix.outputs(for: source.id).map(\.output).filter { $0.category == mode.category && $0.id != source.id }
        return (source.category == mode.category ? [source] : []) + converted
    }

    /// The preselected format: the source's own when it fits, otherwise MP4 (video) or MP3 (audio) when offered,
    /// otherwise the first one offered.
    public static func defaultFormat(for source: FileFormat, mode: DropMode, matrix: ConversionMatrix = .shared) -> FileFormat? {
        let offered = formats(for: source, mode: mode, matrix: matrix)
        if offered.first?.id == source.id { return offered.first }
        let preferred = mode == .video ? "mp4" : "mp3"
        return offered.first { $0.id == preferred } ?? offered.first
    }
}
