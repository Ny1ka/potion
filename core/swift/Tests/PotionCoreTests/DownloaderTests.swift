import XCTest
@testable import PotionCore

/// A transport that answers from a script, records every request, and never touches the network.
final class ScriptedTransport: HTTPTransport, @unchecked Sendable {
    struct Reply {
        var status = 200
        var headers: [String: String] = [:]
        var chunks: [Data] = []
        /// Throw this after sending the chunks (a dropped connection).
        var failAfterChunks: TransportError?
        /// Throw this instead of answering.
        var failBeforeAnswer: TransportError?
    }

    private let lock = NSLock()
    private var script: [String: [Reply]] = [:]
    private var _requests: [HTTPRequest] = []

    var requests: [HTTPRequest] { lock.withLock { _requests } }
    func requests(to url: String) -> [HTTPRequest] { requests.filter { $0.url == url } }

    /// Replies for a URL are used in order; the last one repeats. Unscripted URLs get 404.
    func on(_ url: String, _ replies: Reply...) { lock.withLock { script[url, default: []] += replies } }

    func get(_ request: HTTPRequest) async throws -> HTTPResponse {
        let reply: Reply = lock.withLock {
            _requests.append(request)
            guard var queue = script[request.url], !queue.isEmpty else { return Reply(status: 404) }
            let next = queue.count > 1 ? queue.removeFirst() : queue[0]
            script[request.url] = queue
            return next
        }
        if let error = reply.failBeforeAnswer { throw error }
        let body = AsyncThrowingStream<Data, Error> { continuation in
            for chunk in reply.chunks { continuation.yield(chunk) }
            if let error = reply.failAfterChunks { continuation.finish(throwing: error) } else { continuation.finish() }
        }
        return HTTPResponse(status: reply.status, headers: reply.headers, body: body)
    }
}

final class DownloaderTests: XCTestCase {
    var folder: URL!
    var transport: ScriptedTransport!
    var sleeps: SleepLog!

    final class SleepLog: @unchecked Sendable {
        private let lock = NSLock()
        private var _seconds: [Int] = []
        var seconds: [Int] { lock.withLock { _seconds } }
        func add(_ s: Int) { lock.withLock { _seconds.append(s) } }
    }

    override func setUpWithError() throws {
        folder = FileManager.default.temporaryDirectory.appendingPathComponent("potion-dl-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        transport = ScriptedTransport()
        sleeps = SleepLog()
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: folder)
    }

    var downloader: Downloader {
        let log = sleeps!
        return Downloader(transport: transport, appVersion: "1.2.3", sleep: { log.add($0) })
    }

    /// An MP4 header followed by filler, `size` bytes in all.
    static func mp4(_ size: Int) -> Data {
        var data = Data([0x00, 0x00, 0x00, 0x20]) + Data("ftypisom".utf8) + Data([0x00, 0x00, 0x02, 0x00]) + Data("isomiso2avc1mp41".utf8)
        data.append(Data((0..<(size - data.count)).map { UInt8($0 % 251) }))
        return data
    }

    static func chunks(_ slice: Data, size: Int = 1000) -> [Data] {
        let data = Data(slice)   // slices keep their parent's indices
        return stride(from: 0, to: data.count, by: size).map { data.subdata(in: $0..<min($0 + size, data.count)) }
    }

    func link(_ text: String) throws -> DropLink { try SourceRegistry.shared.link(from: text).get() }

    func failure(_ body: () async throws -> DownloadResult) async -> DownloadFailure? {
        do { _ = try await body(); return nil } catch let error as DownloadFailure { return error } catch { XCTFail("\(error)"); return nil }
    }

    func hiddenFiles() throws -> [String] {
        try FileManager.default.contentsOfDirectory(atPath: folder.path)
    }

    // MARK: - Tests

    func testDownloadsCheckedMediaToAHiddenFile() async throws {
        let body = Self.mp4(5000)
        transport.on("https://example.com/v/clip.mp4", .init(headers: ["Content-Type": "video/mp4", "Content-Length": "5000"], chunks: Self.chunks(body)))
        let progress = SleepLog()
        let result = try await downloader.download(link("https://example.com/v/clip.mp4"), into: folder, maxBytes: 1 << 30,
                                                   progress: { progress.add(Int($0.bytes)) })
        XCTAssertEqual(result.formatID, "mp4")
        XCTAssertEqual(result.fileName, "clip.mp4")
        XCTAssertEqual(result.bytes, 5000)
        XCTAssertEqual(try Data(contentsOf: result.file), body)
        XCTAssertTrue(result.file.lastPathComponent.hasPrefix(".potion-dropper-"))
        XCTAssertEqual(progress.seconds.last, 5000)

        let request = try XCTUnwrap(transport.requests(to: "https://example.com/v/clip.mp4").first)
        XCTAssertEqual(request.headers["User-Agent"], "PotionDropper/1.2.3 (+https://github.com/Ny1ka/potion)")
        XCTAssertEqual(request.headers["Accept-Encoding"], "identity")
        XCTAssertNil(request.headers["Cookie"])
        XCTAssertNil(request.headers["Authorization"])
        XCTAssertNil(request.headers["Range"])
        XCTAssertEqual(transport.requests(to: "https://example.com/robots.txt").count, 1)
    }

