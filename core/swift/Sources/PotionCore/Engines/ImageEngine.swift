import AppKit
import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

/// How to size an image. Fitting never enlarges.
public enum ImageResize: Sendable, Equatable {
    case original
    /// Fit within the box, keeping proportions. Either side may be nil (unconstrained).
    case fit(width: Int?, height: Int?)
    /// Scale both sides by a percentage (e.g. 50).
    case percent(Double)
    /// Exactly this size (proportions may change).
    case exact(width: Int, height: Int)
}

/// Settings for one image conversion.
public struct ImageOptions: Sendable, Equatable {
    /// "high", "standard" or "small" (lossy formats only).
    public var presetID: String?
    /// 0…1, overrides the preset.
    public var quality: Double?
    public var resize: ImageResize
    /// Clockwise rotation: 0, 90, 180 or 270.
    public var rotation: Int
    public var flipHorizontal: Bool
    public var flipVertical: Bool

    public init(presetID: String? = nil, quality: Double? = nil, resize: ImageResize = .original, rotation: Int = 0,
                flipHorizontal: Bool = false, flipVertical: Bool = false) {
        self.presetID = presetID
        self.quality = quality
        self.resize = resize
        self.rotation = rotation
        self.flipHorizontal = flipHorizontal
        self.flipVertical = flipVertical
    }
}

/// Image settings from presets.json (core/spec).
public struct ImagePresets: Sendable {
    public struct Preset: Decodable, Sendable, Equatable, Identifiable {
        public let id: String
        public let name: String
        public let quality: Double
    }
    public let presets: [Preset]
    public let defaultPresetID: String
    public let lossy: Set<String>
    public let transparency: Set<String>
    public let icoMaxSize: Int
    public let pdfDefaultDPI: Double

    public static let shared: ImagePresets = {
        do {
            guard let url = Bundle.module.url(forResource: "presets", withExtension: "json") else { throw ConversionError.toolsMissing }
            return try ImagePresets(data: Data(contentsOf: url))
        } catch {
            preconditionFailure("PotionCore's bundled presets.json is invalid: \(error)")
        }
    }()

    public init(data: Data) throws {
        struct File: Decodable { let image: Image }
        struct Image: Decodable {
            let presets: [Preset]; let defaultPreset: String; let lossy: [String]; let transparency: [String]
            let icoMaxSize: Int; let pdfDefaultDPI: Double
        }
        let i = try JSONDecoder().decode(File.self, from: data).image
        presets = i.presets
        defaultPresetID = i.defaultPreset
        lossy = Set(i.lossy)
        transparency = Set(i.transparency)
        icoMaxSize = i.icoMaxSize
        pdfDefaultDPI = i.pdfDefaultDPI
    }

    public func quality(for options: ImageOptions) -> Double {
        if let q = options.quality { return min(max(q, 0), 1) }
        return (presets.first { $0.id == options.presetID } ?? presets.first { $0.id == defaultPresetID }!).quality
    }
}

/// Converts images with ImageIO (and AppKit for SVG). WebP output, and AVIF where macOS can't encode it, go
/// through the bundled FFmpeg. Handles the matrix operations `convertImage` and `imageToPdf`.
public final class ImageEngine: Sendable {
    /// Needed only for WebP output and the AVIF fallback.
    public let tools: FFmpegTools?
    let presets: ImagePresets
    let matrix: ConversionMatrix

    public init(tools: FFmpegTools?, presets: ImagePresets = .shared, matrix: ConversionMatrix = .shared) {
        self.tools = tools
        self.presets = presets
        self.matrix = matrix
    }

    static let imageIOTypes: [String: String] = [
        "png": "public.png", "jpg": "public.jpeg", "heic": "public.heic", "tiff": "public.tiff",
        "bmp": "com.microsoft.bmp", "gif": "com.compuserve.gif", "ico": "com.microsoft.ico", "avif": "public.avif",
    ]

    /// Whether this Mac's ImageIO can write a format (AVIF depends on the macOS version).
    static func imageIOCanWrite(_ formatID: String) -> Bool {
        guard let type = imageIOTypes[formatID] else { return false }
        let writable = CGImageDestinationCopyTypeIdentifiers() as? [String] ?? []
        return writable.contains(type)
    }

    // MARK: - Convert

