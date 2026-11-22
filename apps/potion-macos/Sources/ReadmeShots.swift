#if DEBUG
import AppKit
import PotionCore
import SwiftUI
import UniformTypeIdentifiers

/// Debug builds only: renders the README screenshots from the real app (scripts/readme/capture.sh). The app sets up
/// each scene with generated sample files, then snapshots its own windows (no screen recording, so no permission
/// prompt). Settings go to a throwaway preferences domain, never the user's.
///
///   POTION_README_SHOTS=<folder>   where the PNGs go (turns this mode on)
///   POTION_README_THEME=light|dark
///   POTION_README_SCENE=onboarding|main
@MainActor
enum ReadmeShots {
    static let environment = ProcessInfo.processInfo.environment
    static var outputFolder: URL? { environment["POTION_README_SHOTS"].map { URL(fileURLWithPath: $0, isDirectory: true) } }
    static var isActive: Bool { outputFolder != nil }
    static var theme: String { environment["POTION_README_THEME"] == "dark" ? "dark" : "light" }
    static let domain = "com.ny1ka.potion.readme-shots"
    static let defaults: UserDefaults = {
        UserDefaults.standard.removePersistentDomain(forName: domain)
        return UserDefaults(suiteName: domain)!
    }()

    static func start(queue: QueueModel) {
        NSApp.appearance = NSAppearance(named: theme == "dark" ? .darkAqua : .aqua)
        Task {
            // Active windows draw colored traffic lights and a dark title, like a window the user is working in.
            NSApp.activate(ignoringOtherApps: true)
            try? await pause(0.5)
            mainWindow?.makeKeyAndOrderFront(nil)
            do {
                if environment["POTION_README_SCENE"] == "onboarding" {
                    try await pause(2)
                    try capture("onboarding")
                } else {
                    try await mainScenes(queue)
                }
            } catch {
                FileHandle.standardError.write(Data("README shots failed: \(error)\n".utf8))
            }
            UserDefaults.standard.removePersistentDomain(forName: domain)
            exit(0)
        }
    }

    // MARK: - Scenes

    private static func mainScenes(_ queue: QueueModel) async throws {
        try await pause(1.5)
        try capture("drop-zone")

        let samples = try await makeSamples(tools: queue.service.tools)
        queue.add(samples.map(\.url))
        try await waitUntil { queue.items.count == samples.count }
        for sample in samples {
            if let item = queue.items.first(where: { $0.url.lastPathComponent == sample.url.lastPathComponent }) {
                queue.setOutput(sample.output, for: [item.id])
            }
        }
        queue.selection = []
        try await pause(0.8)
        try capture("queue")

        for (scene, file) in [("options-video", "Holiday clip.mov"), ("options-image", "Beach.heic"), ("options-pdf", "Slides.pdf")] {
            guard let item = queue.items.first(where: { $0.url.lastPathComponent == file }) else { continue }
            queue.selection = [item.id]
            queue.showOptions(for: [item.id])
            try await pause(1.2)
            try capture(scene)
            queue.sheet = nil
            try await pause(0.8)
        }
        queue.selection = []

        queue.convertAll()
        var frame = 0
        try await waitUntil(timeout: 10) { queue.items.contains { if case .converting(let p) = $0.status { p > 0.15 } else { false } } }
        while queue.isConverting && frame < 6 {
            try capture("converting-\(frame)", scene: "frames")
            if frame == 1 { try capture("converting") }
            frame += 1
            try await pause(0.35)
        }
        try await waitUntil(timeout: 120) { !queue.isConverting }
        try await pause(0.6)
        try capture("done")

        let before = Set(NSApp.windows.map(ObjectIdentifier.init))
        // The app menu's "Settings…" item (⌘,), which is what SettingsLink opens on macOS 14 and later.
        if let menu = NSApp.mainMenu?.items.first?.submenu,
           let index = menu.items.firstIndex(where: { $0.keyEquivalent == "," && $0.keyEquivalentModifierMask == .command }) {
            menu.performActionForItem(at: index)
        }
        try await waitUntil(timeout: 5) { NSApp.windows.contains { $0.isVisible && !before.contains(ObjectIdentifier($0)) } }
        try await pause(1)
        if let settings = NSApp.windows.first(where: { $0.isVisible && !before.contains(ObjectIdentifier($0)) }) {
            settings.makeKeyAndOrderFront(nil)
            try await pause(0.4)
            try capture("settings", window: settings)
        }
    }

