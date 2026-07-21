using CopilotChatApp.Models;
using CopilotChatApp.Services;
using CopilotChatApp.ViewModels;
using CopilotChatApp.Views;

namespace CopilotChatApp;

public partial class MainPage : ContentPage
{
    static readonly ChatModelPickerOption ServerDefaultModel = new(null, "Server default (CLI default)");
    readonly ChatViewModel _viewModel = new();
    double _lastLaidOutFontSize = SettingsService.ChatFontSize;
    int _scrollRequestVersion;
    bool _scrollDispatchQueued;

    // True while the newest message should keep following the bottom. Set false as soon as the user
    // scrolls up to read history (so streaming deltas stop yanking them back down), and restored
    // when they scroll back to the bottom or send a new message. Distance-from-bottom below which we
    // still count as "at the bottom" - generous so a partially-clipped last line still follows.
    bool _autoFollowBottom = true;
    bool _modelCatalogLoaded;
    bool _modelCatalogLoading;
    const double BottomFollowThreshold = 64;

    public MainPage()
    {
        InitializeComponent();
        BindingContext = _viewModel;
        SetModelOptions(new[] { ServerDefaultModel }, null);

        // Only a user-driven scroll changes follow mode (see OnMessagesScrolled) - programmatic
        // scrolls always land at the bottom, and content growth doesn't move the offset, so neither
        // flips this off.
        MessagesView.Scrolled += OnMessagesScrolled;

        _viewModel.Messages.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
            {
                // A message the user just sent always re-pins to the bottom, even if they'd scrolled
                // up to read history - they clearly want to see their new turn and its response.
                if (e.NewItems is not null)
                {
                    foreach (ChatMessage msg in e.NewItems)
                    {
                        if (msg.IsUser) _autoFollowBottom = true;
                    }
                }

                if (_autoFollowBottom) ScrollToLatest();

                // Also scroll on every subsequent change to this message (e.g. each streamed delta
                // appended to Text) - but only while still following the bottom, so a user reading
                // back through history isn't repeatedly dragged down as the response grows.
                //
                // IsSearchHighlighted/IsCopied are excluded implicitly (only Text is handled): those
                // are UI-only flags toggled well after the message is finalized (search jumping to a
                // match, or a "Copied" flash), and treating them as "new content streamed in" fought
                // the in-chat search bar.
                if (e.NewItems is not null)
                {
                    foreach (ChatMessage msg in e.NewItems)
                    {
                        msg.PropertyChanged += (_, propArgs) =>
                        {
                            if (propArgs.PropertyName == nameof(ChatMessage.Text) && _autoFollowBottom)
                            {
                                ScrollToLatest();
                            }
                        };
                    }
                }
            }
        };

