using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ParleyCli.Serialization;
using Serilog;

namespace ParleyCli.Integrations;

/// <summary>
/// Best-effort bridge to a running Codex app-server daemon. Runtime identity is discovered on every
/// call by matching the current Parley role owner's sid against app-server's loaded thread ids; no
/// harness type is persisted in the roster.
/// </summary>
public sealed class CodexWakeClient : IWakeClient
{
    private const int RolloutTailBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string Name => "Codex";
    public string TransportName => "Codex app-server";

    public Task<WakeResult> ProbeAsync(string threadId, CancellationToken ct) =>
        ConnectAndInspectAsync(threadId, null, ct);

    public Task<WakeResult> WakeAsync(string threadId, string notification, CancellationToken ct) =>
        ConnectAndInspectAsync(threadId, notification, ct);

    private static async Task<WakeResult> ConnectAndInspectAsync(
        string threadId, string? notification, CancellationToken outerCt)
    {
        try
        {
            using var discoveryTimeout = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
            discoveryTimeout.CancelAfter(TimeSpan.FromSeconds(4));
            var socketPath = await FindSocketAsync(discoveryTimeout.Token);
            if (socketPath is null) return new(WakeStatus.Unavailable);

            var clientMessageId = notification is null
                ? null
                : CreateClientMessageId(threadId, notification);
            string? rolloutPath = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (clientMessageId is not null
                    && await WaitForClientMessageAsync(rolloutPath, clientMessageId, outerCt))
                {
                    Log.Verbose("[trace] Codex wake reconciled from rollout; attempt={Attempt}", attempt + 1);
                    return new(WakeStatus.Woken);
                }

                using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
                attemptTimeout.CancelAfter(TimeSpan.FromSeconds(4));
                try
                {
                    await using var connection = await AppServerConnection.ConnectAsync(
                        socketPath, attemptTimeout.Token);
                    var prepared = await PrepareAsync(connection, threadId, attemptTimeout.Token);
                    if (prepared.Status != WakeStatus.Woken) return prepared;
                    if (notification is null) return prepared;

                    var submission = await SubmitAsync(connection, threadId, notification,
                        clientMessageId!, attemptTimeout.Token, path => rolloutPath = path);
                    return submission;
                }
                catch (OperationCanceledException) when (!outerCt.IsCancellationRequested)
                {
                    Log.Verbose("[trace] Codex wake attempt timed out; attempt={Attempt} retry={Retry}",
                        attempt + 1, attempt == 0);
                    if (attempt == 1)
                    {
                        if (clientMessageId is not null
                            && await WaitForClientMessageAsync(rolloutPath, clientMessageId, outerCt))
                            return new(WakeStatus.Woken);
                        return new(WakeStatus.Failed, "Codex app-server wake timed out after 2 attempts.");
                    }
                }
            }

            return new(WakeStatus.Failed, "Codex app-server wake timed out after 2 attempts.");
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

    private static async Task<WakeResult> PrepareAsync(
        AppServerConnection connection, string threadId, CancellationToken ct)
    {
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

        return loaded.Result?.Data.Contains(threadId, StringComparer.Ordinal) == true
            ? new(WakeStatus.Woken)
            : new(WakeStatus.Unavailable);
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
        AppServerConnection connection, string threadId, string notification, string clientMessageId,
        CancellationToken ct, Action<string?> setRolloutPath)
    {
        var nextId = 3;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var readId = nextId++;
            await connection.SendAsync(new RpcRequest<ThreadReadParams>
            {
                Method = "thread/read",
                Id = readId,
                Params = new ThreadReadParams { ThreadId = threadId, IncludeTurns = false }
            }, ct);
            var read = await connection.ReadResponseAsync<RpcResponse<ThreadReadResult>>(readId, ct);
            if (HasError(read, out var readError)) return new(WakeStatus.Failed, readError);

            var thread = read.Result?.Thread;
            setRolloutPath(thread?.Path);
            var activeTurnId = string.Equals(thread?.Status?.Type, "active", StringComparison.Ordinal)
                ? FindActiveTurnInRolloutTail(thread?.Path)
                : null;

            if (!string.Equals(thread?.Status?.Type, "idle", StringComparison.Ordinal)
                && activeTurnId is null)
            {
                var historyId = nextId++;
                await connection.SendAsync(new RpcRequest<ThreadReadParams>
                {
                    Method = "thread/read",
                    Id = historyId,
                    Params = new ThreadReadParams { ThreadId = threadId, IncludeTurns = true }
                }, ct);
                var history = await connection.ReadResponseAsync<RpcResponse<ThreadReadResult>>(historyId, ct);
                if (HasError(history, out var historyError)) return new(WakeStatus.Failed, historyError);
                activeTurnId = history.Result?.Thread?.Turns
                    .LastOrDefault(t => t.Status == "inProgress")?.Id;
            }

            var submitId = nextId++;
            if (activeTurnId is not null)
            {
                await connection.SendAsync(new RpcRequest<TurnSteerParams>
                {
                    Method = "turn/steer",
                    Id = submitId,
                    Params = new TurnSteerParams
                    {
                        ThreadId = threadId,
                        ClientUserMessageId = clientMessageId,
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
                        ClientUserMessageId = clientMessageId,
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

    internal static string CreateClientMessageId(string threadId, string notification)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{threadId}\0{notification}"));
        return $"parley-{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }

    private static async Task<bool> WaitForClientMessageAsync(
        string? path, string clientMessageId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        for (var check = 0; check < 4; check++)
        {
            if (RolloutTailContainsClientMessage(path, clientMessageId)) return true;
            if (check < 3) await Task.Delay(100, ct);
        }
        return false;
    }

    internal static bool RolloutTailContainsClientMessage(string? path, string clientMessageId)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        foreach (var entry in ReadRolloutTail(path))
        {
            var payload = entry.Payload;
            if (string.Equals(entry.Type, "event_msg", StringComparison.Ordinal)
                && string.Equals(payload?.Type, "user_message", StringComparison.Ordinal)
                && string.Equals(payload?.ClientId, clientMessageId, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    internal static string? FindActiveTurnInRolloutTail(string? path)
    {
        string? lastLifecycleType = null;
        string? lastTurnId = null;
        foreach (var entry in ReadRolloutTail(path))
        {
            if (!string.Equals(entry.Type, "event_msg", StringComparison.Ordinal)) continue;
            var payload = entry.Payload;
            if (payload?.Type is not ("task_started" or "task_complete")) continue;
            lastLifecycleType = payload.Type;
            lastTurnId = payload.TurnId;
        }
        return lastLifecycleType == "task_started" ? lastTurnId : null;
    }

    private static IReadOnlyList<CodexRolloutEntry> ReadRolloutTail(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return [];

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - RolloutTailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
                bufferSize: 4096, leaveOpen: false);

            if (start > 0) reader.ReadLine(); // discard the partial first record

            var entries = new List<CodexRolloutEntry>();
            while (reader.ReadLine() is { } line)
            {
                if (!line.Contains("task_started", StringComparison.Ordinal)
                    && !line.Contains("task_complete", StringComparison.Ordinal)
                    && !line.Contains("user_message", StringComparison.Ordinal)) continue;

                CodexRolloutEntry? entry;
                try
                {
                    entry = JsonSerializer.Deserialize(line, ParleyJsonContext.Default.CodexRolloutEntry);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (entry is not null) entries.Add(entry);
            }

            return entries;
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
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
