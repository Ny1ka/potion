import Foundation

/// Output file naming and safe saving. Converted files are written to a hidden temporary file in the
/// destination folder, verified, then moved to a name that doesn't exist yet. Nothing is ever overwritten.
public enum OutputFiles {
    /// "Interview.mp3", then "Interview 2.mp3", "Interview 3.mp3"… (Finder's style).
    public static func uniqueURL(in directory: URL, baseName: String, fileExtension: String,
                                 fileManager: FileManager = .default) -> URL {
        var n = 1
        while true {
            let name = n == 1 ? baseName : "\(baseName) \(n)"
            let url = directory.appendingPathComponent(name).appendingPathExtension(fileExtension)
            if !fileManager.fileExists(atPath: url.path) { return url }
            n += 1
        }
    }

    /// A hidden temporary file next to where the output will go (same volume, so the final move is atomic).
    static func temporaryURL(in directory: URL, fileExtension: String) -> URL {
        directory.appendingPathComponent(".potion-\(UUID().uuidString)").appendingPathExtension(fileExtension)
    }

    /// Moves a finished temporary file to a unique final name, retrying if another file claims the name first.
    static func finalize(_ temporary: URL, in directory: URL, baseName: String, fileExtension: String,
                         fileManager: FileManager = .default) throws -> URL {
        for _ in 0..<100 {
            let target = uniqueURL(in: directory, baseName: baseName, fileExtension: fileExtension, fileManager: fileManager)
            do {
                try fileManager.moveItem(at: temporary, to: target)   // fails instead of replacing an existing file
                return target
            } catch CocoaError.fileWriteFileExists {
                continue
            }
        }
        throw ConversionError.cannotWrite(folder: directory.lastPathComponent)
    }

    /// Checks that the destination folder exists and is writable.
    static func checkWritable(_ directory: URL, fileManager: FileManager = .default) throws {
        var isDir: ObjCBool = false
        guard fileManager.fileExists(atPath: directory.path, isDirectory: &isDir), isDir.boolValue,
              fileManager.isWritableFile(atPath: directory.path) else {
            throw ConversionError.cannotWrite(folder: directory.lastPathComponent)
        }
    }
}