    public func convert(_ input: URL, from inputFormat: FileFormat, to outputFormat: FileFormat,
                        outputDirectory: URL, options: ImageOptions = ImageOptions()) async throws -> URL {
        guard let conversion = matrix.conversion(from: inputFormat.id, to: outputFormat.id) else {
            throw ConversionError.unsupported(from: inputFormat.name, to: outputFormat.name)
        }
        if conversion.operation == .imageToPdf {
            return try await makePDF(from: [(input, inputFormat)], outputDirectory: outputDirectory,
                                     baseName: input.deletingPathExtension().lastPathComponent, options: options)
        }
        guard conversion.operation == .convertImage else {
            throw ConversionError.unsupported(from: inputFormat.name, to: outputFormat.name)
        }
        try OutputFiles.checkWritable(outputDirectory)

        let loaded = try load(input, format: inputFormat, resize: options.resize)
        var image = try transform(loaded.image, options: options)
        if !presets.transparency.contains(outputFormat.id) { image = try flatten(image) }
        if outputFormat.id == "ico" {
            // Icons use standard square sizes up to 256 px: fit (never enlarge), then center on the smallest
            // standard transparent canvas that holds the picture. ImageIO rejects other sizes.
            if max(image.width, image.height) > presets.icoMaxSize { image = try scaled(image, toFit: presets.icoMaxSize) }
            image = try squared(image, side: Self.iconSizes.first { $0 >= max(image.width, image.height) } ?? presets.icoMaxSize)
        }
        let quality = presets.lossy.contains(outputFormat.id) ? presets.quality(for: options) : nil

        let ext = outputFormat.preferredExtension
        let temporary = OutputFiles.temporaryURL(in: outputDirectory, fileExtension: ext)
        defer { OutputFiles.removeIfPresent(temporary) }

        if outputFormat.id == "webp" || (outputFormat.id == "avif" && !Self.imageIOCanWrite("avif")) {
            try await encodeWithFFmpeg(image, formatID: outputFormat.id, quality: quality ?? 0.82, to: temporary,
                                       inputName: input.lastPathComponent)
        } else {
            guard let type = Self.imageIOTypes[outputFormat.id] else {
                throw ConversionError.unsupported(from: inputFormat.name, to: outputFormat.name)
            }
            try write(image, type: type, properties: loaded.properties, quality: quality, to: temporary,
                      inputName: input.lastPathComponent)
        }

        guard let source = CGImageSourceCreateWithURL(temporary as CFURL, nil), CGImageSourceGetCount(source) > 0,
              let check = CGImageSourceCreateImageAtIndex(source, 0, nil), check.width > 0, check.height > 0 else {
            throw ConversionError.verificationFailed(fileName: input.lastPathComponent)
        }
        return try OutputFiles.finalize(temporary, in: outputDirectory, baseName: input.deletingPathExtension().lastPathComponent,
                                        fileExtension: ext)
    }

    // MARK: - Images → PDF

    /// One PDF page per image, each page the image's size (at its DPI, 72 by default). Images are drawn
    /// losslessly. Pass several images to get one combined PDF named `baseName`.
    public func makePDF(from inputs: [(url: URL, format: FileFormat)], outputDirectory: URL, baseName: String,
                        options: ImageOptions = ImageOptions()) async throws -> URL {
        guard !inputs.isEmpty else { throw ConversionError.unsupported(from: "Images", to: "PDF") }
        for input in inputs where matrix.conversion(from: input.format.id, to: "pdf")?.operation != .imageToPdf {
            throw ConversionError.unsupported(from: input.format.name, to: "PDF")
        }
        try OutputFiles.checkWritable(outputDirectory)
        let temporary = OutputFiles.temporaryURL(in: outputDirectory, fileExtension: "pdf")
        defer { OutputFiles.removeIfPresent(temporary) }

        guard let context = CGContext(temporary as CFURL, mediaBox: nil, nil) else {
            throw ConversionError.cannotWrite(folder: outputDirectory.lastPathComponent)
        }
        for input in inputs {
            let loaded = try load(input.url, format: input.format, resize: options.resize)
            let image = try transform(loaded.image, options: options)
            let dpi = (loaded.properties[kCGImagePropertyDPIWidth] as? Double).flatMap { $0 > 0 ? $0 : nil } ?? presets.pdfDefaultDPI
            var box = CGRect(x: 0, y: 0, width: Double(image.width) * 72 / dpi, height: Double(image.height) * 72 / dpi)
            context.beginPage(mediaBox: &box)
            context.interpolationQuality = .high
            context.draw(image, in: box)
            context.endPage()
        }
        context.closePDF()

        guard let pdf = CGPDFDocument(temporary as CFURL), pdf.numberOfPages == inputs.count else {
            throw ConversionError.verificationFailed(fileName: inputs[0].url.lastPathComponent)
        }
        return try OutputFiles.finalize(temporary, in: outputDirectory, baseName: baseName, fileExtension: "pdf")
    }

    // MARK: - Steps

    struct Loaded {
        let image: CGImage
        /// Source metadata (EXIF, GPS, TIFF…), carried over to the output.
        let properties: [CFString: Any]
    }

