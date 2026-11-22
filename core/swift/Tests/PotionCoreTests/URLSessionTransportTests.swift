import XCTest
@testable import PotionCore

/// The real macOS transport against a loopback server: no automatic redirects, no cookies, no credentials.
final class URLSessionTransportTests: XCTestCase {
    var server: LoopbackServer!

    override func setUp() async throws {
        server = try LoopbackServer()
    }

    override func tearDown() async throws {
        server.stop()
    }

    func collect(_ response: HTTPResponse) async throws -> Data {
        var data = Data()
        for try await chunk in response.body { data.append(chunk) }
        return data
    }

    let headers = ["User-Agent": "PotionDropper/0.0.1 (+https://github.com/Ny1ka/potion)", "Accept-Encoding": "identity", "Accept": "*/*"]

    func testStreamsTheBodyAndSendsOurHeaders() async throws {
        let body = Data((0..<200_000).map { UInt8($0 % 256) })
        try await server.start { _ in .init(headers: ["Content-Type": "video/mp4", "ETag": "\"x\""], body: body) }
        let response = try await URLSessionTransport().get(HTTPRequest(url: server.url("/a.mp4"), headers: headers))
        XCTAssertEqual(response.status, 200)
        XCTAssertEqual(response.header("content-type"), "video/mp4")
        XCTAssertEqual(response.header("ETag"), "\"x\"")
        let received = try await collect(response)
        XCTAssertEqual(received, body)
        let request = try XCTUnwrap(server.requests.first)
        XCTAssertEqual(request.headers["user-agent"], headers["User-Agent"])
        XCTAssertEqual(request.headers["accept-encoding"], "identity")
        XCTAssertNil(request.headers["cookie"])
        XCTAssertNil(request.headers["authorization"])
    }

    func testRedirectsAreNotFollowed() async throws {
        try await server.start { request in
            request.path == "/old" ? .init(status: 302, reason: "Found", headers: ["Location": "/new"]) : .init(body: Data("new".utf8))
        }
        let response = try await URLSessionTransport().get(HTTPRequest(url: server.url("/old"), headers: headers))
        XCTAssertEqual(response.status, 302)
        XCTAssertEqual(response.header("Location"), "/new")
        _ = try await collect(response)
        XCTAssertEqual(server.requests.map(\.path), ["/old"])
    }

    func testCookiesAreNeitherStoredNorSent() async throws {
        try await server.start { _ in .init(headers: ["Set-Cookie": "session=abc; Path=/"], body: Data("x".utf8)) }
        let transport = URLSessionTransport()
        _ = try await collect(transport.get(HTTPRequest(url: server.url("/one"), headers: headers)))
        _ = try await collect(transport.get(HTTPRequest(url: server.url("/two"), headers: headers)))
        XCTAssertEqual(server.requests.count, 2)
        XCTAssertNil(server.requests[1].headers["cookie"])
    }

    func testSignInRequestsComeBackAsResponses() async throws {
        try await server.start { _ in .init(status: 401, reason: "Unauthorized", headers: ["WWW-Authenticate": "Basic realm=\"files\""], body: Data("no".utf8)) }
        let response = try await URLSessionTransport().get(HTTPRequest(url: server.url("/private.mp4"), headers: headers))
        XCTAssertEqual(response.status, 401)
        _ = try? await collect(response)
        XCTAssertTrue(server.requests.allSatisfy { $0.headers["authorization"] == nil })
    }

    func testTimeoutWhenTheServerNeverAnswers() async throws {
        try await server.start { _ in .init(hang: true) }
        do {
            _ = try await URLSessionTransport(timeoutSeconds: 1).get(HTTPRequest(url: server.url("/slow.mp4"), headers: headers))
            XCTFail("expected a timeout")
        } catch {
            XCTAssertEqual(error as? TransportError, .timedOut)
        }
    }

    func testConnectionRefusedIsANetworkError() async throws {
        try await server.start { _ in .init() }
        let url = server.url("/a.mp4")
        server.stop()
        do {
            _ = try await URLSessionTransport(timeoutSeconds: 15).get(HTTPRequest(url: url, headers: headers))
            XCTFail("expected a network error")
        } catch {
            XCTAssertEqual(error as? TransportError, .network)
        }
    }

    func testDownloaderEndToEnd() async throws {
        let mp4 = DownloaderTests.mp4(150_000)
        try await server.start { request in
            switch request.path {
            case "/robots.txt": return .init(headers: ["Content-Type": "text/plain"], body: Data("User-agent: *\nDisallow: /private/\n".utf8))
            case "/get": return .init(status: 302, reason: "Found", headers: ["Location": "/files/clip.mp4"])
            case "/files/clip.mp4": return .init(headers: ["Content-Type": "video/mp4", "ETag": "\"c\""], body: mp4)
            default: return .init(status: 404, reason: "Not Found")
            }
        }
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("potion-e2e-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }

        let downloader = Downloader(transport: URLSessionTransport(), appVersion: "0.0.1")
        let link = try SourceRegistry.shared.link(from: server.url("/get")).get()
        let result = try await downloader.download(link, into: folder, maxBytes: 1 << 30)
        XCTAssertEqual(result.formatID, "mp4")
        XCTAssertEqual(result.fileName, "clip.mp4")
        XCTAssertEqual(try Data(contentsOf: result.file), mp4)
        XCTAssertEqual(server.requests.map(\.path), ["/robots.txt", "/get", "/files/clip.mp4"])

        do {
            _ = try await downloader.download(SourceRegistry.shared.link(from: server.url("/private/a.mp4")).get(), into: folder, maxBytes: 1 << 30)
            XCTFail("robots.txt disallows /private/")
        } catch let failure as DownloadFailure {
            XCTAssertEqual(failure.error, .robotsDisallowed)
        }
    }

    func testDroppedConnectionCanResume() async throws {
        let mp4 = DownloaderTests.mp4(120_000)
        try await server.start { request in
            if request.path == "/robots.txt" { return .init(status: 404, reason: "Not Found") }
            if let range = request.headers["range"], range == "bytes=50000-", request.headers["if-range"] == "\"r\"" {
                return .init(status: 206, reason: "Partial Content",
                             headers: ["Content-Type": "video/mp4", "Content-Range": "bytes 50000-119999/120000", "ETag": "\"r\""],
                             body: mp4.suffix(from: 50000))
            }
            return .init(headers: ["Content-Type": "video/mp4", "ETag": "\"r\"", "Content-Length": "120000"], body: mp4, dropAfter: 50000)
        }
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("potion-e2e-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }

        let downloader = Downloader(transport: URLSessionTransport(), appVersion: "0.0.1")
        let link = try SourceRegistry.shared.link(from: server.url("/r.mp4")).get()
        var resume: ResumeState?
        do {
            _ = try await downloader.download(link, into: folder, maxBytes: 1 << 30)
            XCTFail("the connection drops")
        } catch let failure as DownloadFailure {
            XCTAssertEqual(failure.error, .interrupted)
            resume = failure.resume
        }
        let state = try XCTUnwrap(resume)
        XCTAssertEqual(state.bytes, 50000)
        let result = try await downloader.download(link, into: folder, maxBytes: 1 << 30, resume: state)
        XCTAssertEqual(try Data(contentsOf: result.file), mp4)
    }
}