    func testRobotsDisallowStopsBeforeTheDownload() async throws {
        transport.on("https://example.com/robots.txt", .init(headers: ["Content-Type": "text/plain"], chunks: [Data("User-agent: PotionDropper\nDisallow: /private/\n".utf8)]))
        transport.on("https://example.com/private/a.mp4", .init(headers: ["Content-Type": "video/mp4"], chunks: [Self.mp4(100)]))
        let f = await failure { try await self.downloader.download(self.link("https://example.com/private/a.mp4"), into: self.folder, maxBytes: 1 << 30) }
        XCTAssertEqual(f?.error, .robotsDisallowed)
        XCTAssertTrue(transport.requests(to: "https://example.com/private/a.mp4").isEmpty)
    }

    func testRobotsIsFetchedOncePerSiteAndServerErrorsBlock() async throws {
        transport.on("https://example.com/a.mp4", .init(headers: ["Content-Type": "video/mp4"], chunks: [Self.mp4(100)]))
        transport.on("https://example.com/b.mp4", .init(headers: ["Content-Type": "video/mp4"], chunks: [Self.mp4(100)]))
        let d = downloader
        _ = try await d.download(link("https://example.com/a.mp4"), into: folder, maxBytes: 1 << 30)
        _ = try await d.download(link("https://example.com/b.mp4"), into: folder, maxBytes: 1 << 30)
        XCTAssertEqual(transport.requests(to: "https://example.com/robots.txt").count, 1)

        transport.on("https://down.example.com/robots.txt", .init(status: 503))
        let f = await failure { try await d.download(self.link("https://down.example.com/a.mp4"), into: self.folder, maxBytes: 1 << 30) }
        XCTAssertEqual(f?.error, .serverError)
    }

    func testRedirectsAreFollowedAndCheckedAgain() async throws {
        transport.on("https://example.com/get/7", .init(status: 302, headers: ["Location": "/files/seven.mp3"]))
        transport.on("https://example.com/files/seven.mp3", .init(status: 301, headers: ["Location": "https://cdn.example.net/x/seven.mp3?sig=1"]))
        var mp3 = Data("ID3".utf8) + Data([0x04, 0, 0, 0, 0, 0x0A])
        mp3.append(Data(repeating: 0, count: 200))
        transport.on("https://cdn.example.net/x/seven.mp3?sig=1", .init(headers: ["Content-Type": "audio/mpeg"], chunks: [mp3]))
        let result = try await downloader.download(link("https://example.com/get/7"), into: folder, maxBytes: 1 << 30)
        XCTAssertEqual(result.formatID, "mp3")
        XCTAssertEqual(result.fileName, "seven.mp3")
        XCTAssertEqual(result.finalLink.string, "https://cdn.example.net/x/seven.mp3?sig=1")
        XCTAssertEqual(transport.requests(to: "https://cdn.example.net/robots.txt").count, 1, "robots.txt for the new site")

        transport.on("https://example.com/bad", .init(status: 302, headers: ["Location": "ftp://example.com/a.mp4"]))
        let f = await failure { try await self.downloader.download(self.link("https://example.com/bad"), into: self.folder, maxBytes: 1 << 30) }
        XCTAssertEqual(f?.error, .link(.unsupportedScheme))

        transport.on("https://example.com/loop", .init(status: 302, headers: ["Location": "/loop"]))
        let g = await failure { try await self.downloader.download(self.link("https://example.com/loop"), into: self.folder, maxBytes: 1 << 30) }
        XCTAssertEqual(g?.error, .tooManyRedirects)
    }

