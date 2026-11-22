import AppKit
import PotionCore
import SwiftUI

/// The main window's state: the link, what Fetch found, the output choices and the current download.
@MainActor
final class DropperModel: ObservableObject {
    struct Media: Equatable {
        let link: DropLink
        let preview: LinkPreview
        let source: FileFormat

        static func == (a: Media, b: Media) -> Bool { a.link == b.link && a.source == b.source }
    }

    enum FetchState: Equatable {
        case idle
        case fetching
        case failed(String)
        case ready(Media)
    }

    enum JobState: Equatable {
        case idle
        /// `fraction` is nil while the total isn't known.
        case running(status: String, fraction: Double?, detail: String?)
        case done([URL])
        case failed(String)
    }

    @Published var text = ""
    @Published private(set) var fetch: FetchState = .idle
    @Published private(set) var job: JobState = .idle
    @Published var mode: DropMode = .video { didSet { if mode != oldValue { chooseDefaultFormat() } } }
    @Published var formatID: String?
    @Published var videoPresetID = "original"
    @Published var audioPresetID = AudioPresets.shared.defaultPresetID

    let registry = SourceRegistry.shared
    private let downloader: Downloader
    private let runner: DropJobRunner
    private var fetchTask: Task<Void, Never>?
    private var jobTask: Task<Void, Never>?
    private var lastProgressUpdate = Date.distantPast

    /// Where downloads are saved (Settings arrive in M6.3).
    var outputFolder: URL { FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask)[0] }
    var maxBytes: Int64 { registry.download.defaultMaxBytes }

    init() {
        let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.0.0"
        downloader = Downloader(transport: URLSessionTransport(), appVersion: version)
        runner = DropJobRunner(downloader: downloader, converter: ConversionService.withBundledTools())
    }

    var media: Media? { if case .ready(let media) = fetch { media } else { nil } }
    var isBusy: Bool { if case .running = job { true } else { false } }

    // MARK: - Fetch

    func fetchLink() {
        let list = registry.links(in: text)
        guard let link = list.links.first else {
            let error: LinkError
            switch registry.link(from: text) {
            case .failure(let e): error = e
            case .success: error = list.problems.first?.error ?? .notALink
            }
            fetch = .failed(registry.message(for: error))
            return
        }
        fetchTask?.cancel()
        job = .idle
        fetch = .fetching
        fetchTask = Task { [downloader, maxBytes, registry] in
            do {
                let preview = try await downloader.inspect(link, maxBytes: maxBytes)
                guard !Task.isCancelled else { return }
                guard let source = FormatRegistry.shared.format(id: preview.formatID) else {
                    fetch = .failed(registry.message(for: .notMedia))
                    return
                }
                fetch = .ready(Media(link: link, preview: preview, source: source))
                mode = DropChoices.modes(for: source).first ?? .video
                chooseDefaultFormat()
            } catch let failure as DownloadFailure {
                guard !Task.isCancelled else { return }
                fetch = .failed(registry.message(for: failure.error))
            } catch {
                guard !Task.isCancelled else { return }
                fetch = .failed(registry.message(for: .network))
            }
        }
    }

    /// Pastes the clipboard's text into the field and fetches it.
    func pasteAndFetch() {
        guard let string = NSPasteboard.general.string(forType: .string) else { return }
        text = string.trimmingCharacters(in: .whitespacesAndNewlines)
        fetchLink()
    }

    /// Whether the clipboard holds something that looks like a link (for the "Paste Link" chip).
    var clipboardHasLink: Bool {
        guard let string = NSPasteboard.general.string(forType: .string) else { return false }
        return !registry.links(in: string).links.isEmpty
    }

    // MARK: - Output choices

    var modes: [DropMode] { media.map { DropChoices.modes(for: $0.source) } ?? [] }
    var formats: [FileFormat] { media.map { DropChoices.formats(for: $0.source, mode: mode) } ?? [] }

    /// True when the chosen format is the downloaded one, so the file is saved as is (no quality choice).
    var keepsOriginal: Bool { formatID != nil && formatID == media?.source.id }

    private func chooseDefaultFormat() {
        guard let media else { return }
        formatID = DropChoices.defaultFormat(for: media.source, mode: mode)?.id
    }

    // MARK: - Download

    func download() {
        guard let media, let formatID, !isBusy else { return }
        var options = DropOptions(outputFormatID: formatID == media.source.id ? nil : formatID)
        options.maxBytes = maxBytes
        options.settings = ConversionSettings(audio: AudioOptions(presetID: audioPresetID), video: VideoOptions(presetID: videoPresetID))
        job = .running(status: "Connecting…", fraction: nil, detail: nil)
        let folder = outputFolder
        jobTask = Task { [runner] in
            do {
                let outcome = try await runner.run(media.link, into: folder, options: options) { stage in
                    Task { @MainActor [weak self] in self?.show(stage) }
                }
                job = .done(outcome.files)
            } catch let error as DropError {
                if case .download(.interrupted, _) = error, Task.isCancelled { job = .idle; return }
                job = .failed(error.message(registry))
            } catch {
                job = .failed(registry.message(for: .interrupted))
            }
        }
    }

    func stop() {
        jobTask?.cancel()
    }

    func showInFinder(_ files: [URL]) {
        NSWorkspace.shared.activateFileViewerSelecting(files)
    }

    private func show(_ stage: DropStage) {
        guard isBusy else { return }
        switch stage {
        case .downloading(let progress):
            // Progress arrives per network chunk; ten updates a second is plenty.
            let now = Date()
            if now.timeIntervalSince(lastProgressUpdate) < 0.1, progress.bytes != progress.total { return }
            lastProgressUpdate = now
            let fraction = progress.total.map { Double(progress.bytes) / Double(max($0, 1)) }
            job = .running(status: "Downloading…", fraction: fraction, detail: Self.detail(progress))
        case .inspecting:
            job = .running(status: "Checking the file…", fraction: nil, detail: nil)
        case .converting(let fraction):
            job = .running(status: "Converting…", fraction: fraction, detail: nil)
        case .saving:
            job = .running(status: "Saving…", fraction: nil, detail: nil)
        }
    }

    /// "12.3 MB of 48 MB · 2.1 MB/s · 17 s left"
    static func detail(_ progress: DownloadProgress) -> String {
        let bytes = ByteCountFormatter.string(fromByteCount: progress.bytes, countStyle: .file)
        var parts = [progress.total.map { "\(bytes) of \(ByteCountFormatter.string(fromByteCount: $0, countStyle: .file))" } ?? bytes]
        if progress.bytesPerSecond > 0 {
            parts.append("\(ByteCountFormatter.string(fromByteCount: Int64(progress.bytesPerSecond), countStyle: .file))/s")
            if let total = progress.total, total > progress.bytes {
                let seconds = Double(total - progress.bytes) / progress.bytesPerSecond
                let formatter = DateComponentsFormatter()
                formatter.unitsStyle = .abbreviated
                formatter.allowedUnits = seconds >= 3600 ? [.hour, .minute] : seconds >= 60 ? [.minute, .second] : [.second]
                formatter.maximumUnitCount = 2
                if let left = formatter.string(from: max(seconds, 1)) { parts.append("\(left) left") }
            }
        }
        return parts.joined(separator: " · ")
    }
}
