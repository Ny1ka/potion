import Foundation
import Network

/// A tiny HTTP/1.1 server on 127.0.0.1 for transport tests: one request per connection, answered by a handler.
/// Never reachable from outside this machine.
final class LoopbackServer: @unchecked Sendable {
    struct Request {
        let method: String
        let path: String
        /// Header names lowercased.
        let headers: [String: String]
    }
    struct Reply {
        var status = 200
        var reason = "OK"
        var headers: [String: String] = [:]
        var body = Data()
        /// Accept the connection but never answer (for timeouts).
        var hang = false
        /// Send the headers and this many body bytes, then drop the connection.
        var dropAfter: Int?
    }

    private let listener: NWListener
    private let queue = DispatchQueue(label: "potion.loopback")
    private let lock = NSLock()
    private var _requests: [Request] = []
    private var handler: (Request) -> Reply = { _ in Reply(status: 404, reason: "Not Found") }
    private var held: [NWConnection] = []

    var requests: [Request] { lock.withLock { _requests } }
    private(set) var port: UInt16 = 0

    init() throws {
        let parameters = NWParameters.tcp
        parameters.requiredLocalEndpoint = NWEndpoint.hostPort(host: "127.0.0.1", port: .any)
        listener = try NWListener(using: parameters)
    }

    func start(_ handler: @escaping (Request) -> Reply) async throws {
        lock.withLock { self.handler = handler }
        listener.newConnectionHandler = { [weak self] connection in self?.accept(connection) }
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            listener.stateUpdateHandler = { [weak self] state in
                guard let self else { return }
                switch state {
                case .ready:
                    self.port = self.listener.port?.rawValue ?? 0
                    self.listener.stateUpdateHandler = nil
                    continuation.resume()
                case .failed(let error):
                    self.listener.stateUpdateHandler = nil
                    continuation.resume(throwing: error)
                default:
                    break
                }
            }
            listener.start(queue: queue)
        }
    }

    func stop() {
        listener.cancel()
        lock.withLock { held.forEach { $0.cancel() }; held = [] }
    }

    func url(_ path: String) -> String { "http://127.0.0.1:\(port)\(path)" }

    private func accept(_ connection: NWConnection) {
        connection.start(queue: queue)
        receive(connection, buffer: Data())
    }

    private func receive(_ connection: NWConnection, buffer: Data) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 65536) { [weak self] data, _, complete, error in
            guard let self else { return }
            var buffer = buffer
            if let data { buffer.append(data) }
            if let end = buffer.range(of: Data("\r\n\r\n".utf8)) {
                self.respond(connection, head: String(decoding: buffer[..<end.lowerBound], as: UTF8.self))
            } else if complete || error != nil {
                connection.cancel()
            } else {
                self.receive(connection, buffer: buffer)
            }
        }
    }

    private func respond(_ connection: NWConnection, head: String) {
        let lines = head.components(separatedBy: "\r\n")
        let start = lines.first?.split(separator: " ") ?? []
        var headers: [String: String] = [:]
        for line in lines.dropFirst() {
            guard let colon = line.firstIndex(of: ":") else { continue }
            headers[line[..<colon].lowercased()] = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
        }
        let request = Request(method: start.first.map(String.init) ?? "", path: start.count > 1 ? String(start[1]) : "", headers: headers)
        let reply = lock.withLock { () -> Reply in
            _requests.append(request)
            return handler(request)
        }
        if reply.hang {
            lock.withLock { held.append(connection) }
            return
        }
        var text = "HTTP/1.1 \(reply.status) \(reply.reason)\r\n"
        var replyHeaders = reply.headers
        if replyHeaders["Content-Length"] == nil { replyHeaders["Content-Length"] = "\(reply.body.count)" }
        replyHeaders["Connection"] = "close"
        for (name, value) in replyHeaders { text += "\(name): \(value)\r\n" }
        text += "\r\n"
        var out = Data(text.utf8)
        out.append(reply.dropAfter.map { reply.body.prefix($0) } ?? reply.body)
        connection.send(content: out, completion: .contentProcessed { _ in
            if reply.dropAfter != nil {
                connection.cancel()   // a dropped connection
            } else {
                connection.send(content: nil, contentContext: .finalMessage, isComplete: true, completion: .contentProcessed { _ in connection.cancel() })
            }
        })
    }
}
