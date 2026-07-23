using CopilotChatApp.Models;
using CopilotChatApp.Services;
using CopilotChatApp.ViewModels;

namespace CopilotChatApp.Views;

/// <summary>
/// PBI-025's orchestration screen: the main session's chat (top, human-editable) plus a
/// polling-refreshed list of child session logs (bottom, read-only - see PBI.md's PBI-025 design
/// notes for why children are never directly typed into by the human). Reachable from HomePage's
/// per-session "⋯" menu ("🎼 Orchestratorで開く").
/// </summary>
public partial class OrchestratorPage : ContentPage
{
    readonly OrchestratorViewModel _viewModel;
    int _mainScrollRequestVersion;
    int _childrenScrollRequestVersion;
    bool _mainScrollDispatchQueued;
    bool _childrenScrollDispatchQueued;
    bool _mainAutoFollowBottom = true;
    bool _childrenAutoFollowBottom = true;
    const double BottomFollowThreshold = 64;

    /// <summary>Opens the Orchestrator screen for an already-existing (resumed) main session on <paramref name="mainProfileId"/>.</summary>
    public OrchestratorPage(SessionSummary mainSession, List<SessionTurn> mainTurns, string mainProfileId)
    {
        InitializeComponent();
        _viewModel = new OrchestratorViewModel(mainSession, mainTurns, mainProfileId);
        BindingContext = _viewModel;
        SetUpAutoScroll();
#if WINDOWS
        SetUpSubmitShortcut();
#elif MACCATALYST
        bool submitShortcutInstalled = false;
        Loaded += (_, _) =>
        {
            if (submitShortcutInstalled) return;
            submitShortcutInstalled = true;
            SetUpSubmitShortcut();
        };
#endif
    }

    /// <summary>Opens the Orchestrator screen for a brand-new main session rooted at <paramref name="cwd"/> on <paramref name="mainProfileId"/> (the "New Chat" flow's Orchestrator option - see NewChatPage).</summary>
    public OrchestratorPage(string? cwd, string mainProfileId)
    {
        InitializeComponent();
        _viewModel = new OrchestratorViewModel(cwd, mainProfileId);
        BindingContext = _viewModel;
        SetUpAutoScroll();
#if WINDOWS
        SetUpSubmitShortcut();
#elif MACCATALYST
        bool submitShortcutInstalled = false;
        Loaded += (_, _) =>
        {
            if (submitShortcutInstalled) return;
            submitShortcutInstalled = true;
            SetUpSubmitShortcut();
        };
#endif
    }

    void SetUpAutoScroll()
    {
        MainMessagesView.Scrolled += OnMainMessagesScrolled;
        ChildrenScrollView.Scrolled += OnChildrenScrolled;

        foreach (var message in _viewModel.MainChat.Messages)
        {
            SubscribeToMainMessage(message);
        }
        _viewModel.MainChat.Messages.CollectionChanged += (_, e) =>
        {
            if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add) return;
            if (e.NewItems is not null)
            {
                foreach (ChatMessage message in e.NewItems)
                {
                    if (message.IsUser) _mainAutoFollowBottom = true;
                    SubscribeToMainMessage(message);
                }
            }
            if (_mainAutoFollowBottom) ScrollMainToLatest();
        };

