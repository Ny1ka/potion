using System.Net;
using System.Net.Sockets;
using System.Text;

/// <summary>
/// A tiny HTTP/1.1 server on 127.0.0.1 for transport tests: one request per connection, answered by a handler.
/// Never reachable from outside this machine; needs no admin rights on Windows (plain TCP, not HttpListener).
/// </summary>
internal sealed class LoopbackServer : IDisposable
{
    public sealed record Request(string Method, string Path, IReadOnlyDictionary<string, string> Headers);

    public sealed record Reply(
        int Status = 200,
        string Reason = "OK",
        Dictionary<string, string>? Headers = null,
        byte[]? Body = null,
        bool Hang = false,
        int? DropAfter = null);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<Request, Reply> _handler;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Request> _requests = [];
    private readonly List<TcpClient> _held = [];

    public LoopbackServer(Func<Request, Reply> handler)
    {
        _handler = handler;
        _listener.Start();
        _ = AcceptLoop();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public string Url(string path) => $"http://127.0.0.1:{Port}{path}";
    public IReadOnlyList<Request> Requests { get { lock (_requests) return [.. _requests]; } }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        lock (_held) foreach (var c in _held) c.Dispose();
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        var keep = false;
        try
        {
            var stream = client.GetStream();
            var buffer = new List<byte>();
            var chunk = new byte[8192];
            while (true)
            {
                var n = await stream.ReadAsync(chunk, _stop.Token);
                if (n == 0) return;
                buffer.AddRange(chunk.AsSpan(0, n).ToArray());
                if (Encoding.ASCII.GetString(buffer.ToArray()).Contains("\r\n\r\n")) break;
            }
            var head = Encoding.UTF8.GetString(buffer.ToArray());
            head = head[..head.IndexOf("\r\n\r\n", StringComparison.Ordinal)];
            var lines = head.Split("\r\n");
            var start = lines[0].Split(' ');
            var headers = new Dictionary<string, string>();
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon > 0) headers[line[..colon].ToLowerInvariant()] = line[(colon + 1)..].Trim();
            }
            var request = new Request(start[0], start.Length > 1 ? start[1] : "", headers);
            Reply reply;
            lock (_requests)
            {
                _requests.Add(request);
                reply = _handler(request);
            }
            if (reply.Hang)
            {
                keep = true;
                lock (_held) _held.Add(client);
                return;
            }
            var body = reply.Body ?? [];
            var replyHeaders = new Dictionary<string, string>(reply.Headers ?? new());
            replyHeaders.TryAdd("Content-Length", body.Length.ToString());
            replyHeaders["Connection"] = "close";
            var text = new StringBuilder($"HTTP/1.1 {reply.Status} {reply.Reason}\r\n");
            foreach (var (name, value) in replyHeaders) text.Append($"{name}: {value}\r\n");
            text.Append("\r\n");
            await stream.WriteAsync(Encoding.UTF8.GetBytes(text.ToString()));
            await stream.WriteAsync(reply.DropAfter is { } drop ? body.AsMemory(0, drop) : body);
            await stream.FlushAsync();
            if (reply.DropAfter is not null) client.Client.LingerState = new LingerOption(true, 0);   // reset, like a dropped connection
        }
        catch (Exception) { }
        finally
        {
            if (!keep) client.Dispose();
        }
    }
}
