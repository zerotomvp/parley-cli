using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ParleyCli.Integrations;

/// <summary>
/// Best-effort bridge to a running Codex app-server daemon. Runtime identity is discovered on every
/// call by matching the current Parley role owner's sid against app-server's loaded thread ids; no
/// harness type is persisted in the roster.
/// </summary>
public sealed class CodexWakeClient
{
    public enum WakeStatus { Unavailable, NotLoaded, Woken, Failed }

    public readonly record struct WakeResult(WakeStatus Status, string? Error = null);

    public async Task<bool> IsLoadedAsync(string threadId, CancellationToken ct) =>
        (await ConnectAndInspectAsync(threadId, null, ct)).Status == WakeStatus.Woken;

    public Task<WakeResult> WakeAsync(string threadId, string notification, CancellationToken ct) =>
        ConnectAndInspectAsync(threadId, notification, ct);

    private static async Task<WakeResult> ConnectAndInspectAsync(
        string threadId, string? notification, CancellationToken outerCt)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        var ct = timeout.Token;

        try
        {
            var socketPath = await FindSocketAsync(ct);
            if (socketPath is null) return new(WakeStatus.Unavailable);

            await using var connection = await AppServerConnection.ConnectAsync(socketPath, ct);
            await connection.SendAsync(new
            {
                method = "initialize",
                id = 1,
                @params = new
                {
                    clientInfo = new { name = "parley_cli", title = "Parley CLI", version = "1" }
                }
            }, ct);
            await connection.SendAsync(new { method = "initialized", @params = new { } }, ct);
            using var initialized = await connection.ReadResponseAsync(1, ct);
            if (HasError(initialized, out var initError))
                return new(WakeStatus.Failed, initError);

            await connection.SendAsync(new { method = "thread/loaded/list", id = 2, @params = new { } }, ct);
            using var loaded = await connection.ReadResponseAsync(2, ct);
            if (HasError(loaded, out var loadedError))
                return new(WakeStatus.Failed, loadedError);

            var isLoaded = loaded.RootElement.GetProperty("result").GetProperty("data")
                .EnumerateArray().Any(e => e.GetString() == threadId);
            if (!isLoaded) return new(WakeStatus.NotLoaded);
            if (notification is null) return new(WakeStatus.Woken);

            return await SubmitAsync(connection, threadId, notification, ct);
        }
        catch (Exception ex) when (ex is IOException
                                   or SocketException
                                   or WebSocketException
                                   or InvalidOperationException
                                   or JsonException
                                   or System.ComponentModel.Win32Exception
                                   or OperationCanceledException)
        {
            return new(ex is System.ComponentModel.Win32Exception or SocketException
                ? WakeStatus.Unavailable
                : WakeStatus.Failed, ex.Message);
        }
    }

    private static async Task<string?> FindSocketAsync(CancellationToken ct)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "codex",
            ArgumentList = { "app-server", "daemon", "version" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process is null) return null;

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            await stderrTask;
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout)) return null;

            using var status = JsonDocument.Parse(stdout);
            var root = status.RootElement;
            if (!root.TryGetProperty("status", out var state) || state.GetString() != "running") return null;
            return root.TryGetProperty("socketPath", out var path) ? path.GetString() : null;
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch { /* best effort cleanup */ }
            }
        }
    }

    private static async Task<WakeResult> SubmitAsync(
        AppServerConnection connection, string threadId, string notification, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var readId = 3 + attempt * 2;
            await connection.SendAsync(new
            {
                method = "thread/read",
                id = readId,
                @params = new { threadId, includeTurns = true }
            }, ct);
            using var read = await connection.ReadResponseAsync(readId, ct);
            if (HasError(read, out var readError)) return new(WakeStatus.Failed, readError);

            var thread = read.RootElement.GetProperty("result").GetProperty("thread");
            string? activeTurnId = null;
            if (thread.TryGetProperty("turns", out var turns))
            {
                foreach (var turn in turns.EnumerateArray())
                    if (turn.TryGetProperty("status", out var status)
                        && status.GetString() == "inProgress"
                        && turn.TryGetProperty("id", out var id))
                        activeTurnId = id.GetString();
            }

            var submitId = readId + 1;
            object request = activeTurnId is not null
                ? new
                {
                    method = "turn/steer",
                    id = submitId,
                    @params = new
                    {
                        threadId,
                        input = new[] { new { type = "text", text = notification } },
                        expectedTurnId = activeTurnId
                    }
                }
                : new
                {
                    method = "turn/start",
                    id = submitId,
                    @params = new
                    {
                        threadId,
                        input = new[] { new { type = "text", text = notification } }
                    }
                };

            await connection.SendAsync(request, ct);
            using var submitted = await connection.ReadResponseAsync(submitId, ct);
            if (!HasError(submitted, out var submitError)) return new(WakeStatus.Woken);
            if (attempt == 1) return new(WakeStatus.Failed, submitError);
        }

        return new(WakeStatus.Failed, "Codex thread changed state while waking it.");
    }

    private static bool HasError(JsonDocument response, out string? error)
    {
        if (!response.RootElement.TryGetProperty("error", out var value))
        {
            error = null;
            return false;
        }

        error = value.TryGetProperty("message", out var message)
            ? message.GetString()
            : value.ToString();
        return true;
    }

    private sealed class AppServerConnection : IAsyncDisposable
    {
        private readonly Socket _socket;
        private readonly NetworkStream _stream;
        private readonly WebSocket _webSocket;

        private AppServerConnection(Socket socket, NetworkStream stream, WebSocket webSocket)
        {
            _socket = socket;
            _stream = stream;
            _webSocket = webSocket;
        }

        public static async Task<AppServerConnection> ConnectAsync(string socketPath, CancellationToken ct)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
            var stream = new NetworkStream(socket, ownsSocket: false);

            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var request = Encoding.ASCII.GetBytes(
                "GET / HTTP/1.1\r\n" +
                "Host: localhost\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                $"Sec-WebSocket-Key: {key}\r\n" +
                "Sec-WebSocket-Version: 13\r\n\r\n");
            await stream.WriteAsync(request, ct);

            var response = new List<byte>();
            var one = new byte[1];
            while (response.Count < 16 * 1024)
            {
                if (await stream.ReadAsync(one, ct) == 0)
                    throw new IOException("Codex app-server closed during WebSocket handshake.");
                response.Add(one[0]);
                var n = response.Count;
                if (n >= 4 && response[n - 4] == '\r' && response[n - 3] == '\n'
                           && response[n - 2] == '\r' && response[n - 1] == '\n')
                    break;
            }
            var headers = Encoding.ASCII.GetString(response.ToArray());
            if (!headers.StartsWith("HTTP/1.1 101", StringComparison.Ordinal))
                throw new WebSocketException($"Codex app-server rejected WebSocket handshake: {headers.Split('\r', '\n')[0]}");

            var webSocket = WebSocket.CreateFromStream(stream, isServer: false, subProtocol: null,
                keepAliveInterval: TimeSpan.FromSeconds(30));
            return new(socket, stream, webSocket);
        }

        public Task SendAsync(object message, CancellationToken ct)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
            return _webSocket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }

        public async Task<JsonDocument> ReadResponseAsync(int id, CancellationToken ct)
        {
            while (true)
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                WebSocketReceiveResult result;
                do
                {
                    result = await _webSocket.ReceiveAsync(chunk, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new IOException("Codex app-server closed before responding.");
                    buffer.Write(chunk, 0, result.Count);
                } while (!result.EndOfMessage);

                var document = JsonDocument.Parse(buffer.ToArray());
                if (document.RootElement.TryGetProperty("id", out var responseId)
                    && responseId.ValueKind == JsonValueKind.Number
                    && responseId.GetInt32() == id)
                    return document;
                document.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_webSocket.State == WebSocketState.Open)
                    await _webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            }
            catch { /* best effort cleanup */ }
            _webSocket.Dispose();
            await _stream.DisposeAsync();
            _socket.Dispose();
        }
    }
}
