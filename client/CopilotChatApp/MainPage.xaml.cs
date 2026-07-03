using CopilotChatApp.Models;
using CopilotChatApp.ViewModels;
using CopilotChatApp.Views;

namespace CopilotChatApp;

public partial class MainPage : ContentPage
{
    readonly ChatViewModel _viewModel = new();

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
                if (e.NewItems is not null)
                {
                    foreach (ChatMessage msg in e.NewItems)
                    {
                        msg.PropertyChanged += (_, __) => ScrollToLatest();
                    }
                }
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
    /// Scrolls the message list so the newest message is visible. Explicit ScrollTo is used in addition to
    /// ItemsUpdatingScrollMode="KeepLastItemInView" because that property alone isn't always reliable when
    /// items are removed and re-added quickly (e.g. sealing a pending assistant bubble on tool start).
    /// </summary>
    void ScrollToLatest()
    {
        if (_viewModel.Messages.Count == 0) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                MessagesView.ScrollTo(_viewModel.Messages.Count - 1, position: ScrollToPosition.End, animate: false);
            }
            catch
            {
                // ignore - can throw if the view isn't laid out yet
            }
        });
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

    async void OnSessionsClicked(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new SessionsPage(_viewModel.ChatClient, _viewModel.ApplyResumedSession, () => _ = _viewModel.StartNewChatAsync()));
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

