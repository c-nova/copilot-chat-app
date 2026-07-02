using System.Collections.ObjectModel;
using CopilotChatApp.Services;

namespace CopilotChatApp.Views;

/// <summary>
/// Lists past Copilot CLI sessions that ran in this app's server-side workspace (read from the
/// CLI's own local session-store.db) and lets the user resume one, or start a brand new chat.
/// </summary>
public partial class SessionsPage : ContentPage
{
    readonly ChatClientService _client = new();
    readonly Action<SessionSummary, List<SessionTurn>> _onResumeSession;
    readonly Action _onNewChat;
    bool _connected;

    public ObservableCollection<SessionSummary> Sessions { get; } = new();

    public SessionsPage(Action<SessionSummary, List<SessionTurn>> onResumeSession, Action onNewChat)
    {
        InitializeComponent();
        BindingContext = this;
        _onResumeSession = onResumeSession;
        _onNewChat = onNewChat;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RefreshAsync();
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        await _client.DisconnectAsync();
        _connected = false;
    }

    async Task EnsureConnectedAsync()
    {
        if (_connected) return;
        if (!SettingsService.IsConfigured)
        {
            await DisplayAlert("Not configured", "Please set the server URL and token in Settings first.", "OK");
            await Navigation.PopAsync();
            return;
        }
        await _client.ConnectAsync(SettingsService.ServerUrl, SettingsService.AuthToken);
        _connected = true;
    }

    async Task RefreshAsync()
    {
        LoadingIndicator.IsVisible = true;
        LoadingIndicator.IsRunning = true;
        try
        {
            await EnsureConnectedAsync();
            var sessions = await _client.ListSessionsAsync();
            Sessions.Clear();
            foreach (var s in sessions) Sessions.Add(s);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Failed to load sessions: {ex.Message}", "OK");
        }
        finally
        {
            LoadingIndicator.IsVisible = false;
            LoadingIndicator.IsRunning = false;
        }
    }

    async void OnSessionTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not SessionSummary session) return;
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
            await DisplayAlert("Error", $"Failed to load session: {ex.Message}", "OK");
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
