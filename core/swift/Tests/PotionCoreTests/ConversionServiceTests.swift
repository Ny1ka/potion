import AppKit
import XCTest
@testable import PotionCore

/// The service routes each matrix operation to its engine.
final class ConversionServiceTests: XCTestCase {
    typealias S = EngineTestSupport
    var dir: URL!

    override func setUpWithError() throws { dir = try S.makeTempDirectory() }
    override func tearDownWithError() throws { if let dir { try? FileManager.default.removeItem(at: dir) } }

    func makePNG() throws -> URL {
        let ctx = CGContext(data: nil, width: 20, height: 10, bitsPerComponent: 8, bytesPerRow: 0,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        ctx.setFillColor(CGColor(red: 1, green: 0, blue: 0, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: 20, height: 10))
        let url = dir.appendingPathComponent("red.png")
        let dest = CGImageDestinationCreateWithURL(url as CFURL, "public.png" as CFString, 1, nil)!
        CGImageDestinationAddImage(dest, ctx.makeImage()!, nil)
        XCTAssertTrue(CGImageDestinationFinalize(dest))
        return url
    }

    func testRoutesImagesDocumentsAndPDFsWithoutFFmpeg() async throws {
        let service = ConversionService(tools: nil)
        let png = try makePNG()
        let jpg = try await service.convert(png, from: S.format("png"), to: S.format("jpg"), outputDirectory: dir)
        XCTAssertEqual(jpg.map(\.lastPathComponent), ["red.jpg"])
        let pdf = try await service.convert(png, from: S.format("png"), to: S.format("pdf"), outputDirectory: dir)
        XCTAssertEqual(pdf.map(\.lastPathComponent), ["red.pdf"])
        let pages = try await service.convert(pdf[0], from: S.format("pdf"), to: S.format("png"), outputDirectory: dir)
        XCTAssertEqual(pages.map(\.lastPathComponent), ["red 2.png"], "red.png exists, so the page is saved alongside")

        let md = dir.appendingPathComponent("notes.md")
        try Data("# Notes\n\nText".utf8).write(to: md)
        let txt = try await service.convert(md, from: S.format("md"), to: S.format("txt"), outputDirectory: dir)
        XCTAssertTrue(try String(contentsOf: txt[0], encoding: .utf8).contains("Notes"))
    }

    func testAudioAndVideoNeedTheTools() async throws {
        let wav = dir.appendingPathComponent("a.wav")
        try Data("RIFF....WAVEfmt ".utf8).write(to: wav)
        do {
            _ = try await ConversionService(tools: nil).convert(wav, from: S.format("wav"), to: S.format("mp3"), outputDirectory: dir)
            XCTFail("expected toolsMissing")
        } catch {
            XCTAssertEqual(error as? ConversionError, .toolsMissing)
        }
    }

    func testRoutesAudioWithTools() async throws {
        let tools = try S.tools()
        let tone = try await S.tone(tools, in: dir)
        final class Last: @unchecked Sendable { let lock = NSLock(); var value: Double? }
        let last = Last()
        let out = try await ConversionService(tools: tools).convert(tone, from: S.format("wav"), to: S.format("mp3"),
                                                                    outputDirectory: dir) { fraction in
            last.lock.lock(); last.value = fraction; last.lock.unlock()
        }
        XCTAssertEqual(out.map(\.pathExtension), ["mp3"])
        XCTAssertEqual(last.value, 1)
    }

    func testUnsupportedPairs() async throws {
        do {
            _ = try await ConversionService(tools: nil).convert(try makePNG(), from: S.format("png"), to: S.format("mp3"), outputDirectory: dir)
            XCTFail("expected unsupported")
        } catch {
            XCTAssertEqual(error as? ConversionError, .unsupported(from: "PNG", to: "MP3"))
        }
    }
}
