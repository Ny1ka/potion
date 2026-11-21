import Foundation

/// Errors shown to the user, worded per the design system's voice rules (design-system/docs/01-brand.md).
/// `detail` carries technical output for the local log only; it is never shown as the message.
public enum ConversionError: Error, Equatable, Sendable, LocalizedError {
    case toolsMissing
    case unsupported(from: String, to: String)
    case unreadable(fileName: String)
    case copyProtected
    case noAudio(fileName: String)
    case cannotWrite(folder: String)
    case conversionFailed(fileName: String, detail: String)
    case verificationFailed(fileName: String)

    public var errorDescription: String? {
        switch self {
        case .toolsMissing:
            return "Potion's conversion tools are missing. Reinstall Potion to restore them."
        case let .unsupported(from, to):
            return "\(from) can't be converted to \(to)."
        case let .unreadable(name):
            return "Can't read “\(name)”. The file may be damaged."
        case .copyProtected:
            return "This file is copy-protected, so Potion can't convert it."
        case let .noAudio(name):
            return "“\(name)” has no audio to convert."
        case let .cannotWrite(folder):
            return "Can't save to “\(folder)”. Check that the folder exists and that you can write to it."
        case let .conversionFailed(name, _):
            return "Couldn't convert “\(name)”."
        case let .verificationFailed(name):
            return "The converted copy of “\(name)” didn't check out, so it wasn't saved."
        }
    }

    /// Technical detail for the local conversion log.
    public var logDetail: String? {
        if case let .conversionFailed(_, detail) = self { return detail }
        return nil
    }
}
