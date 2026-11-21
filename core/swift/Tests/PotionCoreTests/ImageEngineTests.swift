import CoreGraphics
import ImageIO
import XCTest
@testable import PotionCore

/// Image conversions with ImageIO (plus FFmpeg for WebP/AVIF). Samples are drawn in the test.
final class ImageEngineTests: XCTestCase {
    typealias S = EngineTestSupport
    var dir: URL!
    var engine: ImageEngine!

    override func setUp() async throws {
        dir = try S.makeTempDirectory()
        engine = ImageEngine(tools: try? S.tools())
    }

    override func tearDown() async throws {
        if let dir { try? FileManager.default.removeItem(at: dir) }
    }

    // MARK: Samples

    /// 100×60: left half opaque red, right half fully transparent.
    func makePNG(name: String = "sample.png", width: Int = 100, height: Int = 60) throws -> URL {
        let ctx = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        ctx.setFillColor(CGColor(red: 1, green: 0, blue: 0, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: width / 2, height: height))
        return try save(ctx.makeImage()!, type: "public.png", name: name)
    }

    /// Stored 100×60 with EXIF orientation 6 (displayed rotated 90° clockwise: 60×100) and a capture date.
    func makeRotatedJPEG() throws -> URL {
        let ctx = CGContext(data: nil, width: 100, height: 60, bitsPerComponent: 8, bytesPerRow: 0,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue)!
        ctx.setFillColor(CGColor(red: 0, green: 0, blue: 1, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: 100, height: 60))
        return try save(ctx.makeImage()!, type: "public.jpeg", name: "photo.jpg", properties: [
            kCGImagePropertyOrientation: 6,
            kCGImagePropertyExifDictionary: [kCGImagePropertyExifDateTimeOriginal: "2026:01:02 03:04:05"],
        ])
    }

    func save(_ image: CGImage, type: String, name: String, properties: [CFString: Any] = [:]) throws -> URL {
        let url = dir.appendingPathComponent(name)
        let dest = CGImageDestinationCreateWithURL(url as CFURL, type as CFString, 1, nil)!
        CGImageDestinationAddImage(dest, image, properties as CFDictionary)
        XCTAssertTrue(CGImageDestinationFinalize(dest))
        return url
    }

    func image(_ url: URL) throws -> CGImage {
        let src = try XCTUnwrap(CGImageSourceCreateWithURL(url as CFURL, nil))
        return try XCTUnwrap(CGImageSourceCreateThumbnailAtIndex(src, 0, [
            kCGImageSourceCreateThumbnailFromImageAlways: true, kCGImageSourceCreateThumbnailWithTransform: true,
        ] as CFDictionary))
    }

