using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ParleyCli.Serialization;

namespace ParleyCli.Integrations;

/// <summary>
/// Best-effort bridge to a running Codex app-server daemon. Runtime identity is discovered on every
/// call by matching the current Parley role owner's sid against app-server's loaded thread ids; no
/// harness type is persisted in the roster.
/// </summary>
public sealed class CodexWakeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

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
            await connection.SendAsync(new RpcRequest<InitializeParams>
            {
                Method = "initialize",
                Id = 1,
                Params = new InitializeParams
                {
                    ClientInfo = new ClientInfo
                    {
                        Name = "parley_cli", Title = "Parley CLI", Version = "1"
                    }
                }
            }, ct);
            await connection.SendAsync(new RpcRequest<EmptyParams>
                { Method = "initialized", Params = new EmptyParams() }, ct);
            var initialized = await connection.ReadResponseAsync<RpcResponse>(1, ct);
            if (HasError(initialized, out var initError))
                return new(WakeStatus.Failed, initError);

            await connection.SendAsync(new RpcRequest<EmptyParams>
                { Method = "thread/loaded/list", Id = 2, Params = new EmptyParams() }, ct);
            var loaded = await connection.ReadResponseAsync<RpcResponse<LoadedThreadsResult>>(2, ct);
            if (HasError(loaded, out var loadedError))
                return new(WakeStatus.Failed, loadedError);

            var isLoaded = loaded.Result?.Data.Contains(threadId, StringComparer.Ordinal) == true;
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

            var status = JsonSerializer.Deserialize(stdout, ParleyJsonContext.Default.DaemonVersionStatus);
            return status?.Status == "running" ? status.SocketPath : null;
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
            await connection.SendAsync(new RpcRequest<ThreadReadParams>
            {
                Method = "thread/read",
                Id = readId,
                Params = new ThreadReadParams { ThreadId = threadId, IncludeTurns = true }
            }, ct);
            var read = await connection.ReadResponseAsync<RpcResponse<ThreadReadResult>>(readId, ct);
            if (HasError(read, out var readError)) return new(WakeStatus.Failed, readError);

            var activeTurnId = read.Result?.Thread?.Turns
                .LastOrDefault(t => t.Status == "inProgress")?.Id;

            var submitId = readId + 1;
            if (activeTurnId is not null)
            {
                await connection.SendAsync(new RpcRequest<TurnSteerParams>
                {
                    Method = "turn/steer",
                    Id = submitId,
                    Params = new TurnSteerParams
                    {
                        ThreadId = threadId,
                        Input = [new TextInput { Text = notification }],
                        ExpectedTurnId = activeTurnId
                    }
                }, ct);
            }
            else
            {
                await connection.SendAsync(new RpcRequest<TurnStartParams>
                {
                    Method = "turn/start",
                    Id = submitId,
                    Params = new TurnStartParams
                    {
                        ThreadId = threadId,
                        Input = [new TextInput { Text = notification }]
                    }
                }, ct);
            }
            var submitted = await connection.ReadResponseAsync<RpcResponse>(submitId, ct);
            if (!HasError(submitted, out var submitError)) return new(WakeStatus.Woken);
            if (attempt == 1) return new(WakeStatus.Failed, submitError);
        }

        return new(WakeStatus.Failed, "Codex thread changed state while waking it.");
    }

    private static bool HasError(RpcResponse response, out string? error)
    {
        if (response.Error is null)
        {
            error = null;
            return false;
        }

        error = response.Error.Message ?? $"JSON-RPC error {response.Error.Code}";
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

        public Task SendAsync<T>(T message, CancellationToken ct)
        {
            var typeInfo = ParleyJsonContext.Default.GetTypeInfo(typeof(T))
                ?? throw new InvalidOperationException($"No JSON metadata registered for {typeof(T).FullName}.");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message, typeInfo);
            return _webSocket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }

        public async Task<TResponse> ReadResponseAsync<TResponse>(int id, CancellationToken ct)
            where TResponse : RpcResponse
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

                var bytes = buffer.ToArray();
                var envelope = JsonSerializer.Deserialize(bytes, ParleyJsonContext.Default.RpcResponse)
                    ?? throw new JsonException("Codex app-server returned an empty JSON-RPC message.");
                if (envelope.Id == id)
                {
                    var typeInfo = ParleyJsonContext.Default.GetTypeInfo(typeof(TResponse))
                        ?? throw new InvalidOperationException($"No JSON metadata registered for {typeof(TResponse).FullName}.");
                    return (TResponse?)JsonSerializer.Deserialize(bytes, typeInfo)
                        ?? throw new JsonException("Codex app-server returned an invalid JSON-RPC response.");
                }
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
