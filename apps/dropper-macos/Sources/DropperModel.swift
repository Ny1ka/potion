import AppKit
import Combine
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
        /// Several links: they're queued with one choice of mode and format, without fetching each first.
        case batch([DropLink])
    }

    @Published var text = ""
    @Published private(set) var fetch: FetchState = .idle
    /// Batch only: nil keeps each file's downloaded format.
    @Published var batchFormatID: String?
    @Published var mode: DropMode = .video {
        didSet { if mode != oldValue { chooseDefaultFormat(); batchFormatID = nil } }
    }
    @Published var formatID: String?
    @Published var videoPresetID = "original"
    @Published var audioPresetID = AudioPresets.shared.defaultPresetID

    let registry = SourceRegistry.shared
    let queue: DropQueue
    let history: DropHistory
    private let downloader: Downloader
    private var fetchTask: Task<Void, Never>?
    private var queueWatch: AnyCancellable?

    /// Where downloads are saved (Settings arrive in M6.3).
    var outputFolder: URL { FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask)[0] }
    var maxBytes: Int64 { registry.download.defaultMaxBytes }

    init() {
        let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.0.0"
        let downloader = Downloader(transport: URLSessionTransport(), appVersion: version)
        let runner = DropJobRunner(downloader: downloader, converter: ConversionService.withBundledTools())
        let folder = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask)[0]
        self.downloader = downloader
        queue = DropQueue { link, options, resume, stage in
            try await runner.run(link, into: folder, options: options, resume: resume, stage: stage)
        }
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        history = DropHistory(url: support.appendingPathComponent("Potion Dropper/history.json"))
        queue.onItemFinished = { [weak self] item in self?.record(item) }
        queue.onBatchFinished = { done, failed in Notifications.batchFinished(done: done, failed: failed) }
        queueWatch = queue.$items.sink { items in
            let remaining = items.filter { $0.status == .waiting || $0.isRunning }.count
            NSApp?.dockTile.badgeLabel = remaining > 0 ? "\(remaining)" : nil   // 09-platforms.md
        }
    }

    var media: Media? { if case .ready(let media) = fetch { media } else { nil } }
    var batchLinks: [DropLink] { if case .batch(let links) = fetch { links } else { [] } }

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
        if list.links.count > 1 {
            fetch = .batch(list.links)
            if mode == .audio, batchFormatID == nil { batchFormatID = "mp3" }
            return
        }
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

    /// Formats offered for a batch: every format of the mode's kind (nil = keep each file's downloaded format).
    var batchFormats: [FileFormat] {
        FormatRegistry.shared.formats.filter { $0.category == (mode == .video ? .video : .audio) && $0.isAvailable(on: ConversionMatrix.currentPlatform) }
    }

    private var settings: ConversionSettings {
        ConversionSettings(audio: AudioOptions(presetID: audioPresetID), video: VideoOptions(presetID: videoPresetID))
    }

    /// Adds the fetched link (or every link of a batch) to the queue and clears the link bar for the next one.
    func download() {
        Notifications.requestPermissionIfNeeded()
        if let media, let formatID {
            var options = DropOptions(outputFormatID: formatID == media.source.id ? nil : formatID, settings: settings)
            options.maxBytes = maxBytes
            queue.add(media.link, options: options, title: media.preview.fileName, formatID: media.source.id)
        } else if !batchLinks.isEmpty {
            var options = DropOptions(outputFormatID: batchFormatID, settings: settings)
            options.maxBytes = maxBytes
            for link in batchLinks { queue.add(link, options: options) }
        } else {
            return
        }
        text = ""
        fetch = .idle
    }

    func showInFinder(_ files: [URL]) {
        let existing = files.filter { FileManager.default.fileExists(atPath: $0.path) }
        if !existing.isEmpty { NSWorkspace.shared.activateFileViewerSelecting(existing) }
    }

    /// History keeps what the user can see and clear: finished and failed downloads (spec Part 6.1.4).
    private func record(_ item: DropQueue.Item) {
        let entry: DropHistory.Entry
        switch item.status {
        case .done(let files):
            entry = .init(link: item.link.string, site: item.link.host, name: files.first?.lastPathComponent ?? item.title,
                          files: files.map(\.path), formatID: item.formatID)
        case .failed(let message):
            entry = .init(link: item.link.string, site: item.link.host, name: item.title, error: message)
        default:
            return
        }
        try? history.add(entry)
        objectWillChange.send()
    }

    /// "12.3 MB of 48 MB · 2.1 MB/s · 17 s left"
    nonisolated static func detail(_ progress: DownloadProgress) -> String {
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