        _viewModel.MainChat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.IsSending)
                && !_viewModel.MainChat.IsSending
                && _mainAutoFollowBottom)
            {
                ScrollMainToLatest();
            }
        };
        MainMessagesView.SizeChanged += (_, _) =>
        {
            if (_viewModel.MainChat.IsSending && _mainAutoFollowBottom)
            {
                ScrollMainToLatest();
            }
        };

        foreach (var pane in _viewModel.Children)
        {
            SubscribeToChildPane(pane);
        }
        _viewModel.Children.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
            {
                foreach (ChildSessionPaneViewModel pane in e.NewItems)
                {
                    SubscribeToChildPane(pane);
                }
            }
            if (_childrenAutoFollowBottom) ScrollChildrenToLatest();
        };
    }

    void SubscribeToMainMessage(ChatMessage message)
    {
        message.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatMessage.Text) && _mainAutoFollowBottom)
            {
                ScrollMainToLatest();
            }
        };
    }

    void SubscribeToChildPane(ChildSessionPaneViewModel pane)
    {
        pane.Messages.CollectionChanged += (_, _) =>
        {
            if (_childrenAutoFollowBottom) ScrollChildrenToLatest();
        };
        pane.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChildSessionPaneViewModel.IsBusy) && _childrenAutoFollowBottom)
            {
                ScrollChildrenToLatest();
            }
        };
    }

    void OnMainMessagesScrolled(object? sender, ItemsViewScrolledEventArgs e)
    {
        if (Math.Abs(e.VerticalDelta) < 0.5) return;
#if WINDOWS
        if (TryGetNativeDistanceFromBottom(MainMessagesView, out var distance))
        {
            _mainAutoFollowBottom = distance <= BottomFollowThreshold;
            return;
        }
#endif
        _mainAutoFollowBottom = e.LastVisibleItemIndex >= _viewModel.MainChat.Messages.Count - 1;
    }

    void OnChildrenScrolled(object? sender, ScrolledEventArgs e)
    {
#if WINDOWS
        if (TryGetNativeDistanceFromBottom(ChildrenScrollView, out var distance))
        {
            _childrenAutoFollowBottom = distance <= BottomFollowThreshold;
            return;
        }
#endif
        var distanceFromBottom = ChildrenScrollView.ContentSize.Height
            - ChildrenScrollView.Height
            - e.ScrollY;
        _childrenAutoFollowBottom = distanceFromBottom <= BottomFollowThreshold;
    }

    void ScrollMainToLatest()
    {
        if (_viewModel.MainChat.Messages.Count == 0) return;
        ++_mainScrollRequestVersion;
        if (_mainScrollDispatchQueued) return;
        _mainScrollDispatchQueued = true;
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(75), () =>
        {
            _mainScrollDispatchQueued = false;
            ScrollMainToEnd();
            var settledRequestVersion = _mainScrollRequestVersion;
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(250), () =>
            {
                if (settledRequestVersion == _mainScrollRequestVersion) ScrollMainToEnd();
            });
        });
    }

    void ScrollChildrenToLatest()
    {
        ++_childrenScrollRequestVersion;
        if (_childrenScrollDispatchQueued) return;
        _childrenScrollDispatchQueued = true;
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(75), () =>
        {
            _childrenScrollDispatchQueued = false;
            ScrollChildrenToEnd();
            var settledRequestVersion = _childrenScrollRequestVersion;
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(250), () =>
            {
                if (settledRequestVersion == _childrenScrollRequestVersion) ScrollChildrenToEnd();
            });
        });
    }

    void ScrollMainToEnd()
    {
        if (_viewModel.MainChat.Messages.Count == 0) return;
#if WINDOWS
        if (TryScrollNativeToBottom(MainMessagesView)) return;
#endif
        try
        {
            MainMessagesView.ScrollTo(
                _viewModel.MainChat.Messages.Count - 1,
                position: ScrollToPosition.End,
                animate: false);
        }
        catch
        {
            // The view may not be laid out yet.
        }
    }

    void ScrollChildrenToEnd()
    {
#if WINDOWS
        if (TryScrollNativeToBottom(ChildrenScrollView)) return;
#endif
        _ = ChildrenScrollView.ScrollToAsync(0, ChildrenScrollView.ContentSize.Height, false);
    }

#if WINDOWS
    static bool TryScrollNativeToBottom(VisualElement view)
    {
        try
        {
            if (view.Handler?.PlatformView is not Microsoft.UI.Xaml.DependencyObject root) return false;
            var scrollViewer = root as Microsoft.UI.Xaml.Controls.ScrollViewer
                ?? FindDescendant<Microsoft.UI.Xaml.Controls.ScrollViewer>(root);
            if (scrollViewer is null) return false;
            scrollViewer.UpdateLayout();
            scrollViewer.ChangeView(null, scrollViewer.ScrollableHeight, null, disableAnimation: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    static bool TryGetNativeDistanceFromBottom(VisualElement view, out double distance)
    {
        distance = 0;
        try
        {
            if (view.Handler?.PlatformView is not Microsoft.UI.Xaml.DependencyObject root) return false;
            var scrollViewer = root as Microsoft.UI.Xaml.Controls.ScrollViewer
                ?? FindDescendant<Microsoft.UI.Xaml.Controls.ScrollViewer>(root);
            if (scrollViewer is null) return false;
            distance = scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset;
            return true;
        }
        catch
        {
            return false;
        }
    }

    static T? FindDescendant<T>(Microsoft.UI.Xaml.DependencyObject root)
        where T : Microsoft.UI.Xaml.DependencyObject
    {
        var childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }
#endif

    /// <summary>Ctrl+Enter (Windows) / Cmd+Enter (Mac Catalyst) submits the main session's message -
    /// mirrors MainPage.SetUpSubmitShortcut so the shortcut behaves identically on both chat screens.</summary>
    void SetUpSubmitShortcut()
    {
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
                        _viewModel.MainChat.SendCommand.Execute(null);
                    }
                };
            }
        };
