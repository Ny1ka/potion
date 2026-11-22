using System.Collections.Concurrent;
using System.Text;

namespace Potion.Core.Dropper;

/// <summary>A GET request. Transports never add cookies or credentials and never follow redirects themselves.</summary>
public sealed record TransportRequest(string Url, IReadOnlyDictionary<string, string> Headers);

/// <summary>A response: status and headers first, then the body as a stream.</summary>
public sealed class TransportResponse(int status, IReadOnlyDictionary<string, string> headers, Stream body) : IDisposable
{
    public int Status { get; } = status;
    /// <summary>Header names as the server sent them (matched without regard to case).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; } = headers;
    public Stream Body { get; } = body;

    public string? Header(string name) =>
        Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    public void Dispose() => Body.Dispose();
}

/// <summary>A transport failure, before or during a response.</summary>
public sealed class TransportException(bool timedOut, Exception? inner = null)
    : Exception(timedOut ? "The request timed out" : "The request failed", inner)
{
    public bool TimedOut { get; } = timedOut;
}

/// <summary>Sends one GET and hands back the response. The app uses HttpClient; tests script the answers.</summary>
public interface IHttpTransport
{
    Task<TransportResponse> GetAsync(TransportRequest request, CancellationToken cancellationToken);
}

/// <summary>Bytes so far, the expected total when the server says, and the average speed of this attempt.</summary>
public sealed record DownloadProgress(long Bytes, long? Total, double BytesPerSecond);

