using System.Net.WebSockets;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotChatApp.Services;

public class ChatClientService : IAsyncDisposable
{
    ClientWebSocket? _socket;
    CancellationTokenSource? _receiveLoopCts;
    Task? _receiveLoopTask;
    CancellationTokenSource? _backgroundConnectCts;
    readonly Dictionary<string, TaskCompletionSource<McpResult>> _pendingMcpRequests = new();
    readonly Dictionary<string, TaskCompletionSource<SessionsListResult>> _pendingSessionsListRequests = new();
    readonly Dictionary<string, TaskCompletionSource<SessionsHistoryResult>> _pendingSessionsHistoryRequests = new();
    readonly Dictionary<string, TaskCompletionSource<FsListDirResult>> _pendingFsListDirRequests = new();
    readonly Dictionary<string, TaskCompletionSource<FsGitCloneResult>> _pendingFsGitCloneRequests = new();
    readonly Dictionary<string, TaskCompletionSource<ServerInfoResult>> _pendingServerInfoRequests = new();
    readonly Dictionary<string, TaskCompletionSource<SessionsUpdateMetaResult>> _pendingSessionsUpdateMetaRequests = new();

    public event Action<string>? OnDelta;
    public event Action<string>? OnFinal;
    public event Action<string>? OnError;
    public event Action<bool>? OnConnectionChanged;
    public event Action<ToolEventArgs>? OnToolEvent;

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public async Task ConnectAsync(string serverUrl, string authToken, CancellationToken ct = default)
    {
        await DisconnectAsync();

        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {authToken}");

        var uri = new Uri(serverUrl);
        await socket.ConnectAsync(uri, ct);
        _socket = socket;
        OnConnectionChanged?.Invoke(true);

        // A successful connection - whether from a foreground ConnectWithRetryAsync call or from the
        // background retry loop itself - means any pending background retry loop is no longer needed.
        _backgroundConnectCts?.Cancel();
        _backgroundConnectCts = null;

        _receiveLoopCts = new CancellationTokenSource();
        _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(socket, _receiveLoopCts.Token));
    }

    /// <summary>
    /// Connects with retries on failure. Even with LocalNetworkAccessTrigger firing the
    /// local-network-access permission prompt (NSLocalNetworkUsageDescription) at app startup, the
    /// user might not notice and answer it for a while - while that system dialog is up, the socket
    /// connect fails outright even though it would succeed the moment they tap Allow. In practice
    /// this can take the user several seconds to notice, so this retries for up to ~30s (15 attempts,
    /// 2s apart) rather than giving up after just a few seconds and surfacing a scary error while
    /// they're still looking at the permission prompt.
    ///
    /// Even after tapping Allow, iOS has been observed to take well over 30s before it actually lets
    /// traffic through (a known local-network-privacy quirk - the grant doesn't seem to take effect
    /// immediately at the kernel/network-extension level). So if every bounded attempt here still
    /// fails, this hands off to a long-running background retry loop (see
    /// EnsureConnectingInBackground) before giving up and surfacing an error, so that the *next*
    /// time the user checks (e.g. re-opening Sessions), the connection is already up instead of
    /// needing yet another manual Retry.
    /// </summary>
    public async Task ConnectWithRetryAsync(string serverUrl, string authToken, int maxAttempts = 15, CancellationToken ct = default)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await ConnectAsync(serverUrl, authToken, ct);
                return;
            }
            catch when (attempt < maxAttempts)
            {
                await Task.Delay(2000, ct);
            }
            catch
            {
                EnsureConnectingInBackground(serverUrl, authToken);
                throw;
            }
        }
    }

    /// <summary>
    /// Starts a long-running, low-frequency retry loop if one isn't already running and we're not
    /// already connected. Unlike ConnectWithRetryAsync, this doesn't block the caller and isn't
    /// bounded to a UI-friendly ~30s - it keeps trying for up to ~10 minutes (200 attempts, 3s
    /// apart) to ride out however long iOS actually takes to start allowing local-network traffic
    /// after the user grants the permission prompt. Safe to call repeatedly/redundantly.
    /// </summary>
    public void EnsureConnectingInBackground(string serverUrl, string authToken)
    {
        if (IsConnected) return;
        if (_backgroundConnectCts is { IsCancellationRequested: false }) return;

        var cts = new CancellationTokenSource();
        _backgroundConnectCts = cts;
        var ct = cts.Token;

        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 200 && !ct.IsCancellationRequested; attempt++)
            {
                try
                {
                    await ConnectAsync(serverUrl, authToken, ct);
                    return;
                }
                catch
                {
                    try { await Task.Delay(3000, ct); }
                    catch { return; }
                }
            }
        }, ct);
    }

    public async Task<List<McpServerSummary>> ListMcpServersAsync(CancellationToken ct = default)
    {
        var result = await SendMcpRequestAsync(new OutgoingMcpListMessage { RequestId = Guid.NewGuid().ToString("N") }, ct);
        return result.Servers ?? new List<McpServerSummary>();
    }

    public Task<McpServerSummary?> AddMcpServerAsync(
        string name,
        string transport,
        string? command = null,
        List<string>? args = null,
        string? url = null,
        Dictionary<string, string>? env = null,
        Dictionary<string, string>? headers = null,
        CancellationToken ct = default)
    {
        var msg = new OutgoingMcpAddMessage
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Name = name,
            Transport = transport,
            Command = command,
            Args = args,
            Url = url,
            Env = env,
            Headers = headers,
        };
        return SendMcpRequestAsync(msg, ct).ContinueWith(t => t.Result.Server, ct);
    }

    public async Task RemoveMcpServerAsync(string name, CancellationToken ct = default)
    {
        await SendMcpRequestAsync(new OutgoingMcpRemoveMessage { RequestId = Guid.NewGuid().ToString("N"), Name = name }, ct);
    }

    async Task<McpResult> SendMcpRequestAsync(object message, CancellationToken ct)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Not connected to server.");
        }

        var requestId = message switch
        {
            OutgoingMcpListMessage m => m.RequestId,
            OutgoingMcpAddMessage m => m.RequestId,
            OutgoingMcpRemoveMessage m => m.RequestId,
            _ => throw new ArgumentOutOfRangeException(nameof(message)),
        };

        var tcs = new TaskCompletionSource<McpResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingMcpRequests[requestId] = tcs;

        var payload = JsonSerializer.Serialize(message, message.GetType());
        var bytes = Encoding.UTF8.GetBytes(payload);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);

        using var reg = ct.Register(() => tcs.TrySetCanceled());
        var result = await tcs.Task;
        if (!result.Ok)
        {
            throw new InvalidOperationException(result.Error ?? "MCP request failed");
        }
        return result;
    }

    /// <summary>
    /// On iOS, a socket can look "Open" (<see cref="IsConnected"/> stays true) even though the OS is
    /// silently dropping its traffic while the local-network-access permission prompt is unanswered
    /// or was just answered - the receive loop doesn't always notice right away, so a request sent
    /// over that zombie socket can otherwise hang forever with no error and no timeout. Every
    /// request/response call bounds itself with a timeout and, on expiry, force-disconnects so the
    /// *next* attempt (whether automatic or a user-tapped Retry) gets a genuinely fresh connection
    /// via ConnectWithRetryAsync instead of reusing the same dead socket.
    /// </summary>
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    public async Task<List<SessionSummary>> ListSessionsAsync(CancellationToken ct = default)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Not connected to server.");
        }

        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<SessionsListResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingSessionsListRequests[requestId] = tcs;

        try
        {
            var payload = JsonSerializer.Serialize(new OutgoingSessionsListMessage { RequestId = requestId });
            var bytes = Encoding.UTF8.GetBytes(payload);
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);

            var result = await WaitWithTimeoutAsync(tcs, ct);
            if (!result.Ok)
            {
                throw new InvalidOperationException(result.Error ?? "Failed to list sessions");
            }
            return result.Sessions ?? new List<SessionSummary>();
        }
        finally
        {
            _pendingSessionsListRequests.Remove(requestId);
        }
    }

    public async Task<List<SessionTurn>> GetSessionHistoryAsync(string sessionId, CancellationToken ct = default)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Not connected to server.");
        }

        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<SessionsHistoryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingSessionsHistoryRequests[requestId] = tcs;

        try
        {
            var payload = JsonSerializer.Serialize(new OutgoingSessionsHistoryMessage { RequestId = requestId, SessionId = sessionId });
            var bytes = Encoding.UTF8.GetBytes(payload);
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);

            var result = await WaitWithTimeoutAsync(tcs, ct);
            if (!result.Ok)
            {
                throw new InvalidOperationException(result.Error ?? "Failed to load session history");
            }
            return result.Turns ?? new List<SessionTurn>();
        }
        finally
        {
            _pendingSessionsHistoryRequests.Remove(requestId);
        }
    }

    /// <summary>
    /// Awaits a pending request's TaskCompletionSource with a bounded timeout on top of the caller's
    /// own cancellation token. On timeout, force-disconnects (see remarks on RequestTimeout above)
    /// so a stale, silently-blocked socket doesn't keep failing every subsequent attempt the same way.
    /// </summary>
    async Task<T> WaitWithTimeoutAsync<T>(TaskCompletionSource<T> tcs, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(RequestTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        using var reg = linkedCts.Token.Register(() => tcs.TrySetCanceled());

        try
        {
            return await tcs.Task;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            await DisconnectAsync();
            throw new TimeoutException("Timed out waiting for the server. This can happen right after granting local network access - try again.");
        }
    }

    public async Task SendChatAsync(string conversationId, string text, List<ChatAttachment>? attachments = null, string? cwd = null, CancellationToken ct = default)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Not connected to server.");
        }

        var payload = JsonSerializer.Serialize(new OutgoingChatMessage
        {
            Type = "chat",
            ConversationId = conversationId,
            Text = text,
            Attachments = attachments is { Count: > 0 } ? attachments : null,
            Cwd = cwd
        });
        var bytes = Encoding.UTF8.GetBytes(payload);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    /// <summary>Lists subdirectories under `path` (or the configured browse roots themselves, if `path` is omitted) for the new-session folder picker.</summary>
    public async Task<FsListDirResult> ListDirAsync(string? path = null, CancellationToken ct = default)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Not connected to server.");
        }

        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<FsListDirResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingFsListDirRequests[requestId] = tcs;
        try
        {
            var payload = JsonSerializer.Serialize(new OutgoingFsListDirMessage { RequestId = requestId, Path = path });
            var bytes = Encoding.UTF8.GetBytes(payload);
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);

            var result = await WaitWithTimeoutAsync(tcs, ct);
            if (!result.Ok)
            {
                throw new InvalidOperationException(result.Error ?? "Failed to list folder");
            }
            return result;
        }
        finally
        {
            _pendingFsListDirRequests.Remove(requestId);
        }
    }

    /// <summary>Clones a git repository into a new subfolder of `parentPath`, returning the resulting absolute path.</summary>
    public async Task<string> GitCloneAsync(string parentPath, string repoUrl, string? destName = null, CancellationToken ct = default)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Not connected to server.");
        }

        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<FsGitCloneResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingFsGitCloneRequests[requestId] = tcs;
        try
        {
            var payload = JsonSerializer.Serialize(new OutgoingFsGitCloneMessage
            {
                RequestId = requestId,
                ParentPath = parentPath,
                RepoUrl = repoUrl,
                DestName = destName
            });
            var bytes = Encoding.UTF8.GetBytes(payload);
            // Cloning can take a while on a slow connection/large repo - give it much more headroom
            // than the default request timeout rather than force-disconnecting a healthy clone in progress.
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, linkedCts.Token);

            var result = await tcs.Task.WaitAsync(linkedCts.Token);
            if (!result.Ok)
            {
                throw new InvalidOperationException(result.Error ?? "Failed to clone repository");
            }
            return result.Path ?? throw new InvalidOperationException("Clone succeeded but no path was returned.");
        }
        finally
        {
            _pendingFsGitCloneRequests.Remove(requestId);
        }
    }

    /// <summary>Fetches this server's environment/version metadata (OS, CLI version, model, etc.) for display and future controller-session use.</summary>
    public async Task<ServerInfo> GetServerInfoAsync(CancellationToken ct = default)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Not connected to server.");
        }

        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ServerInfoResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingServerInfoRequests[requestId] = tcs;
        try
        {
            var payload = JsonSerializer.Serialize(new OutgoingServerInfoMessage { RequestId = requestId });
            var bytes = Encoding.UTF8.GetBytes(payload);
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);

            var result = await WaitWithTimeoutAsync(tcs, ct);
            if (!result.Ok || result.Info is null)
            {
                throw new InvalidOperationException(result.Error ?? "Failed to get server info");
            }
            return result.Info;
        }
        finally
        {
            _pendingServerInfoRequests.Remove(requestId);
        }
    }

    /// <summary>
    /// Sets (or, with `label: null`, clears) a session's display label - a sidecar annotation only;
    /// never touches the CLI's own session-store.db. Split from archiving into its own message so the
    /// wire payload never has to represent "leave the other field unchanged" ambiguously.
    /// </summary>
    public async Task SetSessionLabelAsync(string sessionId, string? label, CancellationToken ct = default)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Not connected to server.");
        }

        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<SessionsUpdateMetaResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingSessionsUpdateMetaRequests[requestId] = tcs;
        try
        {
            var payload = JsonSerializer.Serialize(new OutgoingSessionsSetLabelMessage
            {
                RequestId = requestId,
                SessionId = sessionId,
                Label = label,
            });
            var bytes = Encoding.UTF8.GetBytes(payload);
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);

            var result = await WaitWithTimeoutAsync(tcs, ct);
            if (!result.Ok)
            {
                throw new InvalidOperationException(result.Error ?? "Failed to set session label");
            }
        }
        finally
        {
            _pendingSessionsUpdateMetaRequests.Remove(requestId);
        }
    }

    /// <summary>Sets a session's archived flag (the "soft delete" - see PBI-021) - a sidecar annotation only.</summary>
    public async Task SetSessionArchivedAsync(string sessionId, bool archived, CancellationToken ct = default)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Not connected to server.");
        }

        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<SessionsUpdateMetaResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingSessionsUpdateMetaRequests[requestId] = tcs;
        try
        {
            var payload = JsonSerializer.Serialize(new OutgoingSessionsSetArchivedMessage
            {
                RequestId = requestId,
                SessionId = sessionId,
                Archived = archived,
            });
            var bytes = Encoding.UTF8.GetBytes(payload);
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);

            var result = await WaitWithTimeoutAsync(tcs, ct);
            if (!result.Ok)
            {
                throw new InvalidOperationException(result.Error ?? "Failed to update session");
            }
        }
        finally
        {
            _pendingSessionsUpdateMetaRequests.Remove(requestId);
        }
    }

    async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var messageBuilder = new StringBuilder();
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                messageBuilder.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        OnConnectionChanged?.Invoke(false);
                        return;
                    }
                    messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                HandleIncoming(messageBuilder.ToString());
            }
        }
        catch (OperationCanceledException)
        {
            // expected on disconnect
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex.Message);
        }
        finally
        {
            OnConnectionChanged?.Invoke(false);
        }
    }

    void HandleIncoming(string json)
    {
        IncomingServerMessage? msg;
        try
        {
            msg = JsonSerializer.Deserialize<IncomingServerMessage>(json);
        }
        catch
        {
            return;
        }
        if (msg is null) return;

        switch (msg.Type)
        {
            case "delta":
                if (msg.Text is not null) OnDelta?.Invoke(msg.Text);
                break;
            case "final":
                OnFinal?.Invoke(msg.Text ?? string.Empty);
                break;
            case "error":
                OnError?.Invoke(msg.Message ?? "Unknown server error");
                break;
            case "tool":
                OnToolEvent?.Invoke(new ToolEventArgs(msg.Status ?? "start", msg.Name ?? "tool", msg.Summary, msg.Detail, msg.Success));
                break;
            case "mcp:result":
                if (msg.RequestId is not null && _pendingMcpRequests.Remove(msg.RequestId, out var tcs))
                {
                    tcs.TrySetResult(new McpResult(msg.Ok ?? false, msg.Error, msg.Servers, msg.Server));
                }
                break;
            case "sessions:list-result":
                if (msg.RequestId is not null && _pendingSessionsListRequests.Remove(msg.RequestId, out var sessionsListTcs))
                {
                    sessionsListTcs.TrySetResult(new SessionsListResult(msg.Ok ?? false, msg.Error, msg.Sessions));
                }
                break;
            case "sessions:history-result":
                if (msg.RequestId is not null && _pendingSessionsHistoryRequests.Remove(msg.RequestId, out var sessionsHistoryTcs))
                {
                    sessionsHistoryTcs.TrySetResult(new SessionsHistoryResult(msg.Ok ?? false, msg.Error, msg.Turns));
                }
                break;
            case "fs:list-dir-result":
                if (msg.RequestId is not null && _pendingFsListDirRequests.Remove(msg.RequestId, out var fsListDirTcs))
                {
                    fsListDirTcs.TrySetResult(new FsListDirResult(msg.Ok ?? false, msg.Error, msg.Path, msg.ParentPath, msg.Entries, msg.Roots));
                }
                break;
            case "fs:git-clone-result":
                if (msg.RequestId is not null && _pendingFsGitCloneRequests.Remove(msg.RequestId, out var fsGitCloneTcs))
                {
                    fsGitCloneTcs.TrySetResult(new FsGitCloneResult(msg.Ok ?? false, msg.Error, msg.Path));
                }
                break;
            case "server:info-result":
                if (msg.RequestId is not null && _pendingServerInfoRequests.Remove(msg.RequestId, out var serverInfoTcs))
                {
                    serverInfoTcs.TrySetResult(new ServerInfoResult(msg.Ok ?? false, msg.Error, msg.Info));
                }
                break;
            case "sessions:update-meta-result":
                if (msg.RequestId is not null && _pendingSessionsUpdateMetaRequests.Remove(msg.RequestId, out var updateMetaTcs))
                {
                    updateMetaTcs.TrySetResult(new SessionsUpdateMetaResult(msg.Ok ?? false, msg.Error));
                }
                break;
        }
    }

    public async Task DisconnectAsync()
    {
        _receiveLoopCts?.Cancel();
        if (_socket is { State: WebSocketState.Open })
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
            }
            catch
            {
                // ignore
            }
        }
        _socket?.Dispose();
        _socket = null;

        if (_receiveLoopTask is not null)
        {
            try { await _receiveLoopTask; } catch { /* ignore */ }
        }
        _receiveLoopTask = null;
    }

    public async ValueTask DisposeAsync()
    {
        _backgroundConnectCts?.Cancel();
        _backgroundConnectCts = null;
        await DisconnectAsync();
    }

    class OutgoingChatMessage
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "chat";
        [JsonPropertyName("conversationId")] public string ConversationId { get; set; } = string.Empty;
        [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
        [JsonPropertyName("attachments")] public List<ChatAttachment>? Attachments { get; set; }
        [JsonPropertyName("cwd")] public string? Cwd { get; set; }
    }

    class IncomingServerMessage
    {
        [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
        [JsonPropertyName("conversationId")] public string? ConversationId { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("summary")] public string? Summary { get; set; }
        [JsonPropertyName("detail")] public string? Detail { get; set; }
        [JsonPropertyName("success")] public bool? Success { get; set; }
        [JsonPropertyName("requestId")] public string? RequestId { get; set; }
        [JsonPropertyName("ok")] public bool? Ok { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("servers")] public List<McpServerSummary>? Servers { get; set; }
        [JsonPropertyName("server")] public McpServerSummary? Server { get; set; }
        [JsonPropertyName("sessions")] public List<SessionSummary>? Sessions { get; set; }
        [JsonPropertyName("turns")] public List<SessionTurn>? Turns { get; set; }
        [JsonPropertyName("path")] public string? Path { get; set; }
        [JsonPropertyName("parentPath")] public string? ParentPath { get; set; }
        [JsonPropertyName("entries")] public List<FsEntry>? Entries { get; set; }
        [JsonPropertyName("roots")] public List<string>? Roots { get; set; }
        [JsonPropertyName("info")] public ServerInfo? Info { get; set; }
    }

    class OutgoingMcpListMessage
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "mcp:list";
        [JsonPropertyName("requestId")] public string RequestId { get; set; } = string.Empty;
    }

    class OutgoingMcpAddMessage
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "mcp:add";
        [JsonPropertyName("requestId")] public string RequestId { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("transport")] public string Transport { get; set; } = "stdio";
        [JsonPropertyName("command")] public string? Command { get; set; }
        [JsonPropertyName("args")] public List<string>? Args { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("env")] public Dictionary<string, string>? Env { get; set; }
        [JsonPropertyName("headers")] public Dictionary<string, string>? Headers { get; set; }
    }

    class OutgoingMcpRemoveMessage
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "mcp:remove";
        [JsonPropertyName("requestId")] public string RequestId { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    }

    class OutgoingSessionsListMessage
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "sessions:list";
        [JsonPropertyName("requestId")] public string RequestId { get; set; } = string.Empty;
    }

    class OutgoingSessionsHistoryMessage
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "sessions:history";
        [JsonPropertyName("requestId")] public string RequestId { get; set; } = string.Empty;
        [JsonPropertyName("sessionId")] public string SessionId { get; set; } = string.Empty;
    }

    class OutgoingFsListDirMessage
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "fs:list-dir";
        [JsonPropertyName("requestId")] public string RequestId { get; set; } = string.Empty;
        [JsonPropertyName("path")] public string? Path { get; set; }
    }

    class OutgoingFsGitCloneMessage
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "fs:git-clone";
        [JsonPropertyName("requestId")] public string RequestId { get; set; } = string.Empty;
        [JsonPropertyName("parentPath")] public string ParentPath { get; set; } = string.Empty;
        [JsonPropertyName("repoUrl")] public string RepoUrl { get; set; } = string.Empty;
        [JsonPropertyName("destName")] public string? DestName { get; set; }
    }

    class OutgoingServerInfoMessage
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "server:info";
        [JsonPropertyName("requestId")] public string RequestId { get; set; } = string.Empty;
    }

    /// <summary>Only ever carries `label` on the wire - never `archived` - so the server can't misread this as also clearing/setting the archived flag.</summary>
    class OutgoingSessionsSetLabelMessage
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "sessions:update-meta";
        [JsonPropertyName("requestId")] public string RequestId { get; set; } = string.Empty;
        [JsonPropertyName("sessionId")] public string SessionId { get; set; } = string.Empty;
        [JsonPropertyName("label")] public string? Label { get; set; }
    }

    /// <summary>Only ever carries `archived` on the wire - never `label` - for the same reason as OutgoingSessionsSetLabelMessage.</summary>
    class OutgoingSessionsSetArchivedMessage
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "sessions:update-meta";
        [JsonPropertyName("requestId")] public string RequestId { get; set; } = string.Empty;
        [JsonPropertyName("sessionId")] public string SessionId { get; set; } = string.Empty;
        [JsonPropertyName("archived")] public bool Archived { get; set; }
    }

    record McpResult(bool Ok, string? Error, List<McpServerSummary>? Servers, McpServerSummary? Server);
    record SessionsListResult(bool Ok, string? Error, List<SessionSummary>? Sessions);
    record SessionsHistoryResult(bool Ok, string? Error, List<SessionTurn>? Turns);
    public record FsListDirResult(bool Ok, string? Error, string? Path, string? ParentPath, List<FsEntry>? Entries, List<string>? Roots);
    record FsGitCloneResult(bool Ok, string? Error, string? Path);
    record ServerInfoResult(bool Ok, string? Error, ServerInfo? Info);
    record SessionsUpdateMetaResult(bool Ok, string? Error);
}

