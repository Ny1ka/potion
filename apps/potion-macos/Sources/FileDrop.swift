import PotionCore
import SwiftUI
import UniformTypeIdentifiers

/// What a drag over the window would do.
enum DropState: Equatable {
    case idle
    /// `files` supported files, plus `folders` folders whose contents are checked on drop.
    case valid(files: Int, folders: Int)
    case invalid

    var isTargeted: Bool { self != .idle }

    var label: String {
        switch self {
        case .idle: return ""
        case .invalid: return "These files can't be converted"
        case let .valid(files, 0): return files == 1 ? "Drop to add 1 file" : "Drop to add \(files) files"
        case let .valid(0, folders): return folders == 1 ? "Drop to add 1 folder" : "Drop to add \(folders) folders"
        case let .valid(files, folders): return "Drop to add \(files + folders) items"
        }
    }
}

/// Accepts dropped files and folders anywhere in the window. While dragging, it reads the file URLs and checks
/// (up to 200 files) whether any can be converted, so the drop zone can say so before the drop.
struct FileDropDelegate: DropDelegate {
    @Binding var state: DropState
    let onDrop: ([URL]) -> Void

    func validateDrop(info: DropInfo) -> Bool { info.hasItemsConforming(to: [.fileURL]) }

    func dropEntered(info: DropInfo) {
        state = .valid(files: info.itemProviders(for: [.fileURL]).count, folders: 0)
        let providers = info.itemProviders(for: [.fileURL])
        Task {
            let urls = await Self.urls(from: providers)
            let classified = await Task.detached { Self.classify(urls) }.value
            // Only update if the drag is still over the window.
            await MainActor.run { if state.isTargeted { state = classified } }
        }
    }

    func dropUpdated(info: DropInfo) -> DropProposal? {
        DropProposal(operation: state == .invalid ? .forbidden : .copy)
    }

    func dropExited(info: DropInfo) { state = .idle }

    func performDrop(info: DropInfo) -> Bool {
        let accepted = state != .invalid
        state = .idle
        guard accepted else { return false }
        let providers = info.itemProviders(for: [.fileURL])
        Task {
            let urls = await Self.urls(from: providers)
            await MainActor.run { onDrop(urls) }
        }
        return true
    }

    static func urls(from providers: [NSItemProvider]) async -> [URL] {
        var urls: [URL] = []
        for provider in providers {
            let url: URL? = await withCheckedContinuation { continuation in
                _ = provider.loadObject(ofClass: URL.self) { url, _ in continuation.resume(returning: url) }
            }
            if let url, url.isFileURL { urls.append(url) }
        }
        return urls
    }

    nonisolated static func classify(_ urls: [URL]) -> DropState {
        var files = 0, folders = 0, checked = 0
        for url in urls {
            let values = try? url.resourceValues(forKeys: [.isDirectoryKey, .isPackageKey])
            if values?.isDirectory == true, values?.isPackage != true { folders += 1; continue }
            checked += 1
            if checked > 200 { files += 1; continue }   // don't read hundreds of headers mid-drag
            if let format = (try? FormatRegistry.shared.detect(fileAt: url))?.format,
               !ConversionMatrix.shared.outputs(for: format.id).isEmpty { files += 1 }
        }
        return files + folders == 0 ? .invalid : .valid(files: files, folders: folders)
    }
}
