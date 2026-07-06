using System.Collections.ObjectModel;
using CopilotChatApp.Services;

namespace CopilotChatApp.Views;

/// <summary>
/// The app's launch screen (Shell's root page, see AppShell.xaml): lists past Copilot CLI sessions
/// and lets the user resume one or start a new chat. Each choice pushes a fresh <see cref="MainPage"/>
/// onto the navigation stack - this is different from the in-chat "☰ Sessions" flow
/// (<see cref="SessionsPage"/>), which mutates the *already open* MainPage's ChatViewModel in place
/// instead of navigating. That existing flow is left untouched; this page is purely a new entry point.
///
/// Owns its own ChatClientService/connection, separate from any MainPage's. By the time the user
/// taps a session or "New chat" here, iOS's local-network-access permission prompt (if any) has
/// already been resolved via this page's own connection attempt, so the MainPage that gets pushed
/// can safely make its own second connection without racing that prompt.
/// </summary>
public partial class HomePage : ContentPage
{
    readonly ChatClientService _client = new();

    public ObservableCollection<SessionSummary> Sessions { get; } = new();

    public HomePage()
    {
        InitializeComponent();
        BindingContext = this;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RefreshAsync();
    }

    async Task EnsureConnectedAsync()
    {
        if (_client.IsConnected) return;
        await _client.ConnectWithRetryAsync(SettingsService.ServerUrl, await SettingsService.GetAuthTokenAsync());
    }

    async Task RefreshAsync()
    {
        if (!await SettingsService.IsConfiguredAsync())
        {
            NotConfiguredLabel.IsVisible = true;
            Sessions.Clear();
            return;
        }
        NotConfiguredLabel.IsVisible = false;

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
            // Same rationale as SessionsPage: this can legitimately fail while iOS's local-network
            // permission prompt is still up, so offer Retry rather than a dead-end error.
            var retry = await DisplayAlert("Couldn't load sessions", ex.Message, "Retry", "Cancel");
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
            await Navigation.PushAsync(new MainPage(session, turns));
        }
        catch (Exception ex)
        {
            var retry = await DisplayAlert("Error", $"Failed to open session: {ex.Message}", "Retry", "Cancel");
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
        SettingsService.ResetConversation();
        await Navigation.PushAsync(new MainPage());
    }

    async void OnSettingsClicked(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new SettingsPage(_client));
    }
}
