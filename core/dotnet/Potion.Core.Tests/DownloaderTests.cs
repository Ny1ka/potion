using System.Text;
using Potion.Core.Dropper;
using Xunit;

/// <summary>A transport that answers from a script, records every request, and never touches the network.</summary>
internal sealed class ScriptedTransport : IHttpTransport
{
    public sealed record Reply(
        int Status = 200,
        Dictionary<string, string>? Headers = null,
        byte[][]? Chunks = null,
        bool FailAfterChunks = false,
        TransportException? FailBeforeAnswer = null);

    private readonly object _lock = new();
    private readonly Dictionary<string, Queue<Reply>> _script = new();
    private readonly List<TransportRequest> _requests = [];

    public IReadOnlyList<TransportRequest> Requests { get { lock (_lock) return [.. _requests]; } }
    public List<TransportRequest> RequestsTo(string url) => Requests.Where(r => r.Url == url).ToList();

    /// <summary>Replies for a URL are used in order; the last one repeats. Unscripted URLs get 404.</summary>
    public void On(string url, Reply first, Reply? second = null, Reply? third = null)
    {
        lock (_lock)
        {
            if (!_script.TryGetValue(url, out var queue)) _script[url] = queue = new Queue<Reply>();
            foreach (var r in new[] { first, second, third }.OfType<Reply>()) queue.Enqueue(r);
        }
    }

    public Task<TransportResponse> GetAsync(TransportRequest request, CancellationToken cancellationToken)
    {
        Reply reply;
        lock (_lock)
        {
            _requests.Add(request);
            reply = _script.TryGetValue(request.Url, out var queue) && queue.Count > 0
                ? (queue.Count > 1 ? queue.Dequeue() : queue.Peek())
                : new Reply(404);
        }
        if (reply.FailBeforeAnswer is { } error) throw error;
        Stream body = new ChunkStream(reply.Chunks ?? [], reply.FailAfterChunks);
        return Task.FromResult(new TransportResponse(reply.Status, reply.Headers ?? new(), body));
    }

