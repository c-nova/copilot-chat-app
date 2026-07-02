using System.Collections.ObjectModel;
using System.Text;
using CopilotChatApp.Models;
using CopilotChatApp.Services;
using CopilotChatApp.Views;

namespace CopilotChatApp;

public partial class MainPage : ContentPage
{
    readonly ChatClientService _chatClient = new();
    ChatMessage? _pendingAssistantMessage;
    readonly StringBuilder _pendingAssistantText = new();
    readonly Dictionary<string, Queue<ChatMessage>> _pendingToolMessages = new();
    bool _sending;

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    public MainPage()
    {
        InitializeComponent();
        BindingContext = this;

        _chatClient.OnDelta += text => MainThread.BeginInvokeOnMainThread(() => AppendAssistantDelta(text));
        _chatClient.OnFinal += text => MainThread.BeginInvokeOnMainThread(() => FinalizeAssistantMessage(text));
        _chatClient.OnError += message => MainThread.BeginInvokeOnMainThread(() => ShowError(message));
        _chatClient.OnToolEvent += args => MainThread.BeginInvokeOnMainThread(() => HandleToolEvent(args));
        Messages.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
            {
                ScrollToLatest();
            }
        };

        SetUpSubmitShortcut();
    }

    /// <summary>
    /// Scrolls the message list so the newest message is visible. Explicit ScrollTo is used in addition to
    /// ItemsUpdatingScrollMode="KeepLastItemInView" because that property alone isn't always reliable when
    /// items are removed and re-added quickly (e.g. sealing a pending assistant bubble on tool start).
    /// </summary>
    void ScrollToLatest()
    {
        if (Messages.Count == 0) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                MessagesView.ScrollTo(Messages.Count - 1, position: ScrollToPosition.End, animate: false);
            }
            catch
            {
                // ignore - can throw if the view isn't laid out yet
            }
        });
    }

    void SetUpSubmitShortcut()
    {
        // Ctrl+Enter submits the message. Implemented via the Windows platform view since MAUI
        // has no cross-platform key-accelerator API for Editor; Mac/iOS keep default Editor behavior
        // (multi-line input, tap Send to submit).
#if WINDOWS
        InputEditor.HandlerChanged += (_, _) =>
        {
            if (InputEditor.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBox textBox)
            {
                textBox.PreviewKeyDown += async (_, e) =>
                {
                    var ctrlDown = Microsoft.UI.Input.InputKeyboardSource
                        .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                        .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                    if (ctrlDown && e.Key == Windows.System.VirtualKey.Enter)
                    {
                        e.Handled = true;
                        await SendCurrentInputAsync();
                    }
                };
            }
        };
#endif
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (!SettingsService.IsConfigured)
        {
            Messages.Add(new ChatMessage
            {
                Role = ChatRole.System,
                Text = "Server not configured yet. Tap Settings to enter the server URL and token."
            });
        }
    }

    async void OnSendClicked(object? sender, EventArgs e) => await SendCurrentInputAsync();

    async Task SendCurrentInputAsync()
    {
        var text = InputEditor.Text?.Trim();
        if (string.IsNullOrEmpty(text) || _sending) return;

        if (!SettingsService.IsConfigured)
        {
            await DisplayAlert("Not configured", "Please set the server URL and token in Settings first.", "OK");
            return;
        }

        _sending = true;
        SendButton.IsEnabled = false;
        InputEditor.Text = string.Empty;

        Messages.Add(new ChatMessage { Role = ChatRole.User, Text = text });

        try
        {
            if (!_chatClient.IsConnected)
            {
                await _chatClient.ConnectAsync(SettingsService.ServerUrl, SettingsService.AuthToken);
            }
            await _chatClient.SendChatAsync(SettingsService.ConversationId, text);
        }
        catch (Exception ex)
        {
            ShowError($"Connection failed: {ex.Message}");
            _sending = false;
            SendButton.IsEnabled = true;
        }
    }

    void AppendAssistantDelta(string delta)
    {
        if (_pendingAssistantMessage is null)
        {
            _pendingAssistantText.Clear();
            _pendingAssistantMessage = new ChatMessage { Role = ChatRole.Assistant, Text = string.Empty };
            Messages.Add(_pendingAssistantMessage);
        }
        _pendingAssistantText.Append(delta);
        _pendingAssistantMessage.Text = _pendingAssistantText.ToString();
        ScrollToLatest();
    }

    void FinalizeAssistantMessage(string finalText)
    {
        if (_pendingAssistantMessage is null)
        {
            Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Text = finalText });
        }
        else
        {
            _pendingAssistantMessage.Text = finalText;
        }
        _pendingAssistantMessage = null;
        _sending = false;
        SendButton.IsEnabled = true;
    }

    void ShowError(string message)
    {
        Messages.Add(new ChatMessage { Role = ChatRole.System, Text = $"⚠️ {message}" });
        _pendingAssistantMessage = null;
        _sending = false;
        SendButton.IsEnabled = true;
    }

    void HandleToolEvent(ToolEventArgs args)
    {
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

    async void OnToolTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Element el || el.BindingContext is not ChatMessage { IsTool: true } msg) return;
        if (string.IsNullOrEmpty(msg.ToolDetail)) return;
        await DisplayAlert("Tool details", msg.ToolDetail, "OK");
    }

    async void OnMessageTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Element el || el.BindingContext is not ChatMessage { IsTool: false } msg) return;
        if (string.IsNullOrEmpty(msg.Text)) return;

        await Clipboard.SetTextAsync(msg.Text);

        msg.IsCopied = true;
        await Task.Delay(1500);
        msg.IsCopied = false;
    }

    async void OnSelectTextTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Element el || el.BindingContext is not ChatMessage { IsTool: false } msg) return;
        if (string.IsNullOrEmpty(msg.Text)) return;

        await Navigation.PushAsync(new Views.SelectableTextPage(msg.Text));
    }

    async void OnNewChatClicked(object? sender, EventArgs e)
    {
        var confirm = await DisplayAlert("New chat", "Start a new conversation? History will be cleared locally and on the server session.", "Yes", "Cancel");
        if (!confirm) return;
        await StartNewChatAsync();
    }

    async Task StartNewChatAsync()
    {
        SettingsService.ResetConversation();
        Messages.Clear();
        await _chatClient.DisconnectAsync();
    }

    async void OnSessionsClicked(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new SessionsPage(ApplyResumedSession, () => _ = StartNewChatAsync()));
    }

    void ApplyResumedSession(SessionSummary session, List<SessionTurn> turns)
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

    async void OnSettingsClicked(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new SettingsPage());
    }
}
