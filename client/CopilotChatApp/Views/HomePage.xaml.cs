using System.Collections.ObjectModel;
using System.Linq;
using CopilotChatApp.Models;
using CopilotChatApp.Services;

namespace CopilotChatApp.Views;

/// <summary>
/// The app's launch screen (Shell's root page, see AppShell.xaml): aggregates past Copilot CLI
/// sessions from every configured <see cref="ServerProfile"/> (Phase 1 "federated" multi-server
/// design - see repo memory: each server independently owns its own sessions, this page just
/// fans out read-only sessions:list/search calls to all of them and merges the results) and lets
/// the user resume one or start a new chat. Each choice pushes a fresh <see cref="MainPage"/> onto
/// the navigation stack. (This used to duplicate an in-chat "☰ Sessions" flow on a separate
/// SessionsPage - that page was removed once this screen covered everything it did.)
///
/// Owns one ChatClientService connection per configured profile (kept alive across refreshes,
/// see <see cref="_clients"/>), separate from any MainPage's. By the time the user taps a session
/// or "New chat" here, iOS's local-network-access permission prompt (if any) has already been
/// resolved via this page's own connection attempts, so the MainPage that gets pushed can safely
/// make its own second connection without racing that prompt.
/// </summary>
public partial class HomePage : ContentPage
{
    /// <summary>One persistent connection per configured server profile, keyed by ServerProfile.Id -
    /// reused across refreshes rather than reconnecting every time. Built/pruned to match
    /// SettingsService.GetProfiles() at the top of every RefreshAsync.</summary>
    readonly Dictionary<string, ChatClientService> _clients = new();

    /// <summary>Every session fetched from every configured profile, regardless of archived status -
    /// the source list that <see cref="Sessions"/> is filtered from (see <see cref="ApplyArchiveFilter"/>).
    /// Kept separately so toggling "show archived" doesn't need another server round-trip.</summary>
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

    /// <summary>Returns (creating if necessary) the persistent client for one profile.</summary>
    ChatClientService GetOrCreateClient(string profileId)
    {
        if (!_clients.TryGetValue(profileId, out var client))
        {
            client = new ChatClientService();
            _clients[profileId] = client;
        }
        return client;
    }

    async Task EnsureConnectedAsync(ServerProfile profile)
    {
        var client = GetOrCreateClient(profile.Id);
        if (client.IsConnected) return;
        await client.ConnectWithRetryAsync(profile.Url, await SettingsService.GetProfileAuthTokenAsync(profile.Id));
    }

    async Task RefreshAsync()
    {
        var profiles = SettingsService.GetProfiles();
        if (profiles.Count == 0 || profiles.All(p => string.IsNullOrWhiteSpace(p.Url)))
        {
            NotConfiguredLabel.IsVisible = true;
            ServerInfoBorder.IsVisible = false;
            Sessions.Clear();
            return;
        }
        NotConfiguredLabel.IsVisible = false;

        // Drop connections for profiles that were removed in Settings since the last refresh.
        foreach (var staleId in _clients.Keys.Where(id => profiles.All(p => p.Id != id)).ToList())
        {
            _clients.Remove(staleId);
        }

        LoadingIndicator.IsVisible = true;
        LoadingIndicator.IsRunning = true;
        LoadingLabel.IsVisible = true;
        try
        {
            // Fan out to every configured server in parallel and merge - one offline/misconfigured
            // server shouldn't block the others from showing (federated design: each server is
            // independent, so a failure here is scoped to just that profile's sessions).
            _serverInfoByProfileId.Clear();
            var perProfileResults = await Task.WhenAll(profiles.Select(p => FetchProfileSessionsAsync(p)));
            _allSessions.Clear();
            foreach (var sessions in perProfileResults) _allSessions.AddRange(sessions);
            _allSessions.Sort((a, b) => string.CompareOrdinal(b.UpdatedAt, a.UpdatedAt));
            ApplyArchiveFilter();
            RefreshServerInfoSummary(profiles);
        }
        finally
        {
            LoadingIndicator.IsVisible = false;
            LoadingIndicator.IsRunning = false;
            LoadingLabel.IsVisible = false;
        }
    }

    /// <summary>One ServerInfo per successfully-reached profile from the most recent refresh (see
    /// RefreshServerInfoSummary) - cleared and rebuilt at the top of every RefreshAsync.</summary>
    readonly Dictionary<string, ServerInfo> _serverInfoByProfileId = new();