    /// <summary>Hands out the chunks one read at a time, then ends or fails like a dropped connection.</summary>
    private sealed class ChunkStream(byte[][] chunks, bool fail) : Stream
    {
        private int _chunk, _offset;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_chunk >= chunks.Length) return fail ? throw new TransportException(timedOut: false) : 0;
            var current = chunks[_chunk];
            var n = Math.Min(count, current.Length - _offset);
            Array.Copy(current, _offset, buffer, offset, n);
            _offset += n;
            if (_offset == current.Length) { _chunk++; _offset = 0; }
            return n;
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

public sealed class DownloaderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"potion-dl-{Guid.NewGuid()}");
    private readonly ScriptedTransport _transport = new();
    private readonly List<int> _sleeps = [];
    private const long Big = 1L << 30;

    public DownloaderTests() => Directory.CreateDirectory(_folder);
    public void Dispose() { try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } }

    private Downloader NewDownloader() => new(_transport, "1.2.3", sleep: (s, _) => { lock (_sleeps) _sleeps.Add(s); return Task.CompletedTask; });

    /// <summary>An MP4 header followed by filler, size bytes in all.</summary>
    private static byte[] Mp4(int size)
    {
        var head = new List<byte> { 0, 0, 0, 0x20 };
        head.AddRange("ftypisom"u8.ToArray());
        head.AddRange(new byte[] { 0, 0, 2, 0 });
        head.AddRange("isomiso2avc1mp41"u8.ToArray());
        while (head.Count < size) head.Add((byte)(head.Count % 251));
        return [.. head];
    }

    private static byte[][] Chunks(byte[] data, int size = 1000) => data.Chunk(size).ToArray();
    private static Dictionary<string, string> H(params (string, string)[] pairs) => pairs.ToDictionary(p => p.Item1, p => p.Item2);
    private static DropLink Link(string text) => SourceRegistry.Shared.Parse(text).Link!;

    private static async Task<DownloadFailureException> Failure(Func<Task> body) => await Assert.ThrowsAsync<DownloadFailureException>(body);

    private string[] Files() => Directory.GetFiles(_folder);

    [Fact]
    public async Task DownloadsCheckedMediaToAHiddenFile()
    {
        var body = Mp4(5000);
        _transport.On("https://example.com/v/clip.mp4", new(Headers: H(("Content-Type", "video/mp4"), ("Content-Length", "5000")), Chunks: Chunks(body)));
        var seen = new List<long>();
        var result = await NewDownloader().DownloadAsync(Link("https://example.com/v/clip.mp4"), _folder, Big, progress: p => seen.Add(p.Bytes));
        Assert.Equal("mp4", result.FormatId);
        Assert.Equal("clip.mp4", result.FileName);
        Assert.Equal(5000, result.Bytes);
        Assert.Equal(body, await File.ReadAllBytesAsync(result.File));
        Assert.StartsWith(".potion-dropper-", Path.GetFileName(result.File));
        Assert.Equal(5000, seen[^1]);

        var request = _transport.RequestsTo("https://example.com/v/clip.mp4").Single();
        Assert.Equal("PotionDropper/1.2.3 (+https://github.com/Ny1ka/potion)", request.Headers["User-Agent"]);
        Assert.Equal("identity", request.Headers["Accept-Encoding"]);
        Assert.False(request.Headers.ContainsKey("Cookie"));
        Assert.False(request.Headers.ContainsKey("Authorization"));
        Assert.False(request.Headers.ContainsKey("Range"));
        Assert.Single(_transport.RequestsTo("https://example.com/robots.txt"));
    }

    [Fact]
    public async Task RobotsDisallowStopsBeforeTheDownload()
    {
        _transport.On("https://example.com/robots.txt", new(Headers: H(("Content-Type", "text/plain")), Chunks: [Encoding.UTF8.GetBytes("User-agent: PotionDropper\nDisallow: /private/\n")]));
        _transport.On("https://example.com/private/a.mp4", new(Headers: H(("Content-Type", "video/mp4")), Chunks: [Mp4(100)]));
        var f = await Failure(() => NewDownloader().DownloadAsync(Link("https://example.com/private/a.mp4"), _folder, Big));
        Assert.Equal(DownloadError.RobotsDisallowed, f.Error);
        Assert.Empty(_transport.RequestsTo("https://example.com/private/a.mp4"));
    }

    [Fact]
    public async Task RobotsIsFetchedOncePerSiteAndServerErrorsBlock()
    {
        _transport.On("https://example.com/a.mp4", new(Headers: H(("Content-Type", "video/mp4")), Chunks: [Mp4(100)]));
        _transport.On("https://example.com/b.mp4", new(Headers: H(("Content-Type", "video/mp4")), Chunks: [Mp4(100)]));
        var d = NewDownloader();
        await d.DownloadAsync(Link("https://example.com/a.mp4"), _folder, Big);
        await d.DownloadAsync(Link("https://example.com/b.mp4"), _folder, Big);
        Assert.Single(_transport.RequestsTo("https://example.com/robots.txt"));

        _transport.On("https://down.example.com/robots.txt", new(503));
        var f = await Failure(() => d.DownloadAsync(Link("https://down.example.com/a.mp4"), _folder, Big));
        Assert.Equal(DownloadError.ServerError, f.Error);
    }

    [Fact]
    public async Task RedirectsAreFollowedAndCheckedAgain()
    {
        _transport.On("https://example.com/get/7", new(302, H(("Location", "/files/seven.mp3"))));
        _transport.On("https://example.com/files/seven.mp3", new(301, H(("Location", "https://cdn.example.net/x/seven.mp3?sig=1"))));
        var mp3 = "ID3"u8.ToArray().Concat(new byte[] { 4, 0, 0, 0, 0, 0x0A }).Concat(new byte[200]).ToArray();
        _transport.On("https://cdn.example.net/x/seven.mp3?sig=1", new(Headers: H(("Content-Type", "audio/mpeg")), Chunks: [mp3]));
        var result = await NewDownloader().DownloadAsync(Link("https://example.com/get/7"), _folder, Big);
        Assert.Equal("mp3", result.FormatId);
        Assert.Equal("seven.mp3", result.FileName);
        Assert.Equal("https://cdn.example.net/x/seven.mp3?sig=1", result.FinalLink.Text);
        Assert.Single(_transport.RequestsTo("https://cdn.example.net/robots.txt"));

        _transport.On("https://example.com/bad", new(302, H(("Location", "ftp://example.com/a.mp4"))));
        var f = await Failure(() => NewDownloader().DownloadAsync(Link("https://example.com/bad"), _folder, Big));
        Assert.Equal(DownloadError.From(LinkError.UnsupportedScheme), f.Error);

        _transport.On("https://example.com/loop", new(302, H(("Location", "/loop"))));
        var g = await Failure(() => NewDownloader().DownloadAsync(Link("https://example.com/loop"), _folder, Big));
        Assert.Equal(DownloadError.TooManyRedirects, g.Error);
    }

    [Fact]
    public async Task RetriesAfterRetryAfter()
    {
        _transport.On("https://example.com/a.mp4",
            new(429, H(("Retry-After", "2"))),
            new(503, H(("Retry-After", "1"))),
            new(Headers: H(("Content-Type", "video/mp4")), Chunks: [Mp4(300)]));
        await NewDownloader().DownloadAsync(Link("https://example.com/a.mp4"), _folder, Big);
        Assert.Equal([2, 1], _sleeps);

        _transport.On("https://example.com/busy.mp4", new(429));
        var f = await Failure(() => NewDownloader().DownloadAsync(Link("https://example.com/busy.mp4"), _folder, Big));
        Assert.Equal(DownloadError.RateLimited, f.Error);
        Assert.Equal(3, _transport.RequestsTo("https://example.com/busy.mp4").Count);
    }

    [Fact]
    public async Task PagesAndNonMediaAreRefusedAndCleanedUp()
    {
        _transport.On("https://example.com/download?id=4", new(Headers: H(("Content-Type", "application/octet-stream")),
            Chunks: [Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body>Sign in</body></html>")]));
        var f = await Failure(() => NewDownloader().DownloadAsync(Link("https://example.com/download?id=4"), _folder, Big));
        Assert.Equal(DownloadError.From(LinkError.UnsupportedSite), f.Error);

        _transport.On("https://example.com/fake.mp4", new(Headers: H(("Content-Type", "video/mp4")),
            Chunks: [[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D]]));
        var g = await Failure(() => NewDownloader().DownloadAsync(Link("https://example.com/fake.mp4"), _folder, Big));
        Assert.Equal(DownloadError.From(LinkError.NotMedia), g.Error);
        Assert.Null(g.Resume);
        Assert.Empty(Files());
    }

    [Fact]
    public async Task SizeLimitBeforeAndDuringTheDownload()
    {
        _transport.On("https://example.com/big.mp4", new(Headers: H(("Content-Type", "video/mp4"), ("Content-Length", "5000")), Chunks: Chunks(Mp4(5000))));
        var f = await Failure(() => NewDownloader().DownloadAsync(Link("https://example.com/big.mp4"), _folder, 4000));
        Assert.Equal(DownloadError.TooLarge, f.Error);

        _transport.On("https://example.com/unknown.mp4", new(Headers: H(("Content-Type", "video/mp4")), Chunks: Chunks(Mp4(5000))));
        var g = await Failure(() => NewDownloader().DownloadAsync(Link("https://example.com/unknown.mp4"), _folder, 4000));
        Assert.Equal(DownloadError.TooLarge, g.Error);
        Assert.Empty(Files());
    }

    [Fact]
    public async Task InterruptedDownloadResumesWithRange()
    {
        var body = Mp4(6000);
        const string url = "https://example.com/r.mp4";
        _transport.On(url,
            new(Headers: H(("Content-Type", "video/mp4"), ("Content-Length", "6000"), ("ETag", "\"v1\"")), Chunks: Chunks(body[..2500]), FailAfterChunks: true),
            new(206, H(("Content-Type", "video/mp4"), ("Content-Length", "3500"), ("Content-Range", "bytes 2500-5999/6000")), Chunks(body[2500..])));
        var f = await Failure(() => NewDownloader().DownloadAsync(Link(url), _folder, Big));
        Assert.Equal(DownloadError.Interrupted, f.Error);
        var resume = Assert.IsType<ResumeState>(f.Resume);
        Assert.Equal(2500, resume.Bytes);
        Assert.Equal("\"v1\"", resume.Validator);

        var result = await NewDownloader().DownloadAsync(Link(url), _folder, Big, resume);
        Assert.Equal(body, await File.ReadAllBytesAsync(result.File));
        Assert.Equal(resume.File, result.File);
        var second = _transport.RequestsTo(url)[^1];
        Assert.Equal("bytes=2500-", second.Headers["Range"]);
        Assert.Equal("\"v1\"", second.Headers["If-Range"]);
    }

    [Fact]
    public async Task ResumeStartsOverWhenTheFileChanged()
    {
        byte[] old = Mp4(4000), changed = Mp4(3000);
        const string url = "https://example.com/c.mp4";
        _transport.On(url,
            new(Headers: H(("Content-Type", "video/mp4"), ("Content-Length", "4000"), ("Last-Modified", "Wed, 21 Oct 2026 07:28:00 GMT")), Chunks: Chunks(old[..1000]), FailAfterChunks: true),
            new(Headers: H(("Content-Type", "video/mp4"), ("Content-Length", "3000")), Chunks: Chunks(changed)));
        var resume = (await Failure(() => NewDownloader().DownloadAsync(Link(url), _folder, Big))).Resume!;
        var result = await NewDownloader().DownloadAsync(Link(url), _folder, Big, resume);
        Assert.Equal(changed, await File.ReadAllBytesAsync(result.File));
        Assert.False(File.Exists(resume.File));

        const string url2 = "https://example.com/d.mp4";
        _transport.On(url2,
            new(Headers: H(("Content-Type", "video/mp4"), ("Content-Length", "4000"), ("ETag", "\"a\"")), Chunks: Chunks(old[..1000]), FailAfterChunks: true),
            new(416),
            new(Headers: H(("Content-Type", "video/mp4"), ("Content-Length", "4000")), Chunks: Chunks(old)));
        var resume2 = (await Failure(() => NewDownloader().DownloadAsync(Link(url2), _folder, Big))).Resume!;
        var result2 = await NewDownloader().DownloadAsync(Link(url2), _folder, Big, resume2);
        Assert.Equal(old, await File.ReadAllBytesAsync(result2.File));
        Assert.False(_transport.RequestsTo(url2)[^1].Headers.ContainsKey("Range"));
    }

    [Fact]
    public async Task NoResumeWithoutAValidator()
    {
        _transport.On("https://example.com/n.mp4", new(Headers: H(("Content-Type", "video/mp4"), ("Content-Length", "3000")),
            Chunks: Chunks(Mp4(3000)[..1000]), FailAfterChunks: true));
        var f = await Failure(() => NewDownloader().DownloadAsync(Link("https://example.com/n.mp4"), _folder, Big));
        Assert.Equal(DownloadError.Interrupted, f.Error);
        Assert.Null(f.Resume);
        Assert.Empty(Files());
    }

    [Fact]
    public async Task NameFromContentDisposition()
    {
        _transport.On("https://example.com/get?id=9", new(Headers: H(("Content-Type", "application/octet-stream"),
            ("Content-Disposition", "attachment; filename*=UTF-8''Caf%C3%A9%20Live.mp4")), Chunks: [Mp4(400)]));
        var result = await NewDownloader().DownloadAsync(Link("https://example.com/get?id=9"), _folder, Big);
        Assert.Equal("Café Live.mp4", result.FileName);
        Assert.Equal("mp4", result.FormatId);
    }

    [Fact]
    public async Task TransportErrors()
    {
        _transport.On("https://example.com/robots.txt", new(FailBeforeAnswer: new TransportException(timedOut: false)));
        var f = await Failure(() => NewDownloader().DownloadAsync(Link("https://example.com/a.mp4"), _folder, Big));
        Assert.Equal(DownloadError.Network, f.Error);

        _transport.On("https://slow.example.com/a.mp4", new(FailBeforeAnswer: new TransportException(timedOut: true)));
        var g = await Failure(() => NewDownloader().DownloadAsync(Link("https://slow.example.com/a.mp4"), _folder, Big));
        Assert.Equal(DownloadError.TimedOut, g.Error);
    }

    [Fact]
    public async Task AccessDeniedIsNotRetried()
    {
        _transport.On("https://example.com/private.mp4", new(401, H(("WWW-Authenticate", "Basic realm=\"x\""))));
        var f = await Failure(() => NewDownloader().DownloadAsync(Link("https://example.com/private.mp4"), _folder, Big));
        Assert.Equal(DownloadError.AccessDenied, f.Error);
        Assert.Single(_transport.RequestsTo("https://example.com/private.mp4"));
    }

    [Fact]
    public async Task CancellingKeepsTheResumablePartialFile()
    {
        using var cts = new CancellationTokenSource();
        var body = Mp4(8000);
        _transport.On("https://example.com/x.mp4", new(Headers: H(("Content-Type", "video/mp4"), ("Content-Length", "8000"), ("ETag", "\"e\"")), Chunks: Chunks(body)));
        var f = await Failure(() => NewDownloader().DownloadAsync(Link("https://example.com/x.mp4"), _folder, Big,
            progress: p => { if (p.Bytes >= 3000) cts.Cancel(); }, cancellationToken: cts.Token));
        Assert.Equal(DownloadError.Interrupted, f.Error);
        Assert.Equal(3000, f.Resume?.Bytes);
        Assert.True(File.Exists(f.Resume!.File));
    }
}
