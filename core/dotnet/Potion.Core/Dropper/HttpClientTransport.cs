using System.Net;
using System.Net.Http.Headers;

namespace Potion.Core.Dropper;

/// <summary>
/// The Windows transport: HttpClient with no cookies, no credentials, no automatic redirects (the Downloader follows
/// them itself, after checking each one against the allowlist) and no decompression. The body streams to the
/// caller as it arrives, with an idle timeout per read. Proxies follow the system settings.
/// </summary>
public sealed class HttpClientTransport : IHttpTransport, IDisposable
{
    private readonly HttpClient _client;
    private readonly TimeSpan _timeout;

    /// <param name="timeoutSeconds">Seconds without progress (connecting, waiting for headers, or between body bytes).</param>
    public HttpClientTransport(int? timeoutSeconds = null)
    {
        _timeout = TimeSpan.FromSeconds(timeoutSeconds ?? SourceRegistry.Shared.Download.TimeoutSeconds);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            Credentials = null,
            PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = _timeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<TransportResponse> GetAsync(TransportRequest request, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)) throw new TransportException(timedOut: false);
        var message = new HttpRequestMessage(HttpMethod.Get, uri);
        foreach (var (name, value) in request.Headers) message.Headers.TryAddWithoutValidation(name, value);

        using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headersTimeout.CancelAfter(_timeout);
        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TransportException(timedOut: true);
        }
        catch (HttpRequestException e)
        {
            throw new TransportException(timedOut: e.InnerException is TimeoutException, e);
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(HttpHeaders source)
        {
            foreach (var (name, values) in source) headers[name] = string.Join(", ", values);
        }
        Add(response.Headers);
        Add(response.Content.Headers);
        Stream body;
        try { body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            response.Dispose();
            throw new TransportException(timedOut: false, e);
        }
        return new TransportResponse((int)response.StatusCode, headers, new IdleTimeoutStream(body, response, _timeout));
    }

    public void Dispose() => _client.Dispose();

    /// <summary>Reads the body with a timeout per read; transport failures become <see cref="TransportException"/>.</summary>
    private sealed class IdleTimeoutStream(Stream inner, HttpResponseMessage response, TimeSpan timeout) : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idle.CancelAfter(timeout);
            try { return await inner.ReadAsync(buffer, idle.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TransportException(timedOut: true); }
            catch (Exception e) when (e is IOException or HttpRequestException) { throw new TransportException(timedOut: false, e); }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
