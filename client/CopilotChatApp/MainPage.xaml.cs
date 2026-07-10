using CopilotChatApp.Models;
using CopilotChatApp.Services;
using CopilotChatApp.ViewModels;
using CopilotChatApp.Views;

namespace CopilotChatApp;

public partial class MainPage : ContentPage
{
    readonly ChatViewModel _viewModel = new();
    double _lastLaidOutFontSize = SettingsService.ChatFontSize;
    int _scrollRequestVersion;
    bool _immediateScrollQueued;

    public MainPage()
    {
        InitializeComponent();
        BindingContext = _viewModel;

        _viewModel.Messages.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
            {
                ScrollToLatest();
                // Also scroll on every subsequent change to this message (e.g. each streamed delta
                // appended to Text, or IsRunning flipping when a tool call completes) - otherwise the
                // view only auto-scrolls once per new bubble and falls behind while a long streamed
                // response or tool call is still growing.
                //
                // IsSearchHighlighted/IsCopied are excluded: those are UI-only flags toggled well
                // after the message is finalized (search jumping to a match, or a "Copied" flash), and
                // treating them the same as "new content just streamed in" fought the in-chat search
                // bar - jumping to a match (setting IsSearchHighlighted) immediately re-scrolled all
                // the way back to the bottom instead of staying at the match.
                if (e.NewItems is not null)
                {
                    foreach (ChatMessage msg in e.NewItems)
                    {
                        msg.PropertyChanged += (_, propArgs) =>
                        {
                            if (propArgs.PropertyName == nameof(ChatMessage.Text))
                            {
                                ScrollToLatest();
                            }
                        };
                    }
                }
            }
        };

        // IsWaitingForResponse flips false exactly once, the instant a turn is fully finalized
        // (see ChatViewModel.FinalizeAssistantMessage) - a more reliable "the content is done
        // growing" signal than reacting to individual PropertyChanged deltas, which can't tell
        // "another delta is coming" from "this was the last one". Do one more settle-scroll here
        // once the finalized text has had a chance to actually lay out.
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.IsWaitingForResponse) && !_viewModel.IsWaitingForResponse)
            {
                ScrollToLatest();
            }
        };

#if WINDOWS
        SetUpSubmitShortcut();
#elif MACCATALYST
        // On Mac Catalyst, MenuBarItems added before the page is attached to its native window
        // don't make it into the system menu bar (the menu bar is built once, early). Loaded fires
        // once the page is actually in the visual tree, which is late enough for the menu bar item
        // to show up. Guard against Loaded firing more than once (e.g. re-navigation).
        bool submitShortcutInstalled = false;
        Loaded += (_, _) =>
        {
            if (submitShortcutInstalled) return;
            submitShortcutInstalled = true;
            SetUpSubmitShortcut();
        };
