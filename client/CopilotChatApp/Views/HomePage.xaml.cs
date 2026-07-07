using System.Collections.ObjectModel;
using System.Linq;
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

    /// <summary>Every session fetched from the server, regardless of archived status - the source list
    /// that <see cref="Sessions"/> is filtered from (see <see cref="ApplyArchiveFilter"/>). Kept
    /// separately so toggling "show archived" doesn't need another server round-trip.</summary>
    readonly List<SessionSummary> _allSessions = new();
    bool _showArchived;

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
            ServerInfoBorder.IsVisible = false;
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
            _allSessions.Clear();
            _allSessions.AddRange(sessions);
            ApplyArchiveFilter();
            await RefreshServerInfoAsync();
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

    /// <summary>
    /// Best-effort: shows a small OS/hostname/CLI-version/model chip at the top of the screen. Never
    /// blocks or fails the rest of the screen - an older server that doesn't understand "server:info"
    /// yet, or any other hiccup, should just leave the chip hidden.
    /// </summary>
    async Task RefreshServerInfoAsync()
    {
        try
        {
            var info = await _client.GetServerInfoAsync();
            var modelText = string.IsNullOrWhiteSpace(info.Model) || info.Model == "(default)" ? "default model" : info.Model;
            ServerInfoLabel.Text = $"{info.OsGlyph} {info.Hostname} ・ Copilot CLI {info.CopilotCliVersion} ・ {modelText}";
            ServerInfoBorder.IsVisible = true;
        }
        catch
        {
            ServerInfoBorder.IsVisible = false;
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
        try
        {
            await EnsureConnectedAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Couldn't connect: {ex.Message}", "OK");
            return;
        }
        await Navigation.PushAsync(new NewChatPage(_client));
    }

    async void OnSettingsClicked(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new SettingsPage(_client));
    }

    /// <summary>Re-populates the visible <see cref="Sessions"/> from <see cref="_allSessions"/> according
    /// to the current "show archived" switch state - no server call, this is purely a client-side
    /// filter over data already fetched in RefreshAsync.</summary>
    void ApplyArchiveFilter()
    {
        Sessions.Clear();
        foreach (var s in _allSessions.Where(s => _showArchived || !s.Archived)) Sessions.Add(s);
    }

    void OnShowArchivedToggled(object? sender, ToggledEventArgs e)
    {
        _showArchived = e.Value;
        // A search may currently be active (SearchBar not empty) - re-apply it against the same
        // "show archived" state rather than falling back to the unfiltered list underneath it.
        if (!string.IsNullOrWhiteSpace(SearchBarControl.Text))
        {
            OnSearchTextChanged(SearchBarControl, new TextChangedEventArgs(SearchBarControl.Text, SearchBarControl.Text));
            return;
        }
        ApplyArchiveFilter();
    }

    CancellationTokenSource? _searchCts;

    /// <summary>
    /// Full-text search (server-side, across session titles AND conversation content - see
    /// ChatClientService.SearchSessionsAsync). Debounced so fast typing doesn't fire a request per
    /// keystroke; clearing the box reverts to the normal <see cref="_allSessions"/> listing.
    /// </summary>
    async void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        var query = e.NewTextValue?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(query))
        {
            ApplyArchiveFilter();
            return;
        }

        try
        {
            await Task.Delay(300, cts.Token);
            await EnsureConnectedAsync();
            var results = await _client.SearchSessionsAsync(query, cts.Token);
            if (cts.IsCancellationRequested) return;
            Sessions.Clear();
            foreach (var s in results.Where(s => _showArchived || !s.Archived)) Sessions.Add(s);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer keystroke (or the page closing) - ignore.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HomePage] Search failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Shown from the "⋯" on each session card (PBI-020 follow-up: session management UI). Both
    /// SetSessionLabelAsync/SetSessionArchivedAsync just persist to the server's sidecar metadata
    /// store - a full RefreshAsync afterwards is the simplest way to reflect the change, since
    /// SessionSummary is a plain POCO with no change notification of its own to patch the existing
    /// ObservableCollection entry in place.
    /// </summary>
    async void OnCardMenuTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not SessionSummary session) return;

        var archiveActionText = session.Archived ? "アーカイブ解除" : "アーカイブ";
        var choice = await DisplayActionSheet(session.DisplayTitle, "キャンセル", null, "ラベルを編集", archiveActionText);

        try
        {
            if (choice == "ラベルを編集")
            {
                var newLabel = await DisplayPromptAsync(
                    "ラベルを編集",
                    "このセッションの表示名を入力してください(空にすると解除されます)",
                    initialValue: session.Label ?? "",
                    maxLength: 100);
                if (newLabel is null) return; // cancelled
                var trimmed = newLabel.Trim();
                await _client.SetSessionLabelAsync(session.Id, string.IsNullOrEmpty(trimmed) ? null : trimmed);
                await RefreshAsync();
            }
            else if (choice == archiveActionText)
            {
                await _client.SetSessionArchivedAsync(session.Id, !session.Archived);
                await RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"操作に失敗しました: {ex.Message}", "OK");
        }
    }
}
