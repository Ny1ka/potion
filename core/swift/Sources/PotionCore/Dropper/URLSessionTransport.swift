import Foundation

/// The macOS transport: one URLSession task per request, with no cookies, no credential storage, no cache and no
/// automatic redirects (the `Downloader` follows them itself, after checking each one against the allowlist).
/// The body streams to the caller as it arrives. Proxies follow the system settings.
public struct URLSessionTransport: HTTPTransport {
    /// Seconds without any data (connecting, waiting for headers, or between body bytes) before giving up.
    public let timeout: TimeInterval

    public init(timeoutSeconds: Int = SourceRegistry.shared.download.timeoutSeconds) {
        timeout = TimeInterval(timeoutSeconds)
    }

    static func configuration(timeout: TimeInterval) -> URLSessionConfiguration {
        let config = URLSessionConfiguration.ephemeral
        config.httpCookieStorage = nil
        config.httpCookieAcceptPolicy = .never
        config.httpShouldSetCookies = false
        config.urlCredentialStorage = nil
        config.urlCache = nil
        config.requestCachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        config.timeoutIntervalForRequest = timeout
        config.waitsForConnectivity = false
        return config
    }

    public func get(_ request: HTTPRequest) async throws -> HTTPResponse {
        guard let url = URL(string: request.url) else { throw TransportError.network }
        var urlRequest = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalAndRemoteCacheData, timeoutInterval: timeout)
        urlRequest.httpShouldHandleCookies = false
        for (name, value) in request.headers { urlRequest.setValue(value, forHTTPHeaderField: name) }

        let delegate = StreamingDelegate()
        let session = URLSession(configuration: Self.configuration(timeout: timeout), delegate: delegate, delegateQueue: nil)
        let task = session.dataTask(with: urlRequest)
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                delegate.start(task, session: session, continuation: continuation)
            }
        } onCancel: {
            task.cancel()
        }
    }
}

/// Hands the response to the waiting caller, then yields body chunks into an AsyncThrowingStream.
private final class StreamingDelegate: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    private let lock = NSLock()
    private var responseContinuation: CheckedContinuation<HTTPResponse, Error>?
    private var bodyContinuation: AsyncThrowingStream<Data, Error>.Continuation?
    private var body: AsyncThrowingStream<Data, Error>?

    func start(_ task: URLSessionDataTask, session: URLSession, continuation: CheckedContinuation<HTTPResponse, Error>) {
        let stream = AsyncThrowingStream<Data, Error> { body in
            body.onTermination = { termination in
                if case .cancelled = termination { task.cancel() }
            }
            lock.withLock { bodyContinuation = body }
        }
        lock.withLock {
            responseContinuation = continuation
            self.body = stream
        }
        task.resume()
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        let http = response as? HTTPURLResponse
        var headers: [String: String] = [:]
        for (key, value) in http?.allHeaderFields ?? [:] {
            if let key = key as? String { headers[key] = "\(value)" }
        }
        let (continuation, stream) = lock.withLock { () -> (CheckedContinuation<HTTPResponse, Error>?, AsyncThrowingStream<Data, Error>?) in
            defer { responseContinuation = nil }
            return (responseContinuation, body)
        }
        if let continuation, let stream {
            continuation.resume(returning: HTTPResponse(status: http?.statusCode ?? 0, headers: headers, body: stream))
        }
        completionHandler(.allow)
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        _ = lock.withLock { bodyContinuation }?.yield(data)
    }

    /// Redirects come back to the Downloader as responses.
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }

    /// Server trust is checked normally; sign-in challenges are refused, so a 401 reaches the Downloader as is.
    func urlSession(_ session: URLSession, task: URLSessionTask, didReceive challenge: URLAuthenticationChallenge,
                    completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        if challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust {
            completionHandler(.performDefaultHandling, nil)
        } else {
            completionHandler(.rejectProtectionSpace, nil)
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        let (continuation, body) = lock.withLock { () -> (CheckedContinuation<HTTPResponse, Error>?, AsyncThrowingStream<Data, Error>.Continuation?) in
            defer { responseContinuation = nil; bodyContinuation = nil }
            return (responseContinuation, bodyContinuation)
        }
        let mapped = error.map(Self.map)
        if let continuation {
            continuation.resume(throwing: mapped ?? TransportError.network)
        }
        if let mapped { body?.finish(throwing: mapped) } else { body?.finish() }
        session.finishTasksAndInvalidate()   // releases this delegate
    }

    private static func map(_ error: Error) -> Error {
        guard let error = error as? URLError else { return TransportError.network }
        switch error.code {
        case .timedOut: return TransportError.timedOut
        case .cancelled: return CancellationError()
        default: return TransportError.network
        }
    }
}