    /// Decodes the first frame, upright (EXIF orientation applied), already fitted when the resize allows it.
    func load(_ url: URL, format: FileFormat, resize: ImageResize) throws -> Loaded {
        if format.id == "svg" { return Loaded(image: try rasterizeSVG(url, resize: resize), properties: [:]) }

        guard let source = CGImageSourceCreateWithURL(url as CFURL, [kCGImageSourceShouldCache: false] as CFDictionary),
              CGImageSourceGetCount(source) > 0,
              let props = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let width = props[kCGImagePropertyPixelWidth] as? Int, let height = props[kCGImagePropertyPixelHeight] as? Int,
              width > 0, height > 0 else {
            throw ConversionError.unreadable(fileName: url.lastPathComponent)
        }
        let target = Self.targetSize(width: width, height: height, resize: resize, orientation: props[kCGImagePropertyOrientation] as? Int)
        let decodeOptions: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,   // apply EXIF orientation
            kCGImageSourceThumbnailMaxPixelSize: max(target.width, target.height),
            kCGImageSourceShouldCacheImmediately: true,
        ]
        guard var image = CGImageSourceCreateThumbnailAtIndex(source, 0, decodeOptions as CFDictionary) else {
            throw ConversionError.unreadable(fileName: url.lastPathComponent)
        }
        if case .exact = resize, image.width != target.width || image.height != target.height {
            image = try redraw(image, width: target.width, height: target.height)
        }
        return Loaded(image: image, properties: props)
    }

    /// The upright output size for a source of `width`×`height` pixels.
    static func targetSize(width: Int, height: Int, resize: ImageResize, orientation: Int?) -> (width: Int, height: Int) {
        let sideways = [5, 6, 7, 8].contains(orientation ?? 1)
        let w = sideways ? height : width, h = sideways ? width : height
        switch resize {
        case .original:
            return (w, h)
        case let .fit(maxW, maxH):
            let scale = min(1, Double(maxW ?? w) / Double(w), Double(maxH ?? h) / Double(h))
            return (max(1, Int((Double(w) * scale).rounded())), max(1, Int((Double(h) * scale).rounded())))
        case let .percent(p):
            let scale = min(max(p, 1), 1000) / 100
            return (max(1, Int((Double(w) * scale).rounded())), max(1, Int((Double(h) * scale).rounded())))
        case let .exact(ew, eh):
            return (max(1, ew), max(1, eh))
        }
    }

    func rasterizeSVG(_ url: URL, resize: ImageResize) throws -> CGImage {
        guard let svg = NSImage(contentsOf: url), svg.size.width > 0, svg.size.height > 0 else {
            throw ConversionError.unreadable(fileName: url.lastPathComponent)
        }
        let target = Self.targetSize(width: Int(svg.size.width.rounded()), height: Int(svg.size.height.rounded()),
                                     resize: resize, orientation: nil)
        var rect = CGRect(x: 0, y: 0, width: target.width, height: target.height)
        guard let image = svg.cgImage(forProposedRect: &rect, context: nil, hints: nil) else {
            throw ConversionError.unreadable(fileName: url.lastPathComponent)
        }
        return image.width == target.width && image.height == target.height
            ? image : try redraw(image, width: target.width, height: target.height)
    }

    func transform(_ image: CGImage, options: ImageOptions) throws -> CGImage {
        let rotation = ((options.rotation % 360) + 360) % 360
        guard rotation != 0 || options.flipHorizontal || options.flipVertical else { return image }
        let sideways = rotation == 90 || rotation == 270
        let w = sideways ? image.height : image.width, h = sideways ? image.width : image.height
        let ctx = try Self.context(width: w, height: h, like: image)
        ctx.translateBy(x: CGFloat(w) / 2, y: CGFloat(h) / 2)
        ctx.scaleBy(x: options.flipHorizontal ? -1 : 1, y: options.flipVertical ? -1 : 1)
        ctx.rotate(by: -CGFloat(rotation) * .pi / 180)   // CG rotates counter-clockwise
        ctx.draw(image, in: CGRect(x: -CGFloat(image.width) / 2, y: -CGFloat(image.height) / 2,
                                   width: CGFloat(image.width), height: CGFloat(image.height)))
        guard let out = ctx.makeImage() else { throw ConversionError.conversionFailed(fileName: "", detail: "transform") }
        return out
    }

    /// Composites onto white, for formats without transparency.
    func flatten(_ image: CGImage) throws -> CGImage {
        let alpha = image.alphaInfo
        guard alpha != .none, alpha != .noneSkipFirst, alpha != .noneSkipLast else { return image }
        let ctx = try Self.context(width: image.width, height: image.height, like: image, opaque: true)
        ctx.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: image.width, height: image.height))
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
        guard let out = ctx.makeImage() else { throw ConversionError.conversionFailed(fileName: "", detail: "flatten") }
        return out
    }

    func scaled(_ image: CGImage, toFit side: Int) throws -> CGImage {
        let scale = Double(side) / Double(max(image.width, image.height))
        return try redraw(image, width: max(1, Int((Double(image.width) * scale).rounded())),
                          height: max(1, Int((Double(image.height) * scale).rounded())))
    }

    static let iconSizes = [16, 24, 32, 48, 64, 128, 256]

    func squared(_ image: CGImage, side: Int) throws -> CGImage {
        guard image.width != side || image.height != side else { return image }
        let ctx = try Self.context(width: side, height: side, like: image)
        ctx.draw(image, in: CGRect(x: (side - image.width) / 2, y: (side - image.height) / 2,
                                   width: image.width, height: image.height))
        guard let out = ctx.makeImage() else { throw ConversionError.conversionFailed(fileName: "", detail: "square") }
        return out
    }

    func redraw(_ image: CGImage, width: Int, height: Int) throws -> CGImage {
        let ctx = try Self.context(width: width, height: height, like: image)
        ctx.interpolationQuality = .high
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        guard let out = ctx.makeImage() else { throw ConversionError.conversionFailed(fileName: "", detail: "resize") }
        return out
    }

    static func context(width: Int, height: Int, like image: CGImage, opaque: Bool = false) throws -> CGContext {
        let space = image.colorSpace.flatMap { $0.model == .rgb ? $0 : nil } ?? CGColorSpace(name: CGColorSpace.sRGB)!
        let info = opaque ? CGImageAlphaInfo.noneSkipLast.rawValue : CGImageAlphaInfo.premultipliedLast.rawValue
        guard let ctx = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: space, bitmapInfo: info) else {
            throw ConversionError.conversionFailed(fileName: "", detail: "Couldn't create a \(width)×\(height) drawing context")
        }
        return ctx
    }

    func write(_ image: CGImage, type: String, properties: [CFString: Any], quality: Double?, to url: URL,
               inputName: String) throws {
        guard let destination = CGImageDestinationCreateWithURL(url as CFURL, type as CFString, 1, nil) else {
            throw ConversionError.conversionFailed(fileName: inputName, detail: "ImageIO can't write \(type)")
        }
        // Keep camera metadata (EXIF, GPS, TIFF, IPTC); orientation is already applied to the pixels.
        var props: [CFString: Any] = [:]
        for key in [kCGImagePropertyExifDictionary, kCGImagePropertyGPSDictionary, kCGImagePropertyIPTCDictionary,
                    kCGImagePropertyTIFFDictionary, kCGImagePropertyDPIWidth, kCGImagePropertyDPIHeight] {
            if let value = properties[key] { props[key] = value }
        }
        if var tiff = props[kCGImagePropertyTIFFDictionary] as? [CFString: Any] {
            tiff[kCGImagePropertyTIFFOrientation] = 1
            props[kCGImagePropertyTIFFDictionary] = tiff
        }
        props[kCGImagePropertyOrientation] = 1
        if let quality { props[kCGImageDestinationLossyCompressionQuality] = quality }
        CGImageDestinationAddImage(destination, image, props as CFDictionary)
        guard CGImageDestinationFinalize(destination) else {
            throw ConversionError.conversionFailed(fileName: inputName, detail: "ImageIO failed to write \(type)")
        }
    }

    /// WebP (libwebp) and the AVIF fallback (SVT-AV1): hand FFmpeg a lossless PNG of the processed image.
    func encodeWithFFmpeg(_ image: CGImage, formatID: String, quality: Double, to url: URL, inputName: String) async throws {
        guard let tools else { throw ConversionError.toolsMissing }
        let png = url.deletingLastPathComponent().appendingPathComponent(".potion-\(UUID().uuidString).png")
        defer { OutputFiles.removeIfPresent(png) }
        try write(image, type: "public.png", properties: [:], quality: nil, to: png, inputName: inputName)

        var args = ["-hide_banner", "-nostdin", "-n", "-loglevel", "error", "-i", png.path, "-frames:v", "1"]
        if formatID == "webp" {
            args += ["-c:v", "libwebp", "-quality", "\(Int((quality * 100).rounded()))", "-f", "webp"]
        } else {
            let crf = Int((63 - quality * 45).rounded())   // 0.92 → 22, 0.6 → 36
            args += ["-c:v", "libsvtav1", "-crf", "\(crf)", "-pix_fmt", "yuv420p", "-f", "avif"]
        }
        let result = try await ProcessRunner.run(tools.ffmpeg, args + [url.path])
        guard result.status == 0 else { throw ConversionError.conversionFailed(fileName: inputName, detail: result.stderr) }
    }
}