#endif
    }

    /// <summary>
    /// Opens this page already showing a resumed session's history (used when navigating here from
    /// the Home/Sessions launch screen). Applies the resumed messages synchronously so they're in
    /// place before the first frame renders - <see cref="ChatViewModel.InitializeAsync"/> (called from
    /// OnAppearing) only connects and never clears Messages, so it won't stomp on this.
    /// </summary>
    public MainPage(SessionSummary session, List<SessionTurn> turns) : this()
    {
        _viewModel.ApplyResumedSession(session, turns);
    }

    /// <summary>Opens this page for a brand-new session rooted at `cwd` (used by the New Chat folder picker). The cwd is only sent on this session's very first turn.</summary>
    public MainPage(string cwd) : this()
    {
        _viewModel.SetPendingCwd(cwd);
    }

    /// <summary>
    /// Scrolls the message list so the newest message is visible. Explicit ScrollTo is used in addition to
    /// ItemsUpdatingScrollMode="KeepLastItemInView" because that property alone isn't always reliable when
    /// items are removed and re-added quickly (e.g. sealing a pending assistant bubble on tool start).
    ///
    /// ScrollTo computes its target offset from the item's *current* measured height. During streaming,
    /// PropertyChanged fires the instant a text delta is appended, but the native layout pass that grows
    /// the cell to fit the new text happens slightly later - so the immediate ScrollTo can land a few
    /// points short of the true bottom. Because there's a PropertyChanged event for every subsequent
    /// delta, this self-corrects for all but the *last* chunk of a response, whose trailing sliver then
    /// has no follow-up scroll to fix it and appears clipped by the viewport. A short follow-up ScrollTo
    /// after the layout pass has had a chance to settle closes that gap.
    /// </summary>
    void ScrollToLatest()
    {
        if (_viewModel.Messages.Count == 0) return;
        var requestVersion = ++_scrollRequestVersion;

        if (!_immediateScrollQueued)
        {
            _immediateScrollQueued = true;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _immediateScrollQueued = false;
                ScrollToEnd();
            });
        }

        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(150), () => ScrollToEndIfCurrent(requestVersion));
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(400), () => ScrollToEndIfCurrent(requestVersion));
    }

    void ScrollToEndIfCurrent(int requestVersion)
    {
        if (requestVersion == _scrollRequestVersion)
        {
            ScrollToEnd();
        }
    }

    void ScrollToEnd()
    {
        if (_viewModel.Messages.Count == 0) return;
        try
        {
            MessagesView.ScrollTo(_viewModel.Messages.Count - 1, position: ScrollToPosition.End, animate: false);
        }
        catch
        {
            // ignore - can throw if the view isn't laid out yet
        }
    }

    void SetUpSubmitShortcut()
    {
        // Ctrl+Enter (Windows) / Cmd+Enter (Mac Catalyst) submits the message, since Editor is
        // multi-line and a plain Enter just inserts a newline.
#if WINDOWS
        InputEditor.HandlerChanged += (_, _) =>
        {
            if (InputEditor.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBox textBox)
            {
                textBox.PreviewKeyDown += (_, e) =>
                {
                    var ctrlDown = Microsoft.UI.Input.InputKeyboardSource
                        .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                        .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                    if (ctrlDown && e.Key == Windows.System.VirtualKey.Enter)
                    {
                        e.Handled = true;
                        _viewModel.SendCommand.Execute(null);
                    }
                };
            }
        };
#elif MACCATALYST
        // MAUI's KeyboardAccelerator API only attaches to MenuFlyoutItem/menu bar items (there's no
        // cross-platform key-accelerator API for arbitrary controls like Editor), so Cmd+Enter is
        // wired up as a hidden menu bar command instead. This adds a small "Message" menu to the
        // Mac menu bar with a single "Send" item - macOS users are used to scanning the menu bar for
        // available shortcuts, so this is a natural (if slightly unusual for this app) place for it.
        var sendMenuItem = new MenuFlyoutItem
        {
            Text = "Send",
            Command = _viewModel.SendCommand
        };
        sendMenuItem.KeyboardAccelerators.Add(new KeyboardAccelerator
        {
            Modifiers = KeyboardAcceleratorModifiers.Cmd,
            Key = "\r"
        });
        var messageMenu = new MenuBarItem { Text = "Message" };
        messageMenu.Add(sendMenuItem);
        MenuBarItems.Add(messageMenu);
#endif
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.InitializeAsync();

        // Changing the chat font size in Settings updates {DynamicResource ChatFontSize} live, but
        // CollectionView's self-sizing cells on iOS/Mac Catalyst don't retroactively re-measure
        // *already-rendered* cells just because a descendant's FontSize changed via a resource -
        // the cell keeps whatever height was cached before the change. Coming back from Settings
        // with a different font size than we last laid out with, force every visible cell to be
        // torn down and rebuilt from scratch by rebinding ItemsSource (this is the same clipping
        // symptom as the streaming/last-line bug, just triggered by a font size change instead of
        // stale streaming layout).
        var currentFontSize = SettingsService.ChatFontSize;
        if (currentFontSize != _lastLaidOutFontSize)
        {
            _lastLaidOutFontSize = currentFontSize;
            var messages = _viewModel.Messages;
            MessagesView.ItemsSource = null;
            MessagesView.ItemsSource = messages;
            ScrollToLatest();
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
        await _viewModel.StartNewChatAsync();
    }

    // --- In-chat "find" bar (search within the currently open conversation) ---
    // Pure client-side substring match over the already-loaded Messages list - unlike HomePage's
    // cross-session search, there's no server round-trip to make here, the whole conversation is
    // already in memory.
    readonly List<int> _searchMatchIndices = new();
    int _searchMatchPos = -1;
    ChatMessage? _searchHighlightedMessage;

    void OnToggleSearchClicked(object? sender, EventArgs e)
    {
        var showing = !SearchBarRow.IsVisible;
        SearchBarRow.IsVisible = showing;
        if (showing)
        {
            ChatSearchBar.Focus();
        }
        else
        {
            ChatSearchBar.Text = string.Empty; // triggers OnChatSearchTextChanged, which clears the highlight/matches
        }
    }

    void OnChatSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        ClearSearchHighlight();
        _searchMatchIndices.Clear();
        _searchMatchPos = -1;

        var query = e.NewTextValue?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(query))
        {
            for (var i = 0; i < _viewModel.Messages.Count; i++)
            {
                if (_viewModel.Messages[i].Text.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    _searchMatchIndices.Add(i);
                }
            }
            if (_searchMatchIndices.Count > 0)
            {
                // Start from the most recent match - in a long conversation, the newest occurrence of
                // a search term is usually the one the user actually wants (they just said it, or the
                // assistant just referenced it), and Prev/Next can step backward from there.
                _searchMatchPos = _searchMatchIndices.Count - 1;
                JumpToCurrentSearchMatch();
            }
        }
        UpdateSearchCounterLabel();
    }

    void OnSearchPrevClicked(object? sender, EventArgs e) => StepSearchMatch(-1);

    void OnSearchNextClicked(object? sender, EventArgs e) => StepSearchMatch(1);

    void StepSearchMatch(int direction)
    {
        if (_searchMatchIndices.Count == 0) return;
        _searchMatchPos = ((_searchMatchPos + direction) % _searchMatchIndices.Count + _searchMatchIndices.Count) % _searchMatchIndices.Count;
        JumpToCurrentSearchMatch();
        UpdateSearchCounterLabel();
    }

    void JumpToCurrentSearchMatch()
    {
        ClearSearchHighlight();
        if (_searchMatchPos < 0 || _searchMatchPos >= _searchMatchIndices.Count) return;

        var index = _searchMatchIndices[_searchMatchPos];
        var message = _viewModel.Messages[index];
        message.IsSearchHighlighted = true;
        _searchHighlightedMessage = message;
        try
        {
            MessagesView.ScrollTo(index, position: ScrollToPosition.Center, animate: true);
        }
        catch
        {
            // ignore - can throw if the view isn't laid out yet
        }
    }

    void ClearSearchHighlight()
    {
        if (_searchHighlightedMessage is not null)
        {
            _searchHighlightedMessage.IsSearchHighlighted = false;
            _searchHighlightedMessage = null;
        }
    }

    void UpdateSearchCounterLabel()
    {
        SearchCounterLabel.Text = _searchMatchIndices.Count == 0
            ? "0/0"
            : $"{_searchMatchPos + 1}/{_searchMatchIndices.Count}";
    }

    async void OnSettingsClicked(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new SettingsPage(_viewModel.ChatClient));
    }

    /// <summary>
    /// Reads an image off the system clipboard and stages it as a pending attachment (PBI-019).
    /// MAUI's cross-platform Clipboard API only supports text, so this drops down to UIPasteboard
    /// directly on iOS/Mac Catalyst (both are UIKit under the hood); other platforms don't have an
    /// image-clipboard path yet. On iOS, reading a pasteboard populated by another app may trigger
    /// the system's "Allow Paste" permission prompt - that's expected, not an error.
    /// </summary>
    async void OnPasteImageClicked(object? sender, EventArgs e)
    {
#if MACCATALYST || IOS
        var pasteboard = UIKit.UIPasteboard.General;
        if (!pasteboard.HasImages || pasteboard.Image is not UIKit.UIImage image)
        {
            await DisplayAlert("No image", "The clipboard doesn't contain an image.", "OK");
            return;
        }
        using var pngData = image.AsPNG();
        if (pngData is null) return;

        var bytes = new byte[pngData.Length];
        System.Runtime.InteropServices.Marshal.Copy(pngData.Bytes, bytes, 0, (int)pngData.Length);
        _viewModel.AddPastedImage(bytes, "image/png");
#else
        await DisplayAlert("Not supported", "Image paste isn't available on this platform yet.", "OK");
#endif
    }

    void OnRemoveAttachmentTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Element el || el.BindingContext is not Models.PendingAttachment attachment) return;
        _viewModel.RemovePendingAttachment(attachment);
    }
}

