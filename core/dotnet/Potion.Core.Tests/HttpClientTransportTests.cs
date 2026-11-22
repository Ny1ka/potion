using System.Text;
using Potion.Core.Dropper;
using Xunit;

/// <summary>The real Windows transport against a loopback server: no automatic redirects, no cookies, no credentials.</summary>
public sealed class HttpClientTransportTests
{
    private static readonly Dictionary<string, string> Headers = new()
    {
        ["User-Agent"] = "PotionDropper/0.0.1 (+https://github.com/Ny1ka/potion)",
        ["Accept-Encoding"] = "identity",
        ["Accept"] = "*/*",
    };

    private static async Task<byte[]> Collect(TransportResponse response)
    {
        using (response)
        {
            using var copy = new MemoryStream();
            await response.Body.CopyToAsync(copy);
            return copy.ToArray();
        }
    }

    private static Dictionary<string, string> H(params (string, string)[] pairs) => pairs.ToDictionary(p => p.Item1, p => p.Item2);

    [Fact]
    public async Task StreamsTheBodyAndSendsOurHeaders()
    {
        var body = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 256)).ToArray();
        using var server = new LoopbackServer(_ => new(Headers: H(("Content-Type", "video/mp4"), ("ETag", "\"x\"")), Body: body));
        using var transport = new HttpClientTransport();
        var response = await transport.GetAsync(new TransportRequest(server.Url("/a.mp4"), Headers), CancellationToken.None);
        Assert.Equal(200, response.Status);
        Assert.Equal("video/mp4", response.Header("content-type"));
        Assert.Equal("\"x\"", response.Header("ETag"));
        Assert.Equal(body, await Collect(response));
        var request = server.Requests.Single();
        Assert.Equal(Headers["User-Agent"], request.Headers["user-agent"]);
        Assert.Equal("identity", request.Headers["accept-encoding"]);
        Assert.False(request.Headers.ContainsKey("cookie"));
        Assert.False(request.Headers.ContainsKey("authorization"));
    }

    [Fact]
    public async Task RedirectsAreNotFollowed()
    {
        using var server = new LoopbackServer(r => r.Path == "/old" ? new(302, "Found", H(("Location", "/new"))) : new(Body: "new"u8.ToArray()));
        using var transport = new HttpClientTransport();
        var response = await transport.GetAsync(new TransportRequest(server.Url("/old"), Headers), CancellationToken.None);
        Assert.Equal(302, response.Status);
        Assert.Equal("/new", response.Header("Location"));
        await Collect(response);
        Assert.Equal(["/old"], server.Requests.Select(r => r.Path));
    }

    [Fact]
    public async Task CookiesAreNeitherStoredNorSent()
    {
        using var server = new LoopbackServer(_ => new(Headers: H(("Set-Cookie", "session=abc; Path=/")), Body: "x"u8.ToArray()));
        using var transport = new HttpClientTransport();
        await Collect(await transport.GetAsync(new TransportRequest(server.Url("/one"), Headers), CancellationToken.None));
        await Collect(await transport.GetAsync(new TransportRequest(server.Url("/two"), Headers), CancellationToken.None));
        Assert.Equal(2, server.Requests.Count);
        Assert.False(server.Requests[1].Headers.ContainsKey("cookie"));
    }

    [Fact]
    public async Task SignInRequestsComeBackAsResponses()
    {
        using var server = new LoopbackServer(_ => new(401, "Unauthorized", H(("WWW-Authenticate", "Basic realm=\"files\"")), "no"u8.ToArray()));
        using var transport = new HttpClientTransport();
        var response = await transport.GetAsync(new TransportRequest(server.Url("/private.mp4"), Headers), CancellationToken.None);
        Assert.Equal(401, response.Status);
        await Collect(response);
        Assert.All(server.Requests, r => Assert.False(r.Headers.ContainsKey("authorization")));
    }

    [Fact]
    public async Task TimeoutWhenTheServerNeverAnswers()
    {
        using var server = new LoopbackServer(_ => new(Hang: true));
        using var transport = new HttpClientTransport(timeoutSeconds: 1);
        var e = await Assert.ThrowsAsync<TransportException>(() => transport.GetAsync(new TransportRequest(server.Url("/slow.mp4"), Headers), CancellationToken.None));
        Assert.True(e.TimedOut);
    }

    [Fact]
    public async Task ConnectionRefusedIsANetworkError()
    {
        string url;
        using (var server = new LoopbackServer(_ => new())) url = server.Url("/a.mp4");
        // Windows retries a refused connection for about 2 seconds, so the timeout must be longer than that.
        using var transport = new HttpClientTransport(timeoutSeconds: 15);
        var e = await Assert.ThrowsAsync<TransportException>(() => transport.GetAsync(new TransportRequest(url, Headers), CancellationToken.None));
        Assert.False(e.TimedOut);
    }

    private static byte[] Mp4(int size)
    {
        var head = new List<byte> { 0, 0, 0, 0x20 };
        head.AddRange("ftypisom"u8.ToArray());
        head.AddRange(new byte[] { 0, 0, 2, 0 });
        head.AddRange("isomiso2avc1mp41"u8.ToArray());
        while (head.Count < size) head.Add((byte)(head.Count % 251));
        return [.. head];
    }

    [Fact]
    public async Task DownloaderEndToEnd()
    {
        var mp4 = Mp4(150_000);
        using var server = new LoopbackServer(r => r.Path switch
        {
            "/robots.txt" => new(Headers: H(("Content-Type", "text/plain")), Body: Encoding.UTF8.GetBytes("User-agent: *\nDisallow: /private/\n")),
            "/get" => new(302, "Found", H(("Location", "/files/clip.mp4"))),
            "/files/clip.mp4" => new(Headers: H(("Content-Type", "video/mp4"), ("ETag", "\"c\"")), Body: mp4),
            _ => new(404, "Not Found"),
        });
        var folder = Path.Combine(Path.GetTempPath(), $"potion-e2e-{Guid.NewGuid()}");
        Directory.CreateDirectory(folder);
        try
        {
            using var transport = new HttpClientTransport();
            var downloader = new Downloader(transport, "0.0.1");
            var result = await downloader.DownloadAsync(SourceRegistry.Shared.Parse(server.Url("/get")).Link!, folder, 1L << 30);
            Assert.Equal("mp4", result.FormatId);
            Assert.Equal("clip.mp4", result.FileName);
            Assert.Equal(mp4, await File.ReadAllBytesAsync(result.File));
            Assert.Equal(["/robots.txt", "/get", "/files/clip.mp4"], server.Requests.Select(r => r.Path));

            var f = await Assert.ThrowsAsync<DownloadFailureException>(() =>
                downloader.DownloadAsync(SourceRegistry.Shared.Parse(server.Url("/private/a.mp4")).Link!, folder, 1L << 30));
            Assert.Equal(DownloadError.RobotsDisallowed, f.Error);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task DroppedConnectionCanResume()
    {
        var mp4 = Mp4(120_000);
        using var server = new LoopbackServer(r =>
        {
            if (r.Path == "/robots.txt") return new(404, "Not Found");
            if (r.Headers.GetValueOrDefault("range") == "bytes=50000-" && r.Headers.GetValueOrDefault("if-range") == "\"r\"")
                return new(206, "Partial Content", H(("Content-Type", "video/mp4"), ("Content-Range", "bytes 50000-119999/120000"), ("ETag", "\"r\"")), mp4[50000..]);
            return new(Headers: H(("Content-Type", "video/mp4"), ("ETag", "\"r\""), ("Content-Length", "120000")), Body: mp4, DropAfter: 50000);
        });
        var folder = Path.Combine(Path.GetTempPath(), $"potion-e2e-{Guid.NewGuid()}");
        Directory.CreateDirectory(folder);
        try
        {
            using var transport = new HttpClientTransport();
            var downloader = new Downloader(transport, "0.0.1");
            var link = SourceRegistry.Shared.Parse(server.Url("/r.mp4")).Link!;
            var f = await Assert.ThrowsAsync<DownloadFailureException>(() => downloader.DownloadAsync(link, folder, 1L << 30));
            Assert.Equal(DownloadError.Interrupted, f.Error);
            var resume = Assert.IsType<ResumeState>(f.Resume);
            Assert.Equal(50000, resume.Bytes);
            var result = await downloader.DownloadAsync(link, folder, 1L << 30, resume);
            Assert.Equal(mp4, await File.ReadAllBytesAsync(result.File));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