/// <summary>A partial download that can continue where it stopped (the server must send a validator for that).</summary>
/// <param name="File">The hidden partial file.</param>
/// <param name="Validator">ETag (strong) or Last-Modified, sent back as If-Range.</param>
/// <param name="Link">The link the user asked for (redirects are followed again, since signed redirect targets expire).</param>
public sealed record ResumeState(string File, long Bytes, string Validator, DropLink Link)
{
    /// <summary>Deletes the partial file when the download won't be resumed.</summary>
    public void Discard()
    {
        try { System.IO.File.Delete(File); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>A failed download, with what's needed to resume it when that's possible.</summary>
public sealed class DownloadFailureException(DownloadError error, ResumeState? resume) : Exception(error.Id)
{
    public DownloadError Error { get; } = error;
    public ResumeState? Resume { get; } = resume;
}

/// <summary>What the media card shows before downloading.</summary>
/// <param name="FormatId">The audio or video format found in the first bytes.</param>
/// <param name="Size">Bytes, when the server says.</param>
/// <param name="FinalLink">The link that answered, after redirects.</param>
/// <param name="Resumable">Whether an interrupted download could continue (the server sends a validator).</param>
public sealed record LinkPreview(string FileName, string FormatId, long? Size, DropLink FinalLink, bool Resumable);

/// <summary>A finished, checked download in a hidden file. The caller converts it or moves it to a unique name.</summary>
/// <param name="FormatId">The audio or video format found in the file's content.</param>
/// <param name="FileName">Suggested name from the server or the link (sanitized; its extension may not match FormatId).</param>
/// <param name="FinalLink">The link that served the file, after redirects.</param>
public sealed record DownloadResult(string File, string FormatId, string FileName, long Bytes, DropLink FinalLink);

/// <summary>
/// Downloads one allowlisted link (core/spec/README.md, "Downloads"): robots.txt per site, redirects re-checked
/// against the allowlist, retries with Retry-After, the size limit while streaming, the content check, resume with
/// Range/If-Range, and a hidden temporary file. core/swift's Downloader behaves the same.
/// </summary>
public sealed class Downloader
{
    private readonly SourceRegistry _registry;
    private readonly IHttpTransport _transport;
    private readonly string _userAgent;
    private readonly Func<int, CancellationToken, Task> _sleep;
    private readonly ConcurrentDictionary<string, RobotsRules> _robots = new();

    public Downloader(IHttpTransport transport, string appVersion, SourceRegistry? registry = null, Func<int, CancellationToken, Task>? sleep = null)
    {
        _registry = registry ?? SourceRegistry.Shared;
        _transport = transport;
        _userAgent = _registry.UserAgent(appVersion);
        _sleep = sleep ?? ((seconds, ct) => Task.Delay(TimeSpan.FromSeconds(Math.Max(0, seconds)), ct));
    }

    private DownloadSettings Settings => _registry.Download;

    private Dictionary<string, string> BaseHeaders() => new()
    {
        ["User-Agent"] = _userAgent,
        ["Accept"] = "*/*",
        ["Accept-Encoding"] = "identity",
    };

    /// <summary>
    /// Downloads link into a hidden file in folder. Throws <see cref="DownloadFailureException"/>; cancelling keeps
    /// the partial file for resuming when the server allows it.
    /// </summary>
    public async Task<DownloadResult> DownloadAsync(DropLink link, string folder, long maxBytes, ResumeState? resume = null,
        Action<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var opened = await OpenAsync(link, maxBytes, resume, cancellationToken).ConfigureAwait(false);
        using (opened.Response)
        {
            return await ReceiveAsync(opened.Response, link, opened.Current, folder, maxBytes, opened.Length,
                opened.Resumed ? opened.Partial : null, opened.Partial, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Fetches what the media card shows without downloading the file: the same checks as DownloadAsync (robots.txt,
    /// redirects, server answer, size limit, content check on the first bytes), then the connection is closed.
    /// </summary>
    public async Task<LinkPreview> InspectAsync(DropLink link, long maxBytes, CancellationToken cancellationToken = default)
    {
        var opened = await OpenAsync(link, maxBytes, null, cancellationToken).ConfigureAwait(false);
        using var response = opened.Response;
        var fileName = DownloadRules.FileName(response.Header("Content-Disposition"), opened.Current.Parts, Settings.MaxFileNameLength);
        var header = new byte[_registry.Formats.HeaderSize];
        var read = 0;
        try
        {
            while (read < header.Length)
            {
                var n = await response.Body.ReadAsync(header.AsMemory(read), cancellationToken).ConfigureAwait(false);
                if (n == 0) break;
                read += n;
            }
        }
        catch (OperationCanceledException) { throw new DownloadFailureException(DownloadError.Interrupted, null); }
        catch (Exception e) when (read == 0) { throw new DownloadFailureException(Map(e), null); }
        catch (Exception) { /* a short start is still enough to check */ }
        var (formatId, error) = DownloadRules.Sniff(header.AsSpan(0, read), fileName, _registry.Formats);
        if (formatId is null) throw new DownloadFailureException(error!, null);
        return new LinkPreview(fileName, formatId, opened.Length, opened.Current, Validator(response) is not null);
    }

    private sealed record Opened(TransportResponse Response, DropLink Current, long? Length, bool Resumed, ResumeState? Partial);

    /// <summary>Follows the link to an accepted answer: robots.txt, redirects re-checked against the allowlist, retries, and resume restarts.</summary>
    private async Task<Opened> OpenAsync(DropLink link, long maxBytes, ResumeState? resume, CancellationToken cancellationToken)
    {
        var current = link;
        var partial = resume;
        var redirects = 0;
        var retries = 0;
        DownloadFailureException Fail(DownloadError error)
        {
            partial?.Discard();
            return new DownloadFailureException(error, null);
        }

        while (true)
        {
            if (cancellationToken.IsCancellationRequested) throw new DownloadFailureException(DownloadError.Interrupted, partial);
            try { await CheckRobotsAsync(current, cancellationToken).ConfigureAwait(false); }
            catch (DownloadFailureException e) { throw Fail(e.Error); }
            catch (OperationCanceledException) { throw new DownloadFailureException(DownloadError.Interrupted, partial); }

            var headers = BaseHeaders();
            if (partial is { Bytes: > 0 })
            {
                headers["Range"] = $"bytes={partial.Bytes}-";
                headers["If-Range"] = partial.Validator;
            }
            TransportResponse response;
            try { response = await _transport.GetAsync(new TransportRequest(current.Text, headers), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw new DownloadFailureException(DownloadError.Interrupted, partial); }
            catch (Exception e) { throw new DownloadFailureException(Map(e), partial); }

            var verdict = DownloadRules.Classify(response.Status, response.Headers, headers.ContainsKey("Range"), maxBytes, Settings);
            if (verdict.Kind == VerdictKind.Accept) return new Opened(response, current, verdict.Length, verdict.Resumed, partial);
            using (response)
            {
                switch (verdict.Kind)
                {
                    case VerdictKind.Redirect:
                        if (++redirects > Settings.MaxRedirects) throw Fail(DownloadError.TooManyRedirects);
                        var (target, error) = _registry.Redirect(current, verdict.Location!);
                        current = target ?? throw Fail(DownloadError.From(error!.Value));
                        break;
                    case VerdictKind.Retry:
                        if (++retries > Settings.Retries) throw Fail(response.Status == 429 ? DownloadError.RateLimited : DownloadError.ServerError);
                        try { await _sleep(verdict.Seconds, cancellationToken).ConfigureAwait(false); }
                        catch (OperationCanceledException) { throw new DownloadFailureException(DownloadError.Interrupted, partial); }
                        break;
                    case VerdictKind.Restart:
                        partial?.Discard();
                        partial = null;
                        if (++retries > Settings.Retries) throw Fail(DownloadError.Refused);
                        break;
                    default:
                        throw Fail(verdict.Error!);
                }
            }
        }
    }

    private async Task<DownloadResult> ReceiveAsync(TransportResponse response, DropLink link, DropLink current, string folder, long maxBytes,
        long? length, ResumeState? resumeFrom, ResumeState? discard, Action<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var fileName = DownloadRules.FileName(response.Header("Content-Disposition"), current.Parts, Settings.MaxFileNameLength);
        string file;
        long received;
        if (resumeFrom is not null)
        {
            file = resumeFrom.File;
            received = resumeFrom.Bytes;
        }
        else
        {
            discard?.Discard();
            file = Path.Combine(folder, $".potion-dropper-{Guid.NewGuid()}.part");
            received = 0;
        }
        var validator = Validator(response);
        var headerSize = _registry.Formats.HeaderSize;
        FileStream? stream = null;
        DownloadFailureException Failure(DownloadError error, bool keep)
        {
            stream?.Dispose();
            stream = null;
            if (keep && validator is not null && received > 0) return new DownloadFailureException(error, new ResumeState(file, received, validator, link));
            try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return new DownloadFailureException(error, null);
        }

        long? total = length + received;
        if (total > maxBytes) throw Failure(DownloadError.TooLarge, keep: false);

        var header = new List<byte>();
        try
        {
            stream = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (received > 0)
            {
                var buffer = new byte[(int)Math.Min(headerSize, received)];
                var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
                header.AddRange(buffer.AsSpan(0, read).ToArray());
            }
            stream.SetLength(received);
            stream.Seek(received, SeekOrigin.Begin);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            throw Failure(DownloadError.Interrupted, keep: false);
        }

        string? formatId = null;
        void Check()
        {
            var (id, error) = DownloadRules.Sniff(header.ToArray(), fileName, _registry.Formats);
            formatId = id ?? throw Failure(error!, keep: false);
        }
        if (header.Count >= headerSize) Check();

        var started = DateTime.UtcNow;
        long thisAttempt = 0;
        var chunk = new byte[81920];
        try
        {
            while (true)
            {
                var n = await response.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (n == 0) break;
                await stream.WriteAsync(chunk.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                received += n;
                thisAttempt += n;
                if (received > maxBytes) throw Failure(DownloadError.TooLarge, keep: false);
                if (formatId is null && header.Count < headerSize)
                {
                    header.AddRange(chunk.AsSpan(0, Math.Min(n, headerSize - header.Count)).ToArray());
                    if (header.Count >= headerSize) Check();
                }
                var elapsed = Math.Max((DateTime.UtcNow - started).TotalSeconds, 0.001);
                progress?.Invoke(new DownloadProgress(received, total, thisAttempt / elapsed));
            }
        }
        catch (DownloadFailureException) { throw; }
        catch (OperationCanceledException) { throw Failure(DownloadError.Interrupted, keep: true); }
        catch (Exception e) { throw Failure(Map(e) == DownloadError.TimedOut ? DownloadError.TimedOut : DownloadError.Interrupted, keep: true); }

        if (total is { } expected && received != expected) throw Failure(DownloadError.Interrupted, keep: true);
        if (formatId is null) Check();
        await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        stream.Dispose();
        return new DownloadResult(file, formatId!, fileName, received, current);
    }

    /// <summary>A strong ETag, otherwise Last-Modified: what If-Range accepts.</summary>
    private static string? Validator(TransportResponse response)
    {
        var etag = response.Header("ETag")?.Trim();
        if (!string.IsNullOrEmpty(etag) && !etag.StartsWith("W/", StringComparison.Ordinal)) return etag;
        var modified = response.Header("Last-Modified")?.Trim();
        return string.IsNullOrEmpty(modified) ? null : modified;
    }

    private static DownloadError Map(Exception e) => e switch
    {
        TransportException { TimedOut: true } => DownloadError.TimedOut,
        TimeoutException => DownloadError.TimedOut,
        _ => DownloadError.Network,
    };

    // robots.txt

    private async Task CheckRobotsAsync(DropLink link, CancellationToken cancellationToken)
    {
        var parts = link.Parts;
        var origin = $"{parts.Scheme}://{parts.Host}{(parts.Port is { } p ? $":{p}" : "")}";
        if (!_robots.TryGetValue(origin, out var rules))
        {
            rules = await FetchRobotsAsync(origin, cancellationToken).ConfigureAwait(false);
            _robots[origin] = rules;
        }
        var path = parts.Path + (parts.Query is null ? "" : "?" + parts.Query);
        if (!rules.IsAllowed(path, _registry.RobotsAgent)) throw new DownloadFailureException(DownloadError.RobotsDisallowed, null);
    }

    /// <summary>
    /// RFC 9309: follow up to five redirects; 2xx is parsed (capped), 4xx allows everything, 5xx means the site is
    /// unavailable (not cached, so a later download asks again).
    /// </summary>
    private async Task<RobotsRules> FetchRobotsAsync(string origin, CancellationToken cancellationToken)
    {
        var url = origin + "/robots.txt";
        for (var hop = 0; hop <= 5; hop++)
        {
            TransportResponse response;
            try { response = await _transport.GetAsync(new TransportRequest(url, BaseHeaders()), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { throw new DownloadFailureException(Map(e), null); }
            using (response)
            {
                if (response.Status is >= 300 and < 400)
                {
                    var (baseParts, _) = LinkSyntax.Normalize(url, _registry.MaxLinkLength);
                    if (response.Header("Location") is not { } location || baseParts is null) return RobotsRules.AllowAll;
                    var (next, _) = LinkSyntax.Normalize(LinkSyntax.Resolve(location, baseParts), _registry.MaxLinkLength);
                    if (next is null) return RobotsRules.AllowAll;
                    url = next.ToString();
                    continue;
                }
                switch (DownloadRules.RobotsFetchFor(response.Status))
                {
                    case RobotsFetch.AllowAll: return RobotsRules.AllowAll;
                    case RobotsFetch.DisallowAll: throw new DownloadFailureException(DownloadError.ServerError, null);
                }
                var data = new byte[Settings.MaxRobotsBytes];
                int read;
                try { read = await response.Body.ReadAtLeastAsync(data, data.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { throw new DownloadFailureException(Map(e), null); }
                return new RobotsRules(Encoding.UTF8.GetString(data, 0, read));
            }
        }
        return RobotsRules.AllowAll;   // too many redirects: RFC 9309 treats robots.txt as unavailable (4xx)
    }
}
