using System.Collections.ObjectModel;
using CopilotChatApp.Services;

namespace CopilotChatApp.Views;

/// <summary>
/// Lists past Copilot CLI sessions that ran in this app's server-side workspace (read from the
/// CLI's own local session-store.db) and lets the user resume one, or start a brand new chat.
/// </summary>
public partial class SessionsPage : ContentPage
{
    readonly ChatClientService _client;
    readonly Action<SessionSummary, List<SessionTurn>> _onResumeSession;
    readonly Action _onNewChat;

    public ObservableCollection<SessionSummary> Sessions { get; } = new();

    /// <summary>
    /// Reuses the ChatViewModel's already-connected client (see ChatViewModel.ChatClient) rather than
    /// opening a second WebSocket - a second connection would re-trigger iOS's local-network-access
    /// permission prompt, and if the user hasn't answered it yet, that second attempt fails immediately
    /// and shows a confusing in-app error dialog racing the system one.
    /// </summary>
    public SessionsPage(ChatClientService client, Action<SessionSummary, List<SessionTurn>> onResumeSession, Action onNewChat)
    {
        InitializeComponent();
        BindingContext = this;
        _client = client;
        _onResumeSession = onResumeSession;
        _onNewChat = onNewChat;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RefreshAsync();
    }

    async Task EnsureConnectedAsync()
    {
        if (_client.IsConnected) return;
        if (!await SettingsService.IsConfiguredAsync())
        {
            await DisplayAlert("Not configured", "Please set the server URL and token in Settings first.", "OK");
            await Navigation.PopAsync();
            return;
        }
        await _client.ConnectWithRetryAsync(SettingsService.ServerUrl, await SettingsService.GetAuthTokenAsync());
    }

    async Task RefreshAsync()
    {
        LoadingIndicator.IsVisible = true;
        LoadingIndicator.IsRunning = true;
        LoadingLabel.IsVisible = true;
        try
        {
            await EnsureConnectedAsync();
            var sessions = await _client.ListSessionsAsync();
            Sessions.Clear();
            foreach (var s in sessions) Sessions.Add(s);
        }
        catch (Exception ex)
        {
            LoadingIndicator.IsVisible = false;
            LoadingIndicator.IsRunning = false;
            LoadingLabel.IsVisible = false;
            // This can legitimately fail while iOS/iPadOS's local-network-access permission prompt
            // (NSLocalNetworkUsageDescription) is still up - our own retry budget can run out before
            // the user notices and answers that system dialog. Offering Retry here (rather than just
            // an OK) lets them try again right after answering it, instead of having to leave and
            // reopen this whole page.
            var retry = await DisplayAlert("Couldn't load sessions", $"{ex.Message}", "Retry", "Cancel");
            if (retry)
            {
                await RefreshAsync();
            }
            return;
        }
        finally
        {
            LoadingIndicator.IsVisible = false;
            LoadingIndicator.IsRunning = false;
            LoadingLabel.IsVisible = false;
        }
    }

    async void OnSessionTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not SessionSummary session) return;
        await OpenSessionAsync(session);
    }

    async Task OpenSessionAsync(SessionSummary session)
    {
        LoadingIndicator.IsVisible = true;
        LoadingIndicator.IsRunning = true;
        try
        {
            await EnsureConnectedAsync();
            var turns = await _client.GetSessionHistoryAsync(session.Id);
            _onResumeSession(session, turns);
            await Navigation.PopAsync();
        }
        catch (Exception ex)
        {
            LoadingIndicator.IsVisible = false;
            LoadingIndicator.IsRunning = false;
            // Same rationale as RefreshAsync's Retry: a fresh connection attempt right after this
            // failure (whether the user's own manual Retry tap here, or the background reconnect
            // loop kicked off inside ConnectWithRetryAsync) tends to succeed within a few seconds -
            // let them retry in place instead of having to back out to Sessions and re-enter.
            var retry = await DisplayAlert("Error", $"Failed to load session: {ex.Message}", "Retry", "Cancel");
            if (retry)
            {
                await OpenSessionAsync(session);
            }
            return;
        }
        finally
        {
            LoadingIndicator.IsVisible = false;
            LoadingIndicator.IsRunning = false;
        }
    }

    async void OnNewChatClicked(object? sender, EventArgs e)
    {
        var confirm = await DisplayAlert("New chat", "Start a brand new conversation?", "Yes", "Cancel");
        if (!confirm) return;
        _onNewChat();
        await Navigation.PopAsync();
    }
}