#elif MACCATALYST
        var sendMenuItem = new MenuFlyoutItem
        {
            Text = "Send",
            Command = _viewModel.MainChat.SendCommand
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

    async void OnSelectTextTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Element element || element.BindingContext is not ChatMessage message) return;
        if (string.IsNullOrEmpty(message.Text)) return;

        await Navigation.PushAsync(new SelectableTextPage(message.Text));
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.MainChat.InitializeAsync();
        await _viewModel.MarkAsOrchestratorMainAsync();
        await _viewModel.RefreshChildrenAsync();
        ScrollMainToLatest();
        ScrollChildrenToLatest();
        _viewModel.StartPolling();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _ = _viewModel.DisposeAsync();
    }

    /// <summary>Asks which configured server profile to target (skipped if only one is configured -
    /// defaults straight to the main session's own profile), returning null if the user cancels.
    /// PBI-026: children aren't limited to the main session's own machine.</summary>
    async Task<ServerProfile?> PickTargetProfileAsync()
    {
        var profiles = SettingsService.GetProfiles().Where(p => !string.IsNullOrWhiteSpace(p.Url)).ToList();
        if (profiles.Count <= 1) return profiles.FirstOrDefault(p => p.Id == _viewModel.MainProfileId) ?? profiles.FirstOrDefault();

        var labels = profiles.Select(p => p.Id == _viewModel.MainProfileId ? $"{p.Name} (このセッション)" : p.Name).ToArray();
        var pick = await DisplayActionSheet("どのサーバーに作成しますか?", "キャンセル", null, labels);
        if (pick is null || pick == "キャンセル") return null;

        var index = Array.IndexOf(labels, pick);
        return index >= 0 ? profiles[index] : null;
    }

    async void OnAddChildClicked(object? sender, EventArgs e)
    {
        var choice = await DisplayActionSheet("子セッションを追加", "キャンセル", null, "新規セッションを作成", "既存セッションをアタッチ");
        if (choice is null || choice == "キャンセル") return;

        try
        {
            var targetProfile = await PickTargetProfileAsync();
            if (targetProfile is null) return;

            if (choice == "新規セッションを作成")
            {
                var message = await DisplayPromptAsync(
                    "新規セッションを作成",
                    "子セッションへの最初の指示を入力してください",
                    accept: "作成", cancel: "キャンセル");
                if (string.IsNullOrWhiteSpace(message)) return;

                await _viewModel.SpawnNewChildAsync(targetProfile.Id, message.Trim(), cwd: null);
            }
            else if (choice == "既存セッションをアタッチ")
            {
                var allSessions = await _viewModel.ListSessionsOnProfileAsync(targetProfile.Id);
                var existingChildIds = _viewModel.Children.Select(c => c.SessionId).ToHashSet();
                var candidates = allSessions
                    .Where(s => s.Id != _viewModel.MainSessionId && !existingChildIds.Contains(s.Id))
                    .ToList();

                if (candidates.Count == 0)
                {
                    await DisplayAlert("アタッチできるセッションがありません", "他に既存のセッションが見つかりませんでした。", "OK");
                    return;
                }

                var labels = candidates.Select(s => $"{s.DisplayTitle} ({s.Id.Substring(0, 8)})").ToArray();
                var pick = await DisplayActionSheet("アタッチするセッションを選択", "キャンセル", null, labels);
                if (pick is null || pick == "キャンセル") return;

                var index = Array.IndexOf(labels, pick);
                if (index < 0) return;

                await _viewModel.AttachExistingChildAsync(targetProfile.Id, candidates[index].Id);
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("エラー", $"子セッションの追加に失敗しました: {ex.Message}", "OK");
        }
    }
}