    // MARK: - Sample files (generated, so no real files appear in the README)

    private struct Sample { let url: URL; let output: String }

    private static func makeSamples(tools: FFmpegTools?) async throws -> [Sample] {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("Potion samples \(UUID().uuidString.prefix(6))", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        var samples: [Sample] = []
        if let tools {
            let video = folder.appendingPathComponent("Holiday clip.mov")
            try run(tools.ffmpeg, ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=30:duration=20",
                                   "-f", "lavfi", "-i", "sine=frequency=330:duration=20", "-c:v", "mpeg4", "-q:v", "3", "-c:a", "pcm_s16le",
                                   "-shortest", video.path])
            samples.append(Sample(url: video, output: "mp4"))
            let audio = folder.appendingPathComponent("Podcast episode.wav")
            try run(tools.ffmpeg, ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=220:duration=90",
                                   "-ac", "2", "-c:a", "pcm_s16le", audio.path])
            samples.append(Sample(url: audio, output: "mp3"))
        }
        let photo = folder.appendingPathComponent("Beach.heic")
        try writeImage(gradient: [(0.99, 0.62, 0.26), (0.98, 0.31, 0.48), (0.18, 0.83, 0.75)], size: CGSize(width: 3000, height: 2000),
                       to: photo, type: .heic)
        samples.append(Sample(url: photo, output: "jpg"))
        let screenshot = folder.appendingPathComponent("Design notes.png")
        try writeImage(gradient: [(0.49, 0.42, 1.0), (0.18, 0.83, 0.75)], size: CGSize(width: 1600, height: 1000), to: screenshot, type: .png)
        samples.append(Sample(url: screenshot, output: "webp"))

        let rtf = folder.appendingPathComponent("Report.rtf")
        let text = NSMutableAttributedString(string: "Quarterly report\n", attributes: [.font: NSFont.boldSystemFont(ofSize: 20)])
        text.append(NSAttributedString(string: String(repeating: "Potion converts files on this computer. Nothing is uploaded. ", count: 40),
                                       attributes: [.font: NSFont.systemFont(ofSize: 12)]))
        try text.rtf(from: NSRange(location: 0, length: text.length), documentAttributes: [:])?.write(to: rtf)
        let service = ConversionService(tools: tools)
        if let rtfFormat = FormatRegistry.shared.format(id: "rtf"), let docx = FormatRegistry.shared.format(id: "docx") {
            _ = try await service.convert(rtf, from: rtfFormat, to: docx, outputDirectory: folder)
            try? FileManager.default.removeItem(at: rtf)
            samples.append(Sample(url: folder.appendingPathComponent("Report.docx"), output: "pdf"))
        }

