import Foundation

/// A GET request. Transports never add cookies or credentials and never follow redirects themselves.
public struct HTTPRequest: Hashable, Sendable {
    public let url: String
    public let headers: [String: String]
}

/// A response: status and headers first, then the body in chunks.
public struct HTTPResponse: Sendable {
    public let status: Int
    /// Header names as the server sent them (matched without regard to case).
    public let headers: [String: String]
    public let body: AsyncThrowingStream<Data, Error>

    public init(status: Int, headers: [String: String], body: AsyncThrowingStream<Data, Error>) {
        self.status = status
        self.headers = headers
        self.body = body
    }

    public func header(_ name: String) -> String? {
        headers.first { $0.key.caseInsensitiveCompare(name) == .orderedSame }?.value
    }
}

/// Transport failures, before or during a response.
public enum TransportError: Error, Equatable, Sendable {
    case network, timedOut
}

/// Sends one GET and hands back the response. The app uses URLSession; tests script the answers.
public protocol HTTPTransport: Sendable {
    func get(_ request: HTTPRequest) async throws -> HTTPResponse
}

/// Bytes so far, the expected total when the server says, and the average speed of this attempt.
public struct DownloadProgress: Equatable, Sendable {
    public let bytes: Int64
    public let total: Int64?
    public let bytesPerSecond: Double
}

/// A partial download that can continue where it stopped (the server must send a validator for that).
public struct ResumeState: Hashable, Sendable {
    /// The hidden partial file.
    public let file: URL
    public let bytes: Int64
    /// ETag (strong) or Last-Modified, sent back as If-Range.
    public let validator: String
    /// The link the user asked for (redirects are followed again, since signed redirect targets expire).
    public let link: DropLink

    /// Deletes the partial file when the download won't be resumed.
    public func discard() { try? FileManager.default.removeItem(at: file) }
}

/// A failed download, with what's needed to resume it when that's possible.
public struct DownloadFailure: Error, Sendable {
    public let error: DownloadError
    public let resume: ResumeState?
}

/// What the media card shows before downloading.
public struct LinkPreview: Sendable {
    /// Suggested name from the server or the link.
    public let fileName: String
    /// The audio or video format found in the first bytes.
    public let formatID: String
    /// Bytes, when the server says.
    public let size: Int64?
    /// The link that answered, after redirects.
    public let finalLink: DropLink
    /// Whether an interrupted download could continue (the server sends a validator).
    public let resumable: Bool
}

/// A finished, checked download in a hidden file. The caller converts it or moves it to a unique name.
public struct DownloadResult: Sendable {
    public let file: URL
    /// The audio or video format found in the file's content.
    public let formatID: String
    /// Suggested name from the server or the link (sanitized; its extension may not match `formatID`).
    public let fileName: String
    public let bytes: Int64
    /// The link that served the file, after redirects.
    public let finalLink: DropLink
}

/// Downloads one allowlisted link (core/spec/README.md, "Downloads"): robots.txt per site, redirects re-checked
/// against the allowlist, retries with Retry-After, the size limit while streaming, the content check, resume
/// with Range/If-Range, and a hidden temporary file. core/dotnet's `Downloader` behaves the same.
public final class Downloader: Sendable {
    public typealias Sleep = @Sendable (Int) async throws -> Void

    private let registry: SourceRegistry
    private let transport: any HTTPTransport
    private let userAgent: String
    private let sleep: Sleep
    private let robots = RobotsCache()

    public init(transport: any HTTPTransport, appVersion: String, registry: SourceRegistry = .shared,
                sleep: @escaping Sleep = { try await Task.sleep(nanoseconds: UInt64(max(0, $0)) * 1_000_000_000) }) {
        self.registry = registry
        self.transport = transport
        self.userAgent = registry.userAgent(version: appVersion)
        self.sleep = sleep
    }

    private var settings: DownloadSettings { registry.download }

    private var baseHeaders: [String: String] {
        ["User-Agent": userAgent, "Accept": "*/*", "Accept-Encoding": "identity"]
    }

    /// Downloads `link` into a hidden file in `folder`. Throws `DownloadFailure`; cancelling the task keeps the
    /// partial file for resuming when the server allows it.
    public func download(_ link: DropLink, into folder: URL, maxBytes: Int64, resume: ResumeState? = nil,
                         progress: @escaping @Sendable (DownloadProgress) -> Void = { _ in }) async throws -> DownloadResult {
        let opened = try await open(link, maxBytes: maxBytes, resume: resume)
        return try await receive(opened.response, link: link, current: opened.current, folder: folder, maxBytes: maxBytes,
                                 length: opened.length, resumeFrom: opened.resumed ? opened.partial : nil, discard: opened.partial,
                                 progress: progress)
    }