public record ToolEventArgs(string Status, string Name, string? Summary, string? Detail, bool? Success);

/// <summary>A folder or file entry returned by fs:list-dir (folders only, in practice - see server/src/fsBrowser.ts).</summary>
public class FsEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("isDir")] public bool IsDir { get; set; }
}

/// <summary>Server environment/version metadata (see server/src/serverInfo.ts) - shown as a per-server badge and usable by a future controller session.</summary>
public class ServerInfo
{
    [JsonPropertyName("os")] public string Os { get; set; } = string.Empty;
    [JsonPropertyName("hostname")] public string Hostname { get; set; } = string.Empty;
    [JsonPropertyName("appVersion")] public string AppVersion { get; set; } = string.Empty;
    [JsonPropertyName("copilotCliVersion")] public string CopilotCliVersion { get; set; } = string.Empty;
    [JsonPropertyName("nodeVersion")] public string NodeVersion { get; set; } = string.Empty;
    [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
    [JsonPropertyName("workDir")] public string WorkDir { get; set; } = string.Empty;
    [JsonPropertyName("browseRoots")] public List<string> BrowseRoots { get; set; } = new();

    /// <summary>A short glyph for the OS, for compact display next to a server name in the Sessions list.</summary>
    public string OsGlyph => Os switch
    {
        "darwin" => "🍎",
        "win32" => "🪟",
        "linux" => "🐧",
        _ => "💻",
    };
}

/// <summary>An image/file pasted into the composer, sent base64-encoded as part of a chat turn (PBI-019).</summary>
public class ChatAttachment
{
    [JsonPropertyName("mimeType")] public string MimeType { get; set; } = string.Empty;
    [JsonPropertyName("data")] public string Data { get; set; } = string.Empty;
    [JsonPropertyName("fileName")] public string? FileName { get; set; }
}

public class McpServerSummary
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("command")] public string? Command { get; set; }
    [JsonPropertyName("args")] public List<string>? Args { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("tools")] public List<string>? Tools { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }

    public string DisplaySubtitle => Type == "local"
        ? $"{Command} {string.Join(' ', Args ?? new List<string>())}".Trim()
        : (Url ?? string.Empty);
}

