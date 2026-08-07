using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ParleyCli.IntegrationTests;

internal sealed class FakeCodexServer : IAsyncDisposable
{
    private readonly Socket _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _serve;
    private readonly string[] _loadedThreads;
    private readonly string? _activeTurnId;
    private readonly string? _threadPath;
    private readonly bool _persistTimedOutSubmission;
    private readonly string? _malformedAtMethod;
    private int _failSubmissions;
    private int _timeoutSubmissions;

    public FakeCodexServer(string[] loadedThreads, string? activeTurnId = null, string? threadPath = null,
        int failSubmissions = 0, string? malformedAtMethod = null, int timeoutSubmissions = 0,
        bool persistTimedOutSubmission = false)
    {
        SocketPath = Path.Combine(Path.GetTempPath(), $"p-{Guid.NewGuid():N}.sock");
        _loadedThreads = loadedThreads;
        _activeTurnId = activeTurnId;
        _threadPath = threadPath;
        _malformedAtMethod = malformedAtMethod;
        _failSubmissions = failSubmissions;
        _timeoutSubmissions = timeoutSubmissions;
        _persistTimedOutSubmission = persistTimedOutSubmission;
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        _listener.Listen(4);
        _serve = ServeAsync();
    }

    public string SocketPath { get; }
    public List<string> SubmittedMethods { get; } = [];
    public List<string> SubmittedPayloads { get; } = [];
    public List<string> ReadPayloads { get; } = [];
    public int ConnectionCount;
    public Exception? LastError;

    public async Task WaitForSubmissionsAsync(int count)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (SubmittedMethods)
                if (SubmittedMethods.Count >= count) return;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Expected {count} Codex submissions.");
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var socket = await _listener.AcceptAsync(_stop.Token);
                Interlocked.Increment(ref ConnectionCount);
                _ = HandleAsync(socket);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task HandleAsync(Socket socket)
    {
        await using var stream = new NetworkStream(socket, ownsSocket: true);
        try
        {
            var headers = await ReadHeadersAsync(stream, _stop.Token);
            var key = headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                .Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                .Split(':', 2)[1].Trim();
            var accept = Convert.ToBase64String(SHA1.HashData(
                Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
            await stream.WriteAsync(response, _stop.Token);

            using var webSocket = WebSocket.CreateFromStream(stream, isServer: true,
                subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));
            while (webSocket.State == WebSocketState.Open && !_stop.IsCancellationRequested)
            {
                var payload = await ReceiveAsync(webSocket, _stop.Token);
                if (payload is null) break;
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                var method = root.GetProperty("method").GetString()!;
                if (!root.TryGetProperty("id", out var idElement)
                    || idElement.ValueKind != JsonValueKind.Number) continue;
                var id = idElement.GetInt32();

                if (method == _malformedAtMethod)
                {
                    await webSocket.SendAsync(Encoding.UTF8.GetBytes("{malformed"),
                        WebSocketMessageType.Text, true, _stop.Token);
                    continue;
                }

                object? responseBody = method switch
                {
                    "initialize" => new { id, result = new { } },
                    "thread/loaded/list" => new { id, result = new { data = _loadedThreads } },
                    "thread/read" => ThreadReadResponse(id, payload),
                    "turn/start" or "turn/steer" => SubmissionResponse(id, method, payload),
                    _ => new { id, error = new { code = -32601, message = "unknown method" } }
                };
                if (responseBody is null) continue;
                var bytes = JsonSerializer.SerializeToUtf8Bytes(responseBody);
                await webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, _stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (IOException) { }
        catch (Exception ex) { LastError = ex; }
    }

    private object ThreadReadResponse(int id, string payload)
    {
        lock (ReadPayloads) ReadPayloads.Add(payload);
        using var document = JsonDocument.Parse(payload);
        var includeTurns = document.RootElement.GetProperty("params")
            .TryGetProperty("includeTurns", out var include) && include.GetBoolean();
        return new
        {
            id,
            result = new
            {
                thread = new
                {
                    id = _loadedThreads.FirstOrDefault(),
                    path = _threadPath,
                    status = new { type = _activeTurnId is null ? "idle" : "active" },
                    turns = includeTurns && _activeTurnId is not null
                        ? new object[] { new { id = _activeTurnId, status = "inProgress" } }
                        : []
                }
            }
        };
    }

    private object? SubmissionResponse(int id, string method, string payload)
    {
        lock (SubmittedMethods)
        {
            SubmittedMethods.Add(method);
            SubmittedPayloads.Add(payload);
        }
        if (Interlocked.Decrement(ref _timeoutSubmissions) >= 0)
        {
            if (_persistTimedOutSubmission && _threadPath is not null)
            {
                using var document = JsonDocument.Parse(payload);
                var clientId = document.RootElement.GetProperty("params")
                    .GetProperty("clientUserMessageId").GetString();
                File.AppendAllText(_threadPath,
                    $"{{\"type\":\"event_msg\",\"payload\":{{\"type\":\"user_message\",\"client_id\":\"{clientId}\"}}}}\n");
            }
            return null;
        }
        if (Interlocked.Decrement(ref _failSubmissions) >= 0)
            return new { id, error = new { code = -32000, message = "thread state changed" } };
        return new { id, result = new { } };
    }

    private static async Task<string> ReadHeadersAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 16 * 1024)
        {
            if (await stream.ReadAsync(one, ct) == 0) throw new IOException("Handshake closed.");
            bytes.Add(one[0]);
            var n = bytes.Count;
            if (n >= 4 && bytes[n - 4] == '\r' && bytes[n - 3] == '\n'
                       && bytes[n - 2] == '\r' && bytes[n - 1] == '\n')
                return Encoding.ASCII.GetString(bytes.ToArray());
        }
        throw new InvalidDataException("Oversized WebSocket handshake.");
    }

    private static async Task<string?> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        using var body = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            body.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(body.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Dispose();
        try { await _serve; } catch { }
        _stop.Dispose();
        if (File.Exists(SocketPath)) File.Delete(SocketPath);
    }
}