    /// Fetches what the media card shows without downloading the file: the same checks as `download` (robots.txt,
    /// redirects, server answer, size limit, content check on the first bytes), then the connection is closed.
    public func inspect(_ link: DropLink, maxBytes: Int64) async throws -> LinkPreview {
        let opened = try await open(link, maxBytes: maxBytes, resume: nil)
        let response = opened.response
        let fileName = DownloadRules.fileName(contentDisposition: response.header("Content-Disposition"), link: opened.current.parts,
                                              maxLength: settings.maxFileNameLength)
        let headerSize = registry.formatsHeaderSize
        var header = Data()
        do {
            for try await chunk in response.body {
                header.append(chunk.prefix(headerSize - header.count))
                if header.count >= headerSize { break }   // stops the transfer
            }
        } catch {
            if Task.isCancelled || error is CancellationError { throw DownloadFailure(error: .interrupted, resume: nil) }
            if header.isEmpty { throw DownloadFailure(error: Self.map(error), resume: nil) }
        }
        switch DownloadRules.sniff(header: header, fileName: fileName, formats: registry.formats) {
        case .failure(let error): throw DownloadFailure(error: error, resume: nil)
        case .success(let formatID):
            return LinkPreview(fileName: fileName, formatID: formatID, size: opened.length, finalLink: opened.current,
                               resumable: Self.validator(response) != nil)
        }
    }

    private struct Opened {
        let response: HTTPResponse
        let current: DropLink
        let length: Int64?
        let resumed: Bool
        let partial: ResumeState?
    }

    /// Follows the link to an accepted answer: robots.txt, redirects re-checked against the allowlist, retries,
    /// and resume restarts.
    private func open(_ link: DropLink, maxBytes: Int64, resume: ResumeState?) async throws -> Opened {
        var current = link
        var partial = resume
        var redirects = 0
        var retries = 0
        func fail(_ error: DownloadError) -> DownloadFailure {
            partial?.discard()
            return DownloadFailure(error: error, resume: nil)
        }

        while true {
            if Task.isCancelled { throw DownloadFailure(error: .interrupted, resume: partial) }
            do { try await checkRobots(current) } catch let error as DownloadError { throw fail(error) }

            var headers = baseHeaders
            if let partial, partial.bytes > 0 {
                headers["Range"] = "bytes=\(partial.bytes)-"
                headers["If-Range"] = partial.validator
            }
            let response: HTTPResponse
            do {
                response = try await transport.get(HTTPRequest(url: current.string, headers: headers))
            } catch {
                if Task.isCancelled || error is CancellationError { throw DownloadFailure(error: .interrupted, resume: partial) }
                throw DownloadFailure(error: Self.map(error), resume: partial)
            }

            let verdict = DownloadRules.classify(status: response.status, headers: response.headers,
                                                 rangeRequested: headers["Range"] != nil, maxBytes: maxBytes, settings: settings)
            switch verdict {
            case .redirect(let location):
                redirects += 1
                guard redirects <= settings.maxRedirects else { throw fail(.tooManyRedirects) }
                switch registry.redirect(from: current, to: location) {
                case .success(let target): current = target
                case .failure(let error): throw fail(.link(error))
                }
            case .retry(let seconds):
                retries += 1
                guard retries <= settings.retries else { throw fail(response.status == 429 ? .rateLimited : .serverError) }
                do { try await sleep(seconds) } catch { throw DownloadFailure(error: .interrupted, resume: partial) }
            case .restart:
                partial?.discard()
                partial = nil
                retries += 1
                guard retries <= settings.retries else { throw fail(.refused) }
            case .fail(let error):
                throw fail(error)
            case .accept(_, let length, let resumed):
                return Opened(response: response, current: current, length: length, resumed: resumed, partial: partial)
            }
        }
    }

