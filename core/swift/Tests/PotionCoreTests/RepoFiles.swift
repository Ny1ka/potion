import Foundation

/// Locates files in the repository (shared spec, design system) from tests.
enum RepoFiles {
    static let root: URL = {
        var dir = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
        while dir.path != "/" {
            if FileManager.default.fileExists(atPath: dir.appendingPathComponent("global.json").path) { return dir }
            dir.deleteLastPathComponent()
        }
        fatalError("Repository root (global.json) not found above \(#filePath)")
    }()

    static func url(_ relativePath: String) -> URL { root.appendingPathComponent(relativePath) }
    static func data(_ relativePath: String) throws -> Data { try Data(contentsOf: url(relativePath)) }
}
