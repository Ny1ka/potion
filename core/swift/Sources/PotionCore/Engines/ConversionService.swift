import Foundation

/// Options for one conversion, per kind of file. Each engine reads only its own part.
public struct ConversionSettings: Sendable, Equatable {
    public var audio = AudioOptions()
    public var video = VideoOptions()
    public var image = ImageOptions()
    public var pdf = PDFOptions()

    public init(audio: AudioOptions = AudioOptions(), video: VideoOptions = VideoOptions(),
                image: ImageOptions = ImageOptions(), pdf: PDFOptions = PDFOptions()) {
        self.audio = audio
        self.video = video
        self.image = image
        self.pdf = pdf
    }
}

/// The single entry point the apps use: looks up the conversion in the matrix and hands it to the engine
/// for that operation. Returns the saved files (several for PDF → images, otherwise one).
public final class ConversionService: Sendable {
    public let tools: FFmpegTools?
    let matrix: ConversionMatrix

    /// `tools` is the bundled FFmpeg; without it, audio and video conversions fail with `toolsMissing`
    /// and everything else still works.
    public init(tools: FFmpegTools?, matrix: ConversionMatrix = .shared) {
        self.tools = tools
        self.matrix = matrix
    }

    /// Finds the bundled FFmpeg (see `FFmpegTools.locate`), or works without it.
    public static func withBundledTools() -> ConversionService {
        ConversionService(tools: try? FFmpegTools.locate())
    }

    public func convert(_ input: URL, from inputFormat: FileFormat, to outputFormat: FileFormat, outputDirectory: URL,
                        settings: ConversionSettings = ConversionSettings(),
                        progress: @escaping @Sendable (Double) -> Void = { _ in }) async throws -> [URL] {
        guard let conversion = matrix.conversion(from: inputFormat.id, to: outputFormat.id) else {
            throw ConversionError.unsupported(from: inputFormat.name, to: outputFormat.name)
        }
        try Task.checkCancellation()
        let result: [URL]
        switch conversion.operation {
        case .transcodeVideo, .videoToGif, .gifToVideo:
            guard let tools else { throw ConversionError.toolsMissing }
            result = [try await VideoEngine(tools: tools, matrix: matrix).convert(
                input, from: inputFormat, to: outputFormat, outputDirectory: outputDirectory,
                options: settings.video, progress: progress)]
        case .transcodeAudio, .extractAudio:
            guard let tools else { throw ConversionError.toolsMissing }
            result = [try await AudioEngine(tools: tools, matrix: matrix).convert(
                input, from: inputFormat, to: outputFormat, outputDirectory: outputDirectory,
                options: settings.audio, progress: progress)]
        case .convertImage, .imageToPdf:
            result = [try await ImageEngine(tools: tools, matrix: matrix).convert(
                input, from: inputFormat, to: outputFormat, outputDirectory: outputDirectory, options: settings.image)]
        case .convertDocument, .documentToPdf:
            result = [try await DocumentEngine(matrix: matrix).convert(
                input, from: inputFormat, to: outputFormat, outputDirectory: outputDirectory)]
        case .pdfToDocument, .pdfToImage:
            result = try await PDFEngine(matrix: matrix).convert(
                input, from: inputFormat, to: outputFormat, outputDirectory: outputDirectory, options: settings.pdf)
        }
        progress(1)
        return result
    }
}