public class SessionSummary
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("cwd")] public string Cwd { get; set; } = string.Empty;
    [JsonPropertyName("summary")] public string Summary { get; set; } = string.Empty;
    [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = string.Empty;
    [JsonPropertyName("updatedAt")] public string UpdatedAt { get; set; } = string.Empty;
    [JsonPropertyName("turnCount")] public int TurnCount { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("archived")] public bool Archived { get; set; }

    /// <summary>The label if set, otherwise the folder name from Cwd, otherwise the CLI's auto-generated summary - whatever's most useful to show as the primary title.</summary>
    public string DisplayTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Label)) return Label!;
            if (!string.IsNullOrWhiteSpace(Cwd))
            {
                var folderName = Cwd.TrimEnd('/', '\\').Split('/', '\\').LastOrDefault();
                if (!string.IsNullOrWhiteSpace(folderName)) return folderName!;
            }
            return Summary;
        }
    }

    public string DisplaySubtitle
    {
        get
        {
            var when = DateTimeOffset.TryParse(UpdatedAt, out var dt)
                ? dt.LocalDateTime.ToString("yyyy/MM/dd HH:mm")
                : UpdatedAt;
            return $"{when} ・ {TurnCount} turn(s)";
        }
    }
}

public class SessionTurn
{
    [JsonPropertyName("turnIndex")] public int TurnIndex { get; set; }
    [JsonPropertyName("userMessage")] public string UserMessage { get; set; } = string.Empty;
    [JsonPropertyName("assistantResponse")] public string AssistantResponse { get; set; } = string.Empty;
    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = string.Empty;
}