    private func receive(_ response: HTTPResponse, link: DropLink, current: DropLink, folder: URL, maxBytes: Int64,
                         length: Int64?, resumeFrom: ResumeState?, discard: ResumeState?,
                         progress: @escaping @Sendable (DownloadProgress) -> Void) async throws -> DownloadResult {
        let fileName = DownloadRules.fileName(contentDisposition: response.header("Content-Disposition"), link: current.parts,
                                              maxLength: settings.maxFileNameLength)
        let fm = FileManager.default
        let file: URL
        var received: Int64
        if let resumeFrom {
            file = resumeFrom.file
            received = resumeFrom.bytes
        } else {
            discard?.discard()
            file = folder.appendingPathComponent(".potion-dropper-\(UUID().uuidString).part")
            received = 0
        }
        let validator = Self.validator(response)
        func failure(_ error: DownloadError, keep: Bool) -> DownloadFailure {
            if keep, let validator, received > 0 {
                return DownloadFailure(error: error, resume: ResumeState(file: file, bytes: received, validator: validator, link: link))
            }
            try? fm.removeItem(at: file)
            return DownloadFailure(error: error, resume: nil)
        }

        let total = length.map { $0 + received }
        if let total, total > maxBytes { throw failure(.tooLarge, keep: false) }

        let handle: FileHandle
        var header = Data()
        do {
            if !fm.fileExists(atPath: file.path) { guard fm.createFile(atPath: file.path, contents: nil) else { throw CocoaError(.fileWriteUnknown) } }
            handle = try FileHandle(forUpdating: file)
            if received > 0 {
                header = try handle.read(upToCount: registry.formatsHeaderSize) ?? Data()
            }
            try handle.truncate(atOffset: UInt64(received))
            try handle.seekToEnd()
        } catch {
            throw failure(.interrupted, keep: false)
        }
        defer { try? handle.close() }

        let headerSize = registry.formatsHeaderSize
        var formatID: String?
        func check() throws {
            switch DownloadRules.sniff(header: header, fileName: fileName, formats: registry.formats) {
            case .success(let id): formatID = id
            case .failure(let error): throw failure(error, keep: false)
            }
        }
        if header.count >= headerSize { try check() }

        let started = Date()
        var thisAttempt: Int64 = 0
        do {
            for try await chunk in response.body {
                if Task.isCancelled { throw CancellationError() }
                try handle.write(contentsOf: chunk)
                received += Int64(chunk.count)
                thisAttempt += Int64(chunk.count)
                if received > maxBytes { throw failure(.tooLarge, keep: false) }
                if formatID == nil, header.count < headerSize {
                    header.append(chunk.prefix(headerSize - header.count))
                    if header.count >= headerSize { try check() }
                }
                let elapsed = max(Date().timeIntervalSince(started), 0.001)
                progress(DownloadProgress(bytes: received, total: total, bytesPerSecond: Double(thisAttempt) / elapsed))
            }
        } catch let error as DownloadFailure {
            throw error
        } catch {
            let cancelled = Task.isCancelled || error is CancellationError
            throw failure(cancelled ? .interrupted : (Self.map(error) == .timedOut ? .timedOut : .interrupted), keep: true)
        }

        if let total, received != total { throw failure(.interrupted, keep: true) }
        if formatID == nil { try check() }
        guard let formatID else { throw failure(.link(.notMedia), keep: false) }
        return DownloadResult(file: file, formatID: formatID, fileName: fileName, bytes: received, finalLink: current)
    }

    /// A strong ETag, otherwise Last-Modified: what If-Range accepts.
    private static func validator(_ response: HTTPResponse) -> String? {
        if let etag = response.header("ETag")?.trimmingCharacters(in: .whitespaces), !etag.isEmpty, !etag.hasPrefix("W/") { return etag }
        if let modified = response.header("Last-Modified")?.trimmingCharacters(in: .whitespaces), !modified.isEmpty { return modified }
        return nil
    }

    private static func map(_ error: Error) -> DownloadError {
        if let error = error as? TransportError { return error == .timedOut ? .timedOut : .network }
        if let error = error as? URLError, error.code == .timedOut { return .timedOut }
        return .network
    }

    // MARK: - robots.txt

    private func checkRobots(_ link: DropLink) async throws {
        let parts = link.parts
        let origin = "\(parts.scheme)://\(parts.host)\(parts.port.map { ":\($0)" } ?? "")"
        let rules: RobotsRules
        if let cached = await robots.rules(for: origin) {
            rules = cached
        } else {
            rules = try await fetchRobots(origin: origin)
            await robots.store(rules, for: origin)
        }
        let path = parts.path + (parts.query.map { "?\($0)" } ?? "")
        if !rules.isAllowed(path: path, agent: registry.robotsAgent) { throw DownloadError.robotsDisallowed }
    }

    /// RFC 9309: follow up to five redirects; 2xx is parsed (capped), 4xx allows everything, 5xx means the site is
    /// unavailable (not cached, so a later download asks again).
    private func fetchRobots(origin: String) async throws -> RobotsRules {
        var url = origin + "/robots.txt"
        for _ in 0...5 {
            let response: HTTPResponse
            do { response = try await transport.get(HTTPRequest(url: url, headers: baseHeaders)) } catch { throw Self.map(error) }
            switch response.status {
            case 300..<400:
                guard let location = response.header("Location"), case .success(let base) = LinkSyntax.normalize(url, maxLength: registry.maxLinkLength),
                      case .success(let next) = LinkSyntax.normalize(LinkSyntax.resolve(location, against: base), maxLength: registry.maxLinkLength) else {
                    return .allowAll
                }
                url = next.string
                continue
            default:
                switch RobotsFetch(status: response.status) {
                case .allowAll: return .allowAll
                case .disallowAll: throw DownloadError.serverError
                case .parse:
                    var data = Data()
                    do {
                        for try await chunk in response.body {
                            data.append(chunk.prefix(settings.maxRobotsBytes - data.count))
                            if data.count >= settings.maxRobotsBytes { break }
                        }
                    } catch {
                        throw Self.map(error)
                    }
                    return RobotsRules(text: String(decoding: data, as: UTF8.self))
                }
            }
        }
        return .allowAll   // too many redirects: RFC 9309 treats robots.txt as unavailable (4xx)
    }
}

/// robots.txt rules per site for this session.
private actor RobotsCache {
    private var entries: [String: RobotsRules] = [:]
    func rules(for origin: String) -> RobotsRules? { entries[origin] }
    func store(_ rules: RobotsRules, for origin: String) { entries[origin] = rules }
}
