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
            // Fan out to every configured server in parallel, but DON'T wait for all of them
            // before showing anything (Task.WhenAll would let one slow/offline server hold the
            // whole screen hostage) - instead, process each one as it completes via Task.WhenAny
            // and re-render incrementally, so fast/reachable servers' sessions appear immediately
            // while a slow or unreachable one is still being given up on in the background.
            _serverInfoByProfileId.Clear();
            _unreachableProfileIds.Clear();
            _allSessions.Clear();
            var configuredProfiles = profiles.Where(p => !string.IsNullOrWhiteSpace(p.Url)).ToList();
            RefreshServerInfoSummary(profiles); // shows every configured server as "connecting…" (⋯) up front

            var pendingByTask = configuredProfiles.ToDictionary(p => FetchProfileSessionsAsync(p), p => p);
            var remaining = new List<Task<List<SessionSummary>>>(pendingByTask.Keys);
            while (remaining.Count > 0)
            {
                var finished = await Task.WhenAny(remaining);
                remaining.Remove(finished);
                var sessions = await finished; // already completed; FetchProfileSessionsAsync swallows its own errors
                _allSessions.AddRange(sessions);
                _allSessions.Sort((a, b) => string.CompareOrdinal(b.UpdatedAt, a.UpdatedAt));
                ApplyOrchestratorParentFlags();
                ApplyArchiveFilter();
                RefreshServerInfoSummary(profiles);
            }
        }
        finally
        {
            LoadingIndicator.IsVisible = false;
            LoadingIndicator.IsRunning = false;
            LoadingLabel.IsVisible = false;
        }
    }

    /// <summary>Re-runs the whole aggregation fetch on demand (toolbar 🔄) - useful after a server
    /// that was unreachable last time has come back up, without needing to leave and re-enter this
    /// page.</summary>
    async void OnRefreshClicked(object? sender, EventArgs e) => await RefreshAsync();

    /// <summary>One ServerInfo per successfully-reached profile from the most recent refresh (see
    /// RefreshServerInfoSummary) - cleared and rebuilt at the top of every RefreshAsync.</summary>
    readonly Dictionary<string, ServerInfo> _serverInfoByProfileId = new();

    /// <summary>Profiles that failed to connect (or failed to list sessions) on the most recent
    /// refresh - rendered as a grayed-out, non-tappable chip in RefreshServerInfoSummary rather than
    /// silently disappearing, so the user can see at a glance which configured server is down.
    /// Cleared and rebuilt at the top of every RefreshAsync.</summary>
    readonly HashSet<string> _unreachableProfileIds = new();

    /// <summary>Fetches one profile's server info + sessions, tags each session with
    /// ProfileId/ProfileName/OsGlyph, and swallows (logs) any failure - an offline server just
    /// contributes zero sessions rather than blocking the whole aggregated refresh, matching the
    /// federated design's "each server is independent" principle. Uses a short, bounded connect
    /// attempt (not the full ~30s-patient ConnectWithRetryAsync used for user-initiated actions
    /// like opening a session) so one unreachable server only costs this screen a few seconds
    /// rather than holding up every other server's sessions too.</summary>
    async Task<List<SessionSummary>> FetchProfileSessionsAsync(ServerProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Url)) return new List<SessionSummary>();
        var client = GetOrCreateClient(profile.Id);
        if (!client.IsConnected)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                await client.ConnectWithRetryAsync(profile.Url, await SettingsService.GetProfileAuthTokenAsync(profile.Id), maxAttempts: 3, cts.Token);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HomePage] '{profile.Name}' unreachable: {ex.Message}");
                _unreachableProfileIds.Add(profile.Id);
                return new List<SessionSummary>();
            }
        }

        try
        {
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
            _unreachableProfileIds.Add(profile.Id);
            return new List<SessionSummary>();
        }
    }

    /// <summary>
    /// Builds the top summary chip from every *configured* server (not just the reachable ones) -
    /// reachable servers get their OS glyph and are tappable to filter the list; servers still being
    /// connected to (this refresh hasn't reached them yet) show a "⋯ connecting" glyph; servers that
    /// failed to connect show a grayed-out "⚠️" glyph and aren't tappable. This lets the user see at a
    /// glance which of their servers is currently unreachable, rather than it just silently vanishing
    /// from the list. Hidden entirely if there are no configured servers at all.
    /// </summary>
    void RefreshServerInfoSummary(List<ServerProfile> profiles)
    {
        ServerInfoStack.Children.Clear();
        var configured = profiles.Where(p => !string.IsNullOrWhiteSpace(p.Url)).ToList();

        if (configured.Count == 0)
        {
            ServerInfoBorder.IsVisible = false;
            return;
        }

        foreach (var profile in configured)
        {
            var isReachable = _serverInfoByProfileId.ContainsKey(profile.Id);
            var isUnreachable = _unreachableProfileIds.Contains(profile.Id);
            var isFiltered = _filterProfileId == profile.Id;
            var glyph = isReachable ? _serverInfoByProfileId[profile.Id].OsGlyph : (isUnreachable ? "⚠️" : "⋯");
            var label = new Label
            {
                Text = $"{glyph} {profile.Name}",
                FontSize = 19,
                FontAttributes = isFiltered ? FontAttributes.Bold : FontAttributes.None,
                TextDecorations = isFiltered ? TextDecorations.Underline : TextDecorations.None,
                TextColor = isUnreachable
                    ? Colors.Gray
                    : (Color?)Application.Current?.Resources[
                        Application.Current.RequestedTheme == AppTheme.Dark ? "SecondaryDarkText" : "Tertiary"]
                        ?? Colors.Black,
                Opacity = isUnreachable ? 0.4 : (_filterProfileId is null || isFiltered ? 1.0 : 0.5),
            };
            if (isReachable)
            {
                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) => OnServerChipTapped(profile.Id);
                label.GestureRecognizers.Add(tap);
            }
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
        // PBI-027: a session that was ever opened as an Orchestrator "main" session reopens there
        // again, rather than the plain chat screen, so the user doesn't have to re-pick every time.
        if (session.OrchestratorMain)
        {
            await OpenOrchestratorAsync(session);
            return;
        }

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
        await Navigation.PushAsync(new NewChatPage(GetOrCreateClient(profile.Id), profile.Id));
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

    /// <summary>
    /// Computes SessionSummary.IsOrchestratorParent (PBI-027) for every session currently known in
    /// <see cref="_allSessions"/> - by design NOT sent by the server (see SessionSummaryDto's doc
    /// comment in protocol.ts): a single server can only ever see children recorded in its own
    /// sessionMeta.json, silently missing any child spawned on a *different* configured profile
    /// (PBI-026's cross-server spawn). This screen already aggregates every profile's sessions into
    /// one list, so it can correctly answer "does this session have children anywhere" by
    /// cross-referencing every session's ParentSessionId against every other session's Id. Called
    /// after every incremental merge in RefreshAsync, so the badge is as complete as whatever's been
    /// fetched so far and fully correct once every profile has responded.
    /// </summary>
    void ApplyOrchestratorParentFlags()
    {
        var parentIds = new HashSet<string>(_allSessions.Select(s => s.ParentSessionId).Where(id => !string.IsNullOrEmpty(id))!);
        foreach (var s in _allSessions)
        {
            s.IsOrchestratorParent = parentIds.Contains(s.Id);
        }
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
    /// ObservableCollection entry in place. "完全に削除" (PBI-021's hard delete) is irreversible
    /// (removes the session from the CLI's own session-store.db, not just this app's sidecar), so
    /// it's gated behind its own confirmation dialog and deliberately placed last as a plain
    /// (non-destructive-styled) button rather than DisplayActionSheet's dedicated `destruction` slot -
    /// that slot renders first/most-prominent on some platforms (Mac Catalyst), which felt too easy
    /// to tap by accident for something this irreversible.
    /// </summary>
    async void OnCardMenuTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not SessionSummary session) return;

        const string orchestratorActionText = "🎼 Orchestratorで開く";
        const string deleteActionText = "完全に削除...";
        var archiveActionText = session.Archived ? "アーカイブ解除" : "アーカイブ";
        var choice = await DisplayActionSheet(session.DisplayTitle, "キャンセル", null, orchestratorActionText, "ラベルを編集", archiveActionText, deleteActionText);

        try
        {
            var client = GetOrCreateClient(session.ProfileId);
            if (choice == orchestratorActionText)
            {
                await OpenOrchestratorAsync(session);
            }
            else if (choice == "ラベルを編集")
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
            else if (choice == deleteActionText)
            {
                var confirmed = await DisplayAlert(
                    "完全に削除しますか?",
                    "このセッションと会話履歴を、Copilot CLI自体の履歴からも完全に削除します。この操作は元に戻せません。",
                    "削除する",
                    "キャンセル");
                if (!confirmed) return;

                await client.DeleteSessionAsync(session.Id, "hard");
                await RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"操作に失敗しました: {ex.Message}", "OK");
        }
    }

    /// <summary>Opens the Orchestrator screen (PBI-025) with `session` as the main session - same connect/history-fetch dance as <see cref="OpenSessionAsync"/>.</summary>
    async Task OpenOrchestratorAsync(SessionSummary session)
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
            SettingsService.ActiveProfileId = profile.Id;
            await Navigation.PushAsync(new OrchestratorPage(session, turns, profile.Id));
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Failed to open Orchestrator: {ex.Message}", "OK");
        }
        finally
        {
            LoadingIndicator.IsVisible = false;
            LoadingIndicator.IsRunning = false;
        }
    }
}
