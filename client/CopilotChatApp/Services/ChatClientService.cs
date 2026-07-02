using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotChatApp.Services;

public class ChatClientService : IAsyncDisposable
{
    ClientWebSocket? _socket;
    CancellationTokenSource? _receiveLoopCts;
    Task? _receiveLoopTask;
    readonly Dictionary<string, TaskCompletionSource<McpResult>> _pendingMcpRequests = new();
    readonly Dictionary<string, TaskCompletionSource<SessionsListResult>> _pendingSessionsListRequests = new();
    readonly Dictionary<string, TaskCompletionSource<SessionsHistoryResult>> _pendingSessionsHistoryRequests = new();

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

        _receiveLoopCts = new CancellationTokenSource();
        _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(socket, _receiveLoopCts.Token));
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

    public async Task<List<SessionSummary>> ListSessionsAsync(CancellationToken ct = default)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Not connected to server.");
        }

        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<SessionsListResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingSessionsListRequests[requestId] = tcs;

        var payload = JsonSerializer.Serialize(new OutgoingSessionsListMessage { RequestId = requestId });
        var bytes = Encoding.UTF8.GetBytes(payload);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);

        using var reg = ct.Register(() => tcs.TrySetCanceled());
        var result = await tcs.Task;
        if (!result.Ok)
        {
            throw new InvalidOperationException(result.Error ?? "Failed to list sessions");
        }
        return result.Sessions ?? new List<SessionSummary>();
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

        var payload = JsonSerializer.Serialize(new OutgoingSessionsHistoryMessage { RequestId = requestId, SessionId = sessionId });
        var bytes = Encoding.UTF8.GetBytes(payload);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);

        using var reg = ct.Register(() => tcs.TrySetCanceled());
        var result = await tcs.Task;
        if (!result.Ok)
        {
            throw new InvalidOperationException(result.Error ?? "Failed to load session history");
        }
        return result.Turns ?? new List<SessionTurn>();
    }

    public async Task SendChatAsync(string conversationId, string text, CancellationToken ct = default)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Not connected to server.");
        }

        var payload = JsonSerializer.Serialize(new OutgoingChatMessage
        {
            Type = "chat",
            ConversationId = conversationId,
            Text = text
        });
        var bytes = Encoding.UTF8.GetBytes(payload);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
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
        await DisconnectAsync();
    }

    class OutgoingChatMessage
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "chat";
        [JsonPropertyName("conversationId")] public string ConversationId { get; set; } = string.Empty;
        [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
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

    record McpResult(bool Ok, string? Error, List<McpServerSummary>? Servers, McpServerSummary? Server);
    record SessionsListResult(bool Ok, string? Error, List<SessionSummary>? Sessions);
    record SessionsHistoryResult(bool Ok, string? Error, List<SessionTurn>? Turns);
}

public record ToolEventArgs(string Status, string Name, string? Summary, string? Detail, bool? Success);

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
    [JsonPropertyName("summary")] public string Summary { get; set; } = string.Empty;
    [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = string.Empty;
    [JsonPropertyName("updatedAt")] public string UpdatedAt { get; set; } = string.Empty;
    [JsonPropertyName("turnCount")] public int TurnCount { get; set; }

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
