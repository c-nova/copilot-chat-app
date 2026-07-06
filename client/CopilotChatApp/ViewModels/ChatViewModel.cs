using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Input;
using CopilotChatApp.Models;
using CopilotChatApp.Services;

namespace CopilotChatApp.ViewModels;

/// <summary>
/// Holds the chat screen's state and behaviour (message list, sending, tool events, biometric
/// gate, session lifecycle) independently of any MAUI Page/control. MainPage.xaml.cs stays a thin
/// shell that only handles things a ViewModel can't reasonably own: DisplayAlert prompts,
/// Clipboard access, Navigation, and scrolling the CollectionView. See PBI-007.
/// </summary>
public class ChatViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    readonly ChatClientService _chatClient = new();
    ChatMessage? _pendingAssistantMessage;
    readonly StringBuilder _pendingAssistantText = new();
    readonly Dictionary<string, Queue<ChatMessage>> _pendingToolMessages = new();
    /// <summary>Working directory to create a brand-new session in - set via <see cref="SetPendingCwd"/> (from the New Chat folder picker) and sent only on the next turn. Null means "use the server's default workspace".</summary>
    string? _pendingCwd;

    /// <summary>
    /// Exposes the already-connected client so other pages (e.g. SessionsPage) can reuse this single
    /// connection instead of opening a second WebSocket. Besides being wasteful, a second connection
    /// re-triggers iOS's local-network-access permission flow, and if the user hasn't answered that
    /// system prompt yet, the second connection attempt fails immediately and surfaces as a confusing
    /// in-app error dialog on top of (or racing) the system one.
    /// </summary>
    public ChatClientService ChatClient => _chatClient;

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    public ObservableCollection<PendingAttachment> PendingAttachments { get; } = new();

    bool _hasPendingAttachments;

    /// <summary>True while at least one image is staged for the next message (PBI-019); drives the attachment strip's visibility.</summary>
    public bool HasPendingAttachments
    {
        get => _hasPendingAttachments;
        private set
        {
            if (_hasPendingAttachments == value) return;
            _hasPendingAttachments = value;
            OnPropertyChanged();
        }
    }

    bool _isSending;
    public bool IsSending
    {
        get => _isSending;
        private set
        {
            if (_isSending == value) return;
            _isSending = value;
            OnPropertyChanged();
            ((RelayCommand)SendCommand).RaiseCanExecuteChanged();
        }
    }

    bool _isWaitingForResponse;

    /// <summary>
    /// True only during the "silent" gap between tapping Send and the first sign of activity
    /// (streamed text or a tool call starting) - drives the "Copilot is working…" indicator so the
    /// user isn't left wondering if the app is hung (see PBI-015). Unlike <see cref="IsSending"/>,
    /// this turns off as soon as *something* visible happens, not just when the turn completes.
    /// </summary>
    public bool IsWaitingForResponse
    {
        get => _isWaitingForResponse;
        private set
        {
            if (_isWaitingForResponse == value) return;
            _isWaitingForResponse = value;
            OnPropertyChanged();
        }
    }

    string _inputText = string.Empty;
    public string InputText
    {
        get => _inputText;
        set
        {
            if (_inputText == value) return;
            _inputText = value;
            OnPropertyChanged();
        }
    }

    bool _isLocked;

    /// <summary>True while the biometric gate (PBI-009) is blocking access; drives the lock overlay.</summary>
    public bool IsLocked
    {
        get => _isLocked;
        private set
        {
            if (_isLocked == value) return;
            _isLocked = value;
            OnPropertyChanged();
        }
    }

    public ICommand SendCommand { get; }
    public ICommand UnlockCommand { get; }

    public ChatViewModel()
    {
        _chatClient.OnDelta += text => MainThread.BeginInvokeOnMainThread(() => AppendAssistantDelta(text));
        _chatClient.OnFinal += text => MainThread.BeginInvokeOnMainThread(() => FinalizeAssistantMessage(text));
        _chatClient.OnError += message => MainThread.BeginInvokeOnMainThread(() => ShowError(message));
        _chatClient.OnToolEvent += args => MainThread.BeginInvokeOnMainThread(() => HandleToolEvent(args));

        // PBI-009: when the app is backgrounded, App.xaml.cs calls BiometricGateService.Lock(), which
        // raises this event so the overlay shows immediately (rather than waiting for the next
        // page-level OnAppearing, which may not fire if this page is already the active one).
        BiometricGateService.Locked += () => MainThread.BeginInvokeOnMainThread(() => IsLocked = true);

        SendCommand = new RelayCommand(SendCurrentInputAsync, () => !IsSending);
        UnlockCommand = new RelayCommand(() => TryUnlockAsync());
    }

    /// <summary>Called from MainPage.OnAppearing: unlocks the biometric gate and seeds the "not configured" hint.</summary>
    public async Task InitializeAsync()
    {
        await TryUnlockAsync();
        if (!await SettingsService.IsConfiguredAsync())
        {
            Messages.Add(new ChatMessage
            {
                Role = ChatRole.System,
                Text = "Server not configured yet. Tap Settings to enter the server URL and token."
            });
            return;
        }

        // Proactively connect as soon as the server is configured, rather than waiting for the
        // user's first Send. On iOS/iPadOS this is the moment the OS shows its "Allow this app to
        // find devices on your local network?" prompt (NSLocalNetworkUsageDescription) - while that
        // prompt is up, the connection attempt fails, which otherwise looked like "nothing works for
        // a while after entering settings". Warming up here surfaces that prompt immediately instead
        // of silently failing the user's first real message. Failures here are swallowed: the user
        // gets a clear error from ShowError when they actually try to send if it's still not working.
        if (!_chatClient.IsConnected)
        {
            try
            {
                await _chatClient.ConnectWithRetryAsync(SettingsService.ServerUrl, await SettingsService.GetAuthTokenAsync());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ChatViewModel] Warm-up connect failed: {ex.Message}");
            }
        }
    }

    /// <summary>Tries to unlock the biometric gate (PBI-009), updating <see cref="IsLocked"/> accordingly.</summary>
    public async Task<bool> TryUnlockAsync()
    {
        var unlocked = await BiometricGateService.EnsureUnlockedAsync();
        IsLocked = !unlocked;
        return unlocked;
    }

    /// <summary>Stages a clipboard-pasted image to be sent with the next message (PBI-019).</summary>
    public void AddPastedImage(byte[] bytes, string mimeType)
    {
        PendingAttachments.Add(new PendingAttachment(bytes, mimeType));
        HasPendingAttachments = PendingAttachments.Count > 0;
    }

    /// <summary>Removes a staged image before it's sent (e.g. the user tapped its ✕ in the preview strip).</summary>
    public void RemovePendingAttachment(PendingAttachment attachment)
    {
        PendingAttachments.Remove(attachment);
        HasPendingAttachments = PendingAttachments.Count > 0;
    }

    async Task SendCurrentInputAsync()
    {
        var text = InputText.Trim();
        if (string.IsNullOrEmpty(text) && PendingAttachments.Count == 0) return;

        if (!await TryUnlockAsync())
        {
            return;
        }

        if (!await SettingsService.IsConfiguredAsync())
        {
            Messages.Add(new ChatMessage
            {
                Role = ChatRole.System,
                Text = "⚠️ Please set the server URL and token in Settings first."
            });
            return;
        }

        var attachments = PendingAttachments.ToList();
        PendingAttachments.Clear();
        HasPendingAttachments = false;

        IsSending = true;
        IsWaitingForResponse = true;
        InputText = string.Empty;

        Messages.Add(new ChatMessage { Role = ChatRole.User, Text = text, Attachments = attachments.Count > 0 ? attachments : null });

        try
        {
            if (!_chatClient.IsConnected)
            {
                await _chatClient.ConnectWithRetryAsync(SettingsService.ServerUrl, await SettingsService.GetAuthTokenAsync());
            }
            var wireAttachments = attachments.Count > 0
                ? attachments.Select(a => new ChatAttachment { MimeType = a.MimeType, Data = Convert.ToBase64String(a.Bytes) }).ToList()
                : null;
            await _chatClient.SendChatAsync(SettingsService.ConversationId, text, wireAttachments, _pendingCwd);
            // Only the very first turn of a brand-new session needs cwd (the server ignores it on
            // every subsequent turn anyway, since it resumes from the session's own recorded cwd) -
            // clearing it here just avoids carrying stale state around for no reason.
            _pendingCwd = null;
        }
        catch (Exception ex)
        {
            ShowError($"Connection failed: {ex.Message}");
            IsSending = false;
            IsWaitingForResponse = false;
        }
    }

    void AppendAssistantDelta(string delta)
    {
        IsWaitingForResponse = false;
        if (_pendingAssistantMessage is null)
        {
            _pendingAssistantText.Clear();
            _pendingAssistantMessage = new ChatMessage { Role = ChatRole.Assistant, Text = string.Empty };
            Messages.Add(_pendingAssistantMessage);
        }
        _pendingAssistantText.Append(delta);
        _pendingAssistantMessage.Text = _pendingAssistantText.ToString();
    }

    void FinalizeAssistantMessage(string finalText)
    {
        IsWaitingForResponse = false;
        if (_pendingAssistantMessage is null)
        {
            Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Text = finalText });
        }
        else
        {
            _pendingAssistantMessage.Text = finalText;
        }
        _pendingAssistantMessage = null;
        IsSending = false;
    }

    void ShowError(string message)
    {
        IsWaitingForResponse = false;
        Messages.Add(new ChatMessage { Role = ChatRole.System, Text = $"⚠️ {message}" });
        _pendingAssistantMessage = null;
        IsSending = false;
    }

    void HandleToolEvent(ToolEventArgs args)
    {
        IsWaitingForResponse = false;
        // If an assistant bubble was started (e.g. streaming reasoning text) before this tool call,
        // seal it in place so the *next* delta/final creates a fresh bubble positioned after the
        // tool rows - otherwise the eventual final answer would end up visually above tool activity
        // that happened after the bubble was first created.
        if (args.Status == "start" && _pendingAssistantMessage is not null)
        {
            if (string.IsNullOrWhiteSpace(_pendingAssistantMessage.Text))
            {
                Messages.Remove(_pendingAssistantMessage);
            }
            _pendingAssistantMessage = null;
            _pendingAssistantText.Clear();
        }

        if (args.Status == "start")
        {
            var label = string.IsNullOrEmpty(args.Summary)
                ? $"{args.Name}"
                : $"{args.Name} — {args.Summary}";
            var detail = $"🔧 {args.Name}\n\n{args.Detail ?? "(no arguments)"}";
            var msg = new ChatMessage { Role = ChatRole.Tool, Text = label, ToolDetail = detail, IsRunning = true };
            Messages.Add(msg);

            if (!_pendingToolMessages.TryGetValue(args.Name, out var queue))
            {
                queue = new Queue<ChatMessage>();
                _pendingToolMessages[args.Name] = queue;
            }
            queue.Enqueue(msg);
        }
        else
        {
            var icon = args.Success == false ? "❌" : "✅";
            var statusLine = args.Success == false ? "Status: ❌ Failed" : "Status: ✅ Completed";
            if (_pendingToolMessages.TryGetValue(args.Name, out var queue) && queue.Count > 0)
            {
                var msg = queue.Dequeue();
                msg.IsRunning = false;
                msg.Text = $"{icon} {args.Name}";
                msg.ToolDetail = $"{msg.ToolDetail}\n\n{statusLine}";
            }
            else
            {
                var detail = $"🔧 {args.Name}\n\n{args.Detail ?? "(no arguments)"}\n\n{statusLine}";
                Messages.Add(new ChatMessage { Role = ChatRole.Tool, Text = $"{icon} {args.Name}", ToolDetail = detail, IsRunning = false });
            }
        }
    }

    public async Task StartNewChatAsync()
    {
        SettingsService.ResetConversation();
        Messages.Clear();
        await _chatClient.DisconnectAsync();
    }

    /// <summary>Sets the working directory the *next* new session should be created in (New Chat folder picker). No-op for a resumed session - the server always uses that session's own recorded cwd instead.</summary>
    public void SetPendingCwd(string cwd) => _pendingCwd = cwd;

    public void ApplyResumedSession(SessionSummary session, List<SessionTurn> turns)
    {
        SettingsService.SetConversation(session.Id);
        Messages.Clear();
        _pendingAssistantMessage = null;
        _pendingAssistantText.Clear();
        _pendingToolMessages.Clear();

        foreach (var turn in turns)
        {
            if (!string.IsNullOrWhiteSpace(turn.UserMessage))
            {
                Messages.Add(new ChatMessage { Role = ChatRole.User, Text = turn.UserMessage });
            }
            if (!string.IsNullOrWhiteSpace(turn.AssistantResponse))
            {
                Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Text = turn.AssistantResponse });
            }
        }

        _ = _chatClient.DisconnectAsync();
    }

    public async ValueTask DisposeAsync() => await _chatClient.DisposeAsync();

    public event PropertyChangedEventHandler? PropertyChanged;

    void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