        // IsSending flips false exactly once, when a turn is fully finalized (or fails). Do one
        // more settle-scroll once the final text has had a chance to lay out.
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.IsSending) && !_viewModel.IsSending && _autoFollowBottom)
            {
                ScrollToLatest();
            }
            if (e.PropertyName == nameof(ChatViewModel.IsSending))
            {
                UpdateModelControlsEnabled();
            }
        };

        // Sending clears the multi-line editor and shows the working indicator, changing the
        // message viewport height after the user bubble has been added. Re-apply the debounced
        // scroll once that layout change reaches the CollectionView; otherwise WinUI can preserve
        // an offset calculated against the old, taller viewport.
        MessagesView.SizeChanged += (_, _) =>
        {
            if (_viewModel.IsSending && _autoFollowBottom)
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
    /// the cell to fit the new text happens slightly later. Throttle those requests: WinUI can visibly
    /// oscillate if ScrollTo runs for every streamed chunk, while a short delayed follow-up after the
    /// final request still catches the last line after its layout has settled.
    /// </summary>
    void ScrollToLatest()
    {
        if (_viewModel.Messages.Count == 0) return;
        ++_scrollRequestVersion;

        if (_scrollDispatchQueued) return;

        _scrollDispatchQueued = true;
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(75), () =>
        {
            _scrollDispatchQueued = false;
            ScrollToEnd();

            var settledRequestVersion = _scrollRequestVersion;
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(250), () =>
            {
                ScrollToEndIfCurrent(settledRequestVersion);
            });
        });
    }

    void ScrollToEndIfCurrent(int requestVersion)
    {
        if (requestVersion == _scrollRequestVersion)
        {
            ScrollToEnd();
        }
    }

    /// <summary>
    /// Updates <see cref="_autoFollowBottom"/> from user-driven scrolling. Content growth during
    /// streaming doesn't move the offset (so it doesn't fire a delta here), and our own programmatic
    /// scrolls always land at the bottom (recomputing to "following"), so only a real user drag away
    /// from the bottom turns following off - and returning to the bottom turns it back on.
    /// </summary>
    void OnMessagesScrolled(object? sender, ItemsViewScrolledEventArgs e)
    {
        // Ignore extent-only notifications (offset unchanged, e.g. a cell growing mid-stream); those
        // must not be mistaken for the user scrolling away from the bottom.
        if (Math.Abs(e.VerticalDelta) < 0.5) return;
        _autoFollowBottom = IsNearBottom(e);
    }

    bool IsNearBottom(ItemsViewScrolledEventArgs e)
    {
#if WINDOWS
        if (TryGetNativeDistanceFromBottom(out var distance))
        {
            return distance <= BottomFollowThreshold;
        }
#endif
        // Cross-platform fallback: treat "the last message is visible" as being at the bottom.
        return e.LastVisibleItemIndex >= _viewModel.Messages.Count - 1;
    }

    void ScrollToEnd()
    {
        if (_viewModel.Messages.Count == 0) return;
#if WINDOWS
        // ScrollTo(index, End) resolves its target offset from the item's realized/measured layout,
        // which WinUI's virtualizing ItemsRepeater hasn't finished when Send simultaneously adds the
        // user bubble, clears the editor, and shows the working indicator (all of which resize the
        // list). That lands the scroll ~half a page short. Driving the native ScrollViewer to its
        // absolute bottom after forcing a layout pass is independent of item realization, so it lands
        // on the true bottom regardless of that mid-flight resize. (iOS/Mac Catalyst's UICollectionView
        // defers scrollToItem to the next layout pass on its own, which is why it never shows this.)
        if (TryScrollNativeToBottom()) return;
#endif
        try
        {
            MessagesView.ScrollTo(_viewModel.Messages.Count - 1, position: ScrollToPosition.End, animate: false);
        }
        catch
        {
            // ignore - can throw if the view isn't laid out yet
        }
    }