        let pdf = folder.appendingPathComponent("Slides.pdf")
        try writeSlides(to: pdf)
        samples.append(Sample(url: pdf, output: "png"))
        return samples
    }

    private static func run(_ executable: URL, _ arguments: [String]) throws {
        let process = Process()
        process.executableURL = executable
        process.arguments = arguments
        try process.run()
        process.waitUntilExit()
        guard process.terminationStatus == 0 else { throw CocoaError(.fileWriteUnknown) }
    }

    private static func writeImage(gradient stops: [(CGFloat, CGFloat, CGFloat)], size: CGSize, to url: URL, type: UTType) throws {
        let space = CGColorSpace(name: CGColorSpace.sRGB)!
        guard let context = CGContext(data: nil, width: Int(size.width), height: Int(size.height), bitsPerComponent: 8, bytesPerRow: 0,
                                      space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { throw CocoaError(.fileWriteUnknown) }
        let colors = stops.map { CGColor(red: $0.0, green: $0.1, blue: $0.2, alpha: 1) } as CFArray
        let gradient = CGGradient(colorsSpace: space, colors: colors, locations: nil)!
        context.drawLinearGradient(gradient, start: .zero, end: CGPoint(x: size.width, y: size.height), options: [])
        guard let image = context.makeImage(),
              let destination = CGImageDestinationCreateWithURL(url as CFURL, type.identifier as CFString, 1, nil) else { throw CocoaError(.fileWriteUnknown) }
        CGImageDestinationAddImage(destination, image, nil)
        guard CGImageDestinationFinalize(destination) else { throw CocoaError(.fileWriteUnknown) }
    }

    private static func writeSlides(to url: URL) throws {
        var box = CGRect(x: 0, y: 0, width: 960, height: 540)
        guard let context = CGContext(url as CFURL, mediaBox: &box, nil) else { throw CocoaError(.fileWriteUnknown) }
        let slides: [(String, CGColor)] = [("Welcome", CGColor(red: 1, green: 0.31, blue: 0.48, alpha: 1)),
                                            ("Roadmap", CGColor(red: 1, green: 0.62, blue: 0.26, alpha: 1)),
                                            ("Thank you", CGColor(red: 0.49, green: 0.42, blue: 1, alpha: 1))]
        for (title, color) in slides {
            context.beginPDFPage(nil)
            context.setFillColor(color)
            context.fill(box)
            let line = CTLineCreateWithAttributedString(NSAttributedString(string: title, attributes: [
                .font: NSFont.boldSystemFont(ofSize: 64), .foregroundColor: NSColor.white]))
            context.textPosition = CGPoint(x: 72, y: 240)
            CTLineDraw(line, context)
            context.endPDFPage()
        }
        context.closePDF()
    }

    // MARK: - Capturing

    private static var mainWindow: NSWindow? {
        NSApp.windows.first { $0.isVisible && $0.sheetParent == nil && $0.identifier?.rawValue.contains("main") == true }
            ?? NSApp.windows.first { $0.isVisible && $0.sheetParent == nil && $0.title == "Potion" }
    }

    /// Snapshots a window and its attached sheets into one PNG with rounded corners and a soft shadow.
    private static func capture(_ name: String, window: NSWindow? = nil, scene: String = "") throws {
        guard let base = window ?? mainWindow, let output = outputFolder else { throw CocoaError(.fileNoSuchFile) }
        let windows = [base] + NSApp.windows.filter { $0.isVisible && $0.sheetParent === base }
        let frame = windows.map(\.frame).reduce(base.frame) { $0.union($1) }
        let scale = base.backingScaleFactor
        let pad: CGFloat = 40
        let size = CGSize(width: frame.width + pad * 2, height: frame.height + pad * 2)
        guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(size.width * scale), pixelsHigh: Int(size.height * scale),
                                         bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                         colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0) else { throw CocoaError(.fileWriteUnknown) }
        rep.size = size
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        for window in windows {
            guard let view = window.contentView?.superview ?? window.contentView,
                  let snapshot = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { continue }
            view.cacheDisplay(in: view.bounds, to: snapshot)
            let rect = CGRect(x: window.frame.minX - frame.minX + pad, y: window.frame.minY - frame.minY + pad,
                              width: window.frame.width, height: window.frame.height)
            let path = NSBezierPath(roundedRect: rect, xRadius: 10, yRadius: 10)
            NSGraphicsContext.saveGraphicsState()
            let shadow = NSShadow()
            shadow.shadowColor = NSColor.black.withAlphaComponent(0.28)
            shadow.shadowBlurRadius = 24
            shadow.shadowOffset = NSSize(width: 0, height: -8)
            shadow.set()
            NSColor.windowBackgroundColor.setFill()
            path.fill()
            NSGraphicsContext.restoreGraphicsState()
            NSGraphicsContext.saveGraphicsState()
            path.addClip()
            snapshot.draw(in: rect)
            NSGraphicsContext.restoreGraphicsState()
        }
        NSGraphicsContext.restoreGraphicsState()
        let folder = scene.isEmpty ? output : output.appendingPathComponent("\(scene)-\(theme)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try rep.representation(using: .png, properties: [:])?.write(to: folder.appendingPathComponent("\(name)-\(theme).png"))
    }

    private static func pause(_ seconds: Double) async throws {
        try await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
    }

    private static func waitUntil(timeout: Double = 20, _ condition: () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        while !condition() {
            guard Date() < deadline else { throw CocoaError(.userCancelled) }
            try await pause(0.1)
        }
    }
}
#endif