    func testRetriesAfterRetryAfter() async throws {
        transport.on("https://example.com/a.mp4",
                     .init(status: 429, headers: ["Retry-After": "2"]),
                     .init(status: 503, headers: ["Retry-After": "1"]),
                     .init(headers: ["Content-Type": "video/mp4"], chunks: [Self.mp4(300)]))
        _ = try await downloader.download(link("https://example.com/a.mp4"), into: folder, maxBytes: 1 << 30)
        XCTAssertEqual(sleeps.seconds, [2, 1])

        transport.on("https://example.com/busy.mp4", .init(status: 429))
        let f = await failure { try await self.downloader.download(self.link("https://example.com/busy.mp4"), into: self.folder, maxBytes: 1 << 30) }
        XCTAssertEqual(f?.error, .rateLimited)
        XCTAssertEqual(transport.requests(to: "https://example.com/busy.mp4").count, 3, "first try plus two retries")
    }

    func testPagesAndNonMediaAreRefusedAndCleanedUp() async throws {
        transport.on("https://example.com/download?id=4", .init(headers: ["Content-Type": "application/octet-stream"],
                                                                 chunks: [Data("<!DOCTYPE html><html><body>Sign in</body></html>".utf8)]))
        let f = await failure { try await self.downloader.download(self.link("https://example.com/download?id=4"), into: self.folder, maxBytes: 1 << 30) }
        XCTAssertEqual(f?.error, .link(.unsupportedSite))

        // Labelled as video, but it's a PNG.
        transport.on("https://example.com/fake.mp4", .init(headers: ["Content-Type": "video/mp4"],
                                                            chunks: [Data([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D])]))
        let g = await failure { try await self.downloader.download(self.link("https://example.com/fake.mp4"), into: self.folder, maxBytes: 1 << 30) }
        XCTAssertEqual(g?.error, .link(.notMedia))
        XCTAssertNil(g?.resume)
        XCTAssertEqual(try hiddenFiles(), [])
    }

    func testSizeLimitBeforeAndDuringTheDownload() async throws {
        transport.on("https://example.com/big.mp4", .init(headers: ["Content-Type": "video/mp4", "Content-Length": "5000"], chunks: Self.chunks(Self.mp4(5000))))
        let f = await failure { try await self.downloader.download(self.link("https://example.com/big.mp4"), into: self.folder, maxBytes: 4000) }
        XCTAssertEqual(f?.error, .tooLarge)

        transport.on("https://example.com/unknown.mp4", .init(headers: ["Content-Type": "video/mp4"], chunks: Self.chunks(Self.mp4(5000))))
        let g = await failure { try await self.downloader.download(self.link("https://example.com/unknown.mp4"), into: self.folder, maxBytes: 4000) }
        XCTAssertEqual(g?.error, .tooLarge)
        XCTAssertEqual(try hiddenFiles(), [])
    }

    func testInterruptedDownloadResumesWithRange() async throws {
        let body = Self.mp4(6000)
        let url = "https://example.com/r.mp4"
        transport.on(url,
                     .init(headers: ["Content-Type": "video/mp4", "Content-Length": "6000", "ETag": "\"v1\""],
                           chunks: Self.chunks(body.prefix(2500)), failAfterChunks: .network),
                     .init(status: 206, headers: ["Content-Type": "video/mp4", "Content-Length": "3500", "Content-Range": "bytes 2500-5999/6000"],
                           chunks: Self.chunks(body.suffix(from: 2500))))
        let f = await failure { try await self.downloader.download(self.link(url), into: self.folder, maxBytes: 1 << 30) }
        XCTAssertEqual(f?.error, .interrupted)
        let resume = try XCTUnwrap(f?.resume)
        XCTAssertEqual(resume.bytes, 2500)
        XCTAssertEqual(resume.validator, "\"v1\"")

        let result = try await downloader.download(link(url), into: folder, maxBytes: 1 << 30, resume: resume)
        XCTAssertEqual(try Data(contentsOf: result.file), body)
        XCTAssertEqual(result.file, resume.file)
        let second = try XCTUnwrap(transport.requests(to: url).last)
        XCTAssertEqual(second.headers["Range"], "bytes=2500-")
        XCTAssertEqual(second.headers["If-Range"], "\"v1\"")
    }