    /// RGBA of one pixel, (0,0) = top-left.
    func pixel(_ image: CGImage, x: Int, y: Int) -> [UInt8] {
        var data = [UInt8](repeating: 0, count: 4)
        let ctx = CGContext(data: &data, width: 1, height: 1, bitsPerComponent: 8, bytesPerRow: 4,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        ctx.draw(image, in: CGRect(x: -x, y: -(image.height - 1 - y), width: image.width, height: image.height))
        return data
    }

    func convert(_ url: URL, from: String, to: String, options: ImageOptions = ImageOptions()) async throws -> URL {
        try await engine.convert(url, from: S.format(from), to: S.format(to), outputDirectory: dir, options: options)
    }

    // MARK: Tests

    func testPNGToEveryImageFormat() async throws {
        let png = try makePNG()
        var formats = ["png", "jpg", "heic", "tiff", "bmp", "gif", "ico", "avif"]
        if engine.tools != nil { formats.append("webp") }
        for id in formats {
            let out = try await convert(png, from: "png", to: id)
            let img = try image(out)
            XCTAssertEqual([img.width, img.height], id == "ico" ? [128, 128] : [100, 60], id)
            XCTAssertEqual(try FormatRegistry.shared.detect(fileAt: out).format?.id, id, "\(id) is detected as itself")
        }
    }

    func testAppliesEXIFOrientationAndKeepsMetadata() async throws {
        let jpg = try makeRotatedJPEG()
        let heic = try await convert(jpg, from: "jpg", to: "heic")
        let src = try XCTUnwrap(CGImageSourceCreateWithURL(heic as CFURL, nil))
        let props = try XCTUnwrap(CGImageSourceCopyPropertiesAtIndex(src, 0, nil) as? [CFString: Any])
        XCTAssertEqual(props[kCGImagePropertyPixelWidth] as? Int, 60, "rotated upright")
        XCTAssertEqual(props[kCGImagePropertyPixelHeight] as? Int, 100)
        XCTAssertEqual((props[kCGImagePropertyOrientation] as? Int) ?? 1, 1)
        let exif = props[kCGImagePropertyExifDictionary] as? [CFString: Any]
        XCTAssertEqual(exif?[kCGImagePropertyExifDateTimeOriginal] as? String, "2026:01:02 03:04:05", "capture date kept")
    }

    func testTransparencyBecomesWhiteOnlyWhereNeeded() async throws {
        let png = try makePNG()
        let jpg = try image(try await convert(png, from: "png", to: "jpg"))
        let white = pixel(jpg, x: 90, y: 30)
        XCTAssertTrue(white.prefix(3).allSatisfy { $0 > 245 }, "transparent area is white in JPG: \(white)")
        XCTAssertTrue(pixel(jpg, x: 10, y: 30)[0] > 200, "red stays red")

        let webpOrPng = try image(try await convert(png, from: "png", to: "tiff"))
        XCTAssertEqual(pixel(webpOrPng, x: 90, y: 30)[3], 0, "TIFF keeps transparency")
    }

    func testResizeModes() async throws {
        let png = try makePNG()
        let cases: [(ImageResize, [Int])] = [
            (.fit(width: 50, height: nil), [50, 30]),
            (.fit(width: 1000, height: 1000), [100, 60]),   // never enlarges
            (.percent(50), [50, 30]),
            (.exact(width: 40, height: 40), [40, 40]),
        ]
        for (resize, size) in cases {
            let img = try image(try await convert(png, from: "png", to: "png", options: ImageOptions(resize: resize)))
            XCTAssertEqual([img.width, img.height], size, "\(resize)")
        }
    }

    func testRotateAndFlip() async throws {
        let png = try makePNG()   // red on the left
        let rotated = try image(try await convert(png, from: "png", to: "png", options: ImageOptions(rotation: 90)))
        XCTAssertEqual([rotated.width, rotated.height], [60, 100])
        XCTAssertGreaterThan(pixel(rotated, x: 30, y: 10)[0], 200, "after a clockwise turn, the red half is on top")

        let flipped = try image(try await convert(png, from: "png", to: "png", options: ImageOptions(flipHorizontal: true)))
        XCTAssertEqual(pixel(flipped, x: 10, y: 30)[3], 0, "left side is now the transparent half")
        XCTAssertGreaterThan(pixel(flipped, x: 90, y: 30)[0], 200)
    }

    func testQualityPresetsChangeFileSize() async throws {
        let png = try makePNG(name: "noise.png", width: 400, height: 300)
        let high = try await convert(png, from: "png", to: "jpg", options: ImageOptions(presetID: "high"))
        let small = try await convert(png, from: "png", to: "jpg", options: ImageOptions(presetID: "small"))
        let size = { (u: URL) in (try FileManager.default.attributesOfItem(atPath: u.path)[.size] as? Int) ?? 0 }
        XCTAssertLessThan(try size(small), try size(high))
    }

    func testICOIsSquareAndCappedAt256() async throws {
        let big = try makePNG(name: "big.png", width: 1000, height: 500)
        let ico = try image(try await convert(big, from: "png", to: "ico"))
        XCTAssertEqual([ico.width, ico.height], [256, 256])
        XCTAssertEqual(pixel(ico, x: 10, y: 10)[3], 0, "padding above the picture is transparent")
        XCTAssertGreaterThan(pixel(ico, x: 10, y: 128)[0], 200, "the picture is centered")
    }

    func testSVGToPNG() async throws {
        let svg = dir.appendingPathComponent("icon.svg")
        try ##"<svg xmlns="http://www.w3.org/2000/svg" width="40" height="20"><rect width="20" height="20" fill="#00ff00"/></svg>"##
            .write(to: svg, atomically: true, encoding: .utf8)
        let png = try image(try await convert(svg, from: "svg", to: "png"))
        XCTAssertEqual([png.width, png.height], [40, 20])
        XCTAssertGreaterThan(pixel(png, x: 5, y: 5)[1], 200, "green square drawn")
        XCTAssertEqual(pixel(png, x: 35, y: 5)[3], 0, "outside the shape stays transparent")
    }

    func testImageToPDFAndCombinedPDF() async throws {
        let png = try makePNG()
        let single = try await convert(png, from: "png", to: "pdf")
        let doc = try XCTUnwrap(CGPDFDocument(single as CFURL))
        XCTAssertEqual(doc.numberOfPages, 1)
        let box = try XCTUnwrap(doc.page(at: 1)?.getBoxRect(.mediaBox))
        XCTAssertEqual([box.width, box.height], [100, 60], "one point per pixel at 72 dpi")

        let jpg = try makeRotatedJPEG()
        let combined = try await engine.makePDF(from: [(png, S.format("png")), (jpg, S.format("jpg")), (png, S.format("png"))],
                                                outputDirectory: dir, baseName: "Album")
        XCTAssertEqual(combined.lastPathComponent, "Album.pdf")
        let combinedDoc = try XCTUnwrap(CGPDFDocument(combined as CFURL))
        XCTAssertEqual(combinedDoc.numberOfPages, 3)
        XCTAssertEqual(combinedDoc.page(at: 2)?.getBoxRect(.mediaBox).height, 100, "the photo page is upright")
    }

    func testUnreadableImage() async throws {
        let junk = dir.appendingPathComponent("broken.png")
        try Data((0..<2048).map { UInt8($0 % 7) }).write(to: junk)
        do {
            _ = try await convert(junk, from: "png", to: "jpg")
            XCTFail("expected unreadable")
        } catch let error as ConversionError {
            XCTAssertEqual(error, .unreadable(fileName: "broken.png"))
        }
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: dir.path).filter { $0.hasPrefix(".potion-") }, [])
    }

    func testTargetSizeMath() {
        XCTAssertTrue(ImageEngine.targetSize(width: 100, height: 60, resize: .original, orientation: 6) == (60, 100))
        XCTAssertTrue(ImageEngine.targetSize(width: 4000, height: 3000, resize: .fit(width: nil, height: 300), orientation: 1) == (400, 300))
    }

    func testPresetsMatchTheMatrix() {
        let outputs = Set(ConversionMatrix.shared.outputs(for: "png").filter { $0.operation == .convertImage }.map(\.output.id))
        let transparent = ImagePresets.shared.transparency
        XCTAssertEqual(outputs.subtracting(transparent), ["jpg", "bmp"], "only JPG and BMP lose transparency (matches the matrix note)")
        XCTAssertEqual(outputs.subtracting(Set(ImageEngine.imageIOTypes.keys)), ["webp"], "everything else is written by ImageIO")
    }
}