#if WINDOWS
    bool TryScrollNativeToBottom()
    {
        try
        {
            if (MessagesView.Handler?.PlatformView is not Microsoft.UI.Xaml.DependencyObject root) return false;
            if (FindDescendant<Microsoft.UI.Xaml.Controls.ScrollViewer>(root) is not { } scrollViewer) return false;

            // Force the pending layout (new bubble height, resized viewport) to settle so
            // ScrollableHeight reflects the just-added content before we jump to it.
            scrollViewer.UpdateLayout();
            scrollViewer.ChangeView(null, scrollViewer.ScrollableHeight, null, disableAnimation: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    bool TryGetNativeDistanceFromBottom(out double distance)
    {
        distance = 0;
        try
        {
            if (MessagesView.Handler?.PlatformView is not Microsoft.UI.Xaml.DependencyObject root) return false;
            if (FindDescendant<Microsoft.UI.Xaml.Controls.ScrollViewer>(root) is not { } scrollViewer) return false;

            distance = scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset;
            return true;
        }
        catch
        {
            return false;
        }
    }

    static T? FindDescendant<T>(Microsoft.UI.Xaml.DependencyObject root) where T : Microsoft.UI.Xaml.DependencyObject
    {
        var childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }
#endif

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
        if (!_modelCatalogLoaded && await SettingsService.IsConfiguredAsync())
        {
            await LoadModelsAsync();
        }

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

    async void OnRefreshModelsClicked(object? sender, EventArgs e) => await LoadModelsAsync();

    void OnChatModelChanged(object? sender, EventArgs e)
    {
        _viewModel.SelectedModelId = (ChatModelPicker.SelectedItem as ChatModelPickerOption)?.ModelId;
    }

    async Task LoadModelsAsync()
    {
        if (_modelCatalogLoading) return;
        _modelCatalogLoading = true;
        UpdateModelControlsEnabled();
        ChatModelStatusLabel.IsVisible = true;
        ChatModelStatusLabel.TextColor = Colors.Gray;
        ChatModelStatusLabel.Text = "Loading models from Copilot SDK...";

        try
        {
            if (!_viewModel.ChatClient.IsConnected)
            {
                await _viewModel.ChatClient.ConnectWithRetryAsync(
                    SettingsService.ServerUrl,
                    await SettingsService.GetAuthTokenAsync());
            }

            var selectedModelId = _viewModel.SelectedModelId;
            var models = await _viewModel.ChatClient.ListModelsAsync();
            var options = models
                .Where(model => string.IsNullOrEmpty(model.Policy) || model.Policy == "enabled")
                .Select(model => new ChatModelPickerOption(model.Id, model.Name))
                .ToList();
            var serverDefaultModel = ServerDefaultModel;
            try
            {
                var serverInfo = await _viewModel.ChatClient.GetServerInfoAsync();
                var configuredModelId = serverInfo.Model == "(default)" ? null : serverInfo.Model;
                var configuredModelName = models.FirstOrDefault(model => model.Id == configuredModelId)?.Name
                    ?? configuredModelId
                    ?? "CLI default";
                serverDefaultModel = new ChatModelPickerOption(null, $"Server default ({configuredModelName})");
            }
            catch
            {
                // Model selection still works when optional server metadata is unavailable.
            }
            options.Insert(0, serverDefaultModel);
            SetModelOptions(options, selectedModelId);
            _modelCatalogLoaded = true;
            ChatModelStatusLabel.IsVisible = false;
        }
        catch (Exception ex)
        {
            if (!_modelCatalogLoaded)
            {
                SetModelOptions(new[] { ServerDefaultModel }, null);
            }
            ChatModelStatusLabel.TextColor = Colors.OrangeRed;
            ChatModelStatusLabel.Text = $"Couldn't load models: {ex.Message}";
        }
        finally
        {
            _modelCatalogLoading = false;
            UpdateModelControlsEnabled();
        }
    }

    void SetModelOptions(IEnumerable<ChatModelPickerOption> options, string? selectedModelId)
    {
        var optionList = options.ToList();
        ChatModelPicker.ItemsSource = optionList;
        ChatModelPicker.SelectedItem = optionList.FirstOrDefault(option => option.ModelId == selectedModelId)
            ?? optionList[0];
        _viewModel.SelectedModelId = (ChatModelPicker.SelectedItem as ChatModelPickerOption)?.ModelId;
    }

    void UpdateModelControlsEnabled()
    {
        var enabled = !_modelCatalogLoading && !_viewModel.IsSending;
        ChatModelPicker.IsEnabled = enabled;
        RefreshModelsButton.IsEnabled = enabled;
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
    /// directly on iOS/Mac Catalyst (both are UIKit under the hood), and WinRT's DataTransfer
    /// clipboard API on Windows. On iOS, reading a pasteboard populated by another app may trigger
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
#elif WINDOWS
        try
        {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (!content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap))
            {
                await DisplayAlert("No image", "The clipboard doesn't contain an image.", "OK");
                return;
            }

            var bitmapReference = await content.GetBitmapAsync();
            using var sourceStream = await bitmapReference.OpenReadAsync();
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(sourceStream);
            using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
            using var pngStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,
                pngStream);
            encoder.SetSoftwareBitmap(softwareBitmap);
            await encoder.FlushAsync();

            if (pngStream.Size > int.MaxValue)
            {
                await DisplayAlert("Image too large", "The clipboard image is too large to attach.", "OK");
                return;
            }

            pngStream.Seek(0);
            var bytes = new byte[(int)pngStream.Size];
            using var reader = new Windows.Storage.Streams.DataReader(pngStream);
            await reader.LoadAsync((uint)pngStream.Size);
            reader.ReadBytes(bytes);
            _viewModel.AddPastedImage(bytes, "image/png");
        }
        catch (Exception ex)
        {
            await DisplayAlert("Paste failed", $"Couldn't read the clipboard image: {ex.Message}", "OK");
        }
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

public sealed record ChatModelPickerOption(string? ModelId, string DisplayName);