    /// <summary>Fetches one profile's server info + sessions, tags each session with
    /// ProfileId/ProfileName/OsGlyph, and swallows (logs) any failure - an offline server just
    /// contributes zero sessions rather than blocking the whole aggregated refresh, matching the
    /// federated design's "each server is independent" principle.</summary>
    async Task<List<SessionSummary>> FetchProfileSessionsAsync(ServerProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Url)) return new List<SessionSummary>();
        try
        {
            await EnsureConnectedAsync(profile);
            var client = GetOrCreateClient(profile.Id);

            // Best-effort: an older server that doesn't understand "server:info" yet (or any other
            // hiccup) just means this profile's sessions show a generic 💻 glyph and it's left out
            // of the summary chip - shouldn't fail the sessions fetch itself.
            string osGlyph = "💻";
            try
            {
                var info = await client.GetServerInfoAsync();
                _serverInfoByProfileId[profile.Id] = info;
                osGlyph = info.OsGlyph;
            }
            catch
            {
                // leave osGlyph as the generic default; no entry added to _serverInfoByProfileId
            }

            var sessions = await client.ListSessionsAsync();
            foreach (var s in sessions)
            {
                s.ProfileId = profile.Id;
                s.ProfileName = profile.Name;
                s.OsGlyph = osGlyph;
            }
            return sessions;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HomePage] Failed to list sessions for profile '{profile.Name}': {ex.Message}");
            return new List<SessionSummary>();
        }
    }

    /// <summary>
    /// Builds the top summary chip from every successfully-reached server this refresh (one tappable
    /// "🍎 Mac mini" label per server) - not tied to a single "active" profile, since this screen
    /// aggregates sessions from every configured server. Tapping a label filters the list down to
    /// just that server (see <see cref="_filterProfileId"/>); tapping the same one again clears it.
    /// Hidden entirely if not a single configured server could be reached.
    /// </summary>
    void RefreshServerInfoSummary(List<ServerProfile> profiles)
    {
        ServerInfoStack.Children.Clear();
        var reachable = profiles.Where(p => _serverInfoByProfileId.ContainsKey(p.Id)).ToList();

        if (reachable.Count == 0)
        {
            ServerInfoBorder.IsVisible = false;
            return;
        }

        foreach (var profile in reachable)
        {
            var isFiltered = _filterProfileId == profile.Id;
            var label = new Label
            {
                Text = $"{_serverInfoByProfileId[profile.Id].OsGlyph} {profile.Name}",
                FontSize = 19,
                FontAttributes = isFiltered ? FontAttributes.Bold : FontAttributes.None,
                TextDecorations = isFiltered ? TextDecorations.Underline : TextDecorations.None,
                TextColor = (Color?)Application.Current?.Resources[
                    Application.Current.RequestedTheme == AppTheme.Dark ? "SecondaryDarkText" : "Tertiary"]
                    ?? Colors.Black,
                Opacity = _filterProfileId is null || isFiltered ? 1.0 : 0.5,
            };
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => OnServerChipTapped(profile.Id);
            label.GestureRecognizers.Add(tap);
            ServerInfoStack.Children.Add(label);
        }
        ServerInfoBorder.IsVisible = true;
    }

    /// <summary>When set, only sessions from this profile are shown (see PassesFilters) - toggled by
    /// tapping a server chip built in RefreshServerInfoSummary. Null means "show every server".</summary>
    string? _filterProfileId;

    void OnServerChipTapped(string profileId)
    {
        _filterProfileId = _filterProfileId == profileId ? null : profileId;
        RefreshServerInfoSummary(SettingsService.GetProfiles());
        // A search may currently be active (SearchBar not empty) - re-apply it against the new
        // filter rather than falling back to the unfiltered list underneath it.
        if (!string.IsNullOrWhiteSpace(SearchBarControl.Text))
        {
            OnSearchTextChanged(SearchBarControl, new TextChangedEventArgs(SearchBarControl.Text, SearchBarControl.Text));
            return;
        }
        ApplyArchiveFilter();
    }

    /// <summary>The combined "should this session currently be visible" predicate - archive toggle AND server chip filter both apply together.</summary>
    bool PassesFilters(SessionSummary s)
        => (_showArchived || !s.Archived) && (_filterProfileId is null || s.ProfileId == _filterProfileId);

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
            var profile = SettingsService.GetProfiles().FirstOrDefault(p => p.Id == session.ProfileId);
            if (profile is null)
            {
                await DisplayAlert("Error", "This session's server profile no longer exists.", "OK");
                return;
            }
            await EnsureConnectedAsync(profile);
            var turns = await GetOrCreateClient(profile.Id).GetSessionHistoryAsync(session.Id);
            // Tapping a session switches the active profile to whichever server it lives on, so the
            // MainPage that opens (and any subsequent turn it sends) talks to the right server -
            // see SettingsService's back-compat single-active-profile API.
            SettingsService.ActiveProfileId = profile.Id;
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
        var profiles = SettingsService.GetProfiles().Where(p => !string.IsNullOrWhiteSpace(p.Url)).ToList();
        if (profiles.Count == 0)
        {
            await DisplayAlert("Not configured", "Please set up a server in Settings first.", "OK");
            return;
        }

        ServerProfile? profile = profiles.Count == 1 ? profiles[0] : null;
        if (profile is null)
        {
            // Multiple servers configured - ask which one this new chat should start on rather than
            // silently guessing (e.g. picking whatever happens to be "active").
            var choice = await DisplayActionSheet("どのサーバーで始めますか?", "キャンセル", null, profiles.Select(p => p.Name).ToArray());
            profile = profiles.FirstOrDefault(p => p.Name == choice);
            if (profile is null) return; // cancelled
        }

        try
        {
            await EnsureConnectedAsync(profile);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Couldn't connect: {ex.Message}", "OK");
            return;
        }
        SettingsService.ActiveProfileId = profile.Id;
        await Navigation.PushAsync(new NewChatPage(GetOrCreateClient(profile.Id)));
    }

    async void OnSettingsClicked(object? sender, EventArgs e)
    {
        // A fresh, disposable connection - SettingsPage connects using whatever profile ends up
        // active after the user saves there, which may not be any profile HomePage already has a
        // cached connection for.
        await Navigation.PushAsync(new SettingsPage(new ChatClientService()));
    }

    /// <summary>Re-populates the visible <see cref="Sessions"/> from <see cref="_allSessions"/> according
    /// to the current "show archived"/server-chip filter state - no server call, this is purely a
    /// client-side filter over data already fetched in RefreshAsync.</summary>
    void ApplyArchiveFilter()
    {
        Sessions.Clear();
        foreach (var s in _allSessions.Where(PassesFilters)) Sessions.Add(s);
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
    /// ChatClientService.SearchSessionsAsync), fanned out to every configured profile in parallel
    /// and merged, same as RefreshAsync. Debounced so fast typing doesn't fire a request per
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
            var profiles = SettingsService.GetProfiles().Where(p => !string.IsNullOrWhiteSpace(p.Url)).ToList();
            var perProfileResults = await Task.WhenAll(profiles.Select(p => SearchProfileSessionsAsync(p, query, cts.Token)));
            if (cts.IsCancellationRequested) return;

            Sessions.Clear();
            foreach (var results in perProfileResults)
            {
                foreach (var s in results.Where(PassesFilters)) Sessions.Add(s);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer keystroke (or the page closing) - ignore.
        }
    }

    async Task<List<SessionSummary>> SearchProfileSessionsAsync(ServerProfile profile, string query, CancellationToken ct)
    {
        try
        {
            await EnsureConnectedAsync(profile);
            var results = await GetOrCreateClient(profile.Id).SearchSessionsAsync(query, ct);
            foreach (var s in results)
            {
                s.ProfileId = profile.Id;
                s.ProfileName = profile.Name;
            }
            return results;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HomePage] Search failed for profile '{profile.Name}': {ex.Message}");
            return new List<SessionSummary>();
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
            var client = GetOrCreateClient(session.ProfileId);
            if (choice == "ラベルを編集")
            {
                var newLabel = await DisplayPromptAsync(
                    "ラベルを編集",
                    "このセッションの表示名を入力してください(空にすると解除されます)",
                    initialValue: session.Label ?? "",
                    maxLength: 100);
                if (newLabel is null) return; // cancelled
                var trimmed = newLabel.Trim();
                await client.SetSessionLabelAsync(session.Id, string.IsNullOrEmpty(trimmed) ? null : trimmed);
                await RefreshAsync();
            }
            else if (choice == archiveActionText)
            {
                await client.SetSessionArchivedAsync(session.Id, !session.Archived);
                await RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"操作に失敗しました: {ex.Message}", "OK");
        }
    }
}