    func testResumeStartsOverWhenTheFileChanged() async throws {
        let old = Self.mp4(4000), new = Self.mp4(3000)
        let url = "https://example.com/c.mp4"
        transport.on(url,
                     .init(headers: ["Content-Type": "video/mp4", "Content-Length": "4000", "Last-Modified": "Wed, 21 Oct 2026 07:28:00 GMT"],
                           chunks: Self.chunks(old.prefix(1000)), failAfterChunks: .network),
                     .init(headers: ["Content-Type": "video/mp4", "Content-Length": "3000"], chunks: Self.chunks(new)))
        let first = await failure { try await self.downloader.download(self.link(url), into: self.folder, maxBytes: 1 << 30) }
        let resume = try XCTUnwrap(first?.resume)
        let result = try await downloader.download(link(url), into: folder, maxBytes: 1 << 30, resume: resume)
        XCTAssertEqual(try Data(contentsOf: result.file), new)
        XCTAssertFalse(FileManager.default.fileExists(atPath: resume.file.path), "the stale partial file is deleted")

        // 416: the partial file is useless, so the next request asks for everything.
        let url2 = "https://example.com/d.mp4"
        transport.on(url2,
                     .init(headers: ["Content-Type": "video/mp4", "Content-Length": "4000", "ETag": "\"a\""], chunks: Self.chunks(old.prefix(1000)), failAfterChunks: .network),
                     .init(status: 416),
                     .init(headers: ["Content-Type": "video/mp4", "Content-Length": "4000"], chunks: Self.chunks(old)))
        let first2 = await failure { try await self.downloader.download(self.link(url2), into: self.folder, maxBytes: 1 << 30) }
        let resume2 = try XCTUnwrap(first2?.resume)
        let result2 = try await downloader.download(link(url2), into: folder, maxBytes: 1 << 30, resume: resume2)
        XCTAssertEqual(try Data(contentsOf: result2.file), old)
        XCTAssertNil(transport.requests(to: url2).last?.headers["Range"])
    }

    func testNoResumeWithoutAValidator() async throws {
        transport.on("https://example.com/n.mp4", .init(headers: ["Content-Type": "video/mp4", "Content-Length": "3000"],
                                                         chunks: Self.chunks(Self.mp4(3000).prefix(1000)), failAfterChunks: .network))
        let f = await failure { try await self.downloader.download(self.link("https://example.com/n.mp4"), into: self.folder, maxBytes: 1 << 30) }
        XCTAssertEqual(f?.error, .interrupted)
        XCTAssertNil(f?.resume)
        XCTAssertEqual(try hiddenFiles(), [])
    }

    func testNameFromContentDisposition() async throws {
        transport.on("https://example.com/get?id=9", .init(headers: ["Content-Type": "application/octet-stream",
                                                                      "Content-Disposition": "attachment; filename*=UTF-8''Caf%C3%A9%20Live.mp4"],
                                                            chunks: [Self.mp4(400)]))
        let result = try await downloader.download(link("https://example.com/get?id=9"), into: folder, maxBytes: 1 << 30)
        XCTAssertEqual(result.fileName, "Café Live.mp4")
        XCTAssertEqual(result.formatID, "mp4")
    }

    func testTransportErrors() async throws {
        transport.on("https://example.com/robots.txt", .init(failBeforeAnswer: .network))
        let f = await failure { try await self.downloader.download(self.link("https://example.com/a.mp4"), into: self.folder, maxBytes: 1 << 30) }
        XCTAssertEqual(f?.error, .network)

        transport.on("https://slow.example.com/a.mp4", .init(failBeforeAnswer: .timedOut))
        let g = await failure { try await self.downloader.download(self.link("https://slow.example.com/a.mp4"), into: self.folder, maxBytes: 1 << 30) }
        XCTAssertEqual(g?.error, .timedOut)
    }

    func testCancellingKeepsTheResumablePartialFile() async throws {
        transport.on("https://example.com/x.mp4", .init(headers: ["Content-Type": "video/mp4", "Content-Length": "8000", "ETag": "\"e\""],
                                                         chunks: Self.chunks(Self.mp4(8000))))
        let f = await failure {
            try await self.downloader.download(self.link("https://example.com/x.mp4"), into: self.folder, maxBytes: 1 << 30, progress: { p in
                if p.bytes >= 3000 { withUnsafeCurrentTask { $0?.cancel() } }   // progress runs inside the download's task
            })
        }
        XCTAssertEqual(f?.error, .interrupted)
        XCTAssertEqual(f?.resume?.bytes, 3000)
        XCTAssertTrue(FileManager.default.fileExists(atPath: try XCTUnwrap(f?.resume?.file.path)))
    }

    func testAccessDeniedIsNotRetried() async throws {
        transport.on("https://example.com/private.mp4", .init(status: 401, headers: ["WWW-Authenticate": "Basic realm=\"x\""]))
        let f = await failure { try await self.downloader.download(self.link("https://example.com/private.mp4"), into: self.folder, maxBytes: 1 << 30) }
        XCTAssertEqual(f?.error, .accessDenied)
        XCTAssertEqual(transport.requests(to: "https://example.com/private.mp4").count, 1)
    }
}
