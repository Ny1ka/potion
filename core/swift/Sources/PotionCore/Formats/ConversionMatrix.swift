import Foundation

/// What an engine does to produce an output. Engines (M2) dispatch on this.
public enum ConversionOperation: String, CaseIterable, Sendable {
    case transcodeVideo, videoToGif, extractAudio, transcodeAudio
    case convertImage, imageToPdf, gifToVideo
    case convertDocument, documentToPdf, pdfToDocument, pdfToImage
}

/// A limitation shown to the user before converting, e.g. "Only the text is kept."
public struct ConversionNote: Hashable, Sendable {
    public let id: String
    public let text: String
}

/// One output a file can be converted into.
public struct ConversionOption: Hashable, Sendable {
    public let output: FileFormat
    public let operation: ConversionOperation
    /// Limitations to show before converting, in spec order. Empty for clean conversions.
    public let notes: [ConversionNote]
}

/// Which conversions are allowed, loaded from `matrix.json` (core/spec). Only conversions listed here are
/// ever offered. The rules are specified in core/spec/README.md and implemented identically in core/dotnet.
public final class ConversionMatrix: Sendable {
    public enum LoadError: Error, Equatable {
        case missingResource
        case invalid(String)
    }

    /// The matrix built from the matrix.json bundled with PotionCore, over `FormatRegistry.shared`.
    public static let shared: ConversionMatrix = {
        do {
            guard let url = Bundle.module.url(forResource: "matrix", withExtension: "json") else { throw LoadError.missingResource }
            return try ConversionMatrix(data: Data(contentsOf: url), registry: .shared)
        } catch {
            preconditionFailure("PotionCore's bundled matrix.json is invalid: \(error)")
        }
    }()

    public let registry: FormatRegistry
    /// input id → output id → option
    private let table: [String: [String: ConversionOption]]

    private struct SpecFile: Decodable {
        let version: Int
        let groups: [String: [String]]
        let routes: [Route]
        let notes: [String: String]
    }

    private struct Route: Decodable {
        let from: [String]
        let to: [String]
        let operation: String?
        let note: String?
        let sameFormat: Bool?
    }

    public init(data: Data, registry: FormatRegistry) throws {
        let spec: SpecFile
        do { spec = try JSONDecoder().decode(SpecFile.self, from: data) } catch {
            throw LoadError.invalid("matrix.json can't be decoded: \(error)")
        }
        guard spec.version == 1 else { throw LoadError.invalid("Unsupported matrix.json version \(spec.version)") }

        func resolve(_ refs: [String]) throws -> [String] {
            var ids: [String] = []
            for ref in refs {
                if ref.hasPrefix("#") {
                    guard let category = FormatCategory(rawValue: String(ref.dropFirst())) else {
                        throw LoadError.invalid("Unknown category \(ref)")
                    }
                    ids += registry.formats.filter { $0.category == category }.map(\.id)
                } else if ref.hasPrefix("@") {
                    guard let group = spec.groups[String(ref.dropFirst())] else { throw LoadError.invalid("Unknown group \(ref)") }
                    ids += group
                } else {
                    ids.append(ref)
                }
            }
            for id in ids where registry.format(id: id) == nil { throw LoadError.invalid("Unknown format \(id)") }
            return ids
        }

        // Pass 1: operation routes create pairs. Pass 2: every route adds its note to pairs that exist.
        var operations: [String: [String: ConversionOperation]] = [:]
        var notes: [String: [String: [String]]] = [:]
        for route in spec.routes {
            if let note = route.note, spec.notes[note] == nil { throw LoadError.invalid("Unknown note \(note)") }
            guard let name = route.operation else { continue }
            guard let operation = ConversionOperation(rawValue: name) else { throw LoadError.invalid("Unknown operation \(name)") }
            for from in try resolve(route.from) {
                for to in try resolve(route.to) where to != from || route.sameFormat == true {
                    if let existing = operations[from]?[to], existing != operation {
                        throw LoadError.invalid("\(from) → \(to) has two operations: \(existing.rawValue) and \(name)")
                    }
                    operations[from, default: [:]][to] = operation
                }
            }
        }
        for route in spec.routes {
            guard let note = route.note else { continue }
            for from in try resolve(route.from) {
                for to in try resolve(route.to) where operations[from]?[to] != nil {
                    if notes[from]?[to]?.contains(note) != true { notes[from, default: [:]][to, default: []].append(note) }
                }
            }
        }

        var table: [String: [String: ConversionOption]] = [:]
        for (from, outputs) in operations {
            for (to, operation) in outputs {
                let noteList = (notes[from]?[to] ?? []).map { ConversionNote(id: $0, text: spec.notes[$0]!) }
                table[from, default: [:]][to] = ConversionOption(output: registry.format(id: to)!, operation: operation, notes: noteList)
            }
        }
        self.registry = registry
        self.table = table
    }

    /// The conversion from one format to another, or nil when it isn't allowed.
    public func conversion(from inputID: String, to outputID: String) -> ConversionOption? {
        table[inputID]?[outputID]
    }

    /// Everything an input can become: outputs in the input's own category first, then the rest,
    /// each in formats.json order.
    public func outputs(for inputID: String) -> [ConversionOption] {
        guard let input = registry.format(id: inputID), let options = table[inputID] else { return [] }
        let ordered = registry.formats.compactMap { options[$0.id] }
        return ordered.filter { $0.output.category == input.category } + ordered.filter { $0.output.category != input.category }
    }

    /// Outputs a whole batch can be converted to. An output qualifies when every input either converts to it
    /// or is already in that format (those files are skipped), and at least one input actually converts.
    /// Ordered like `outputs(for:)`, using the first input's category.
    public func commonOutputs(for inputIDs: [String]) -> [FileFormat] {
        guard let first = inputIDs.first.flatMap(registry.format(id:)) else { return [] }
        let qualifying = registry.formats.filter { output in
            inputIDs.allSatisfy { $0 == output.id || table[$0]?[output.id] != nil }
                && inputIDs.contains { table[$0]?[output.id] != nil }
        }
        return qualifying.filter { $0.category == first.category } + qualifying.filter { $0.category != first.category }
    }
}
