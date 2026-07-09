using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using CopilotChatApp.Models;
using CopilotChatApp.Services;

namespace CopilotChatApp.ViewModels;

/// <summary>
/// One child session's live-updating log pane in the Orchestrator screen (PBI-025 Phase 3).
/// Read-only by design - a child session is only ever driven by the main session's AI (via
/// run_turn_on_session/spawn_session), never directly by the human user, so there's deliberately no
/// input box here (see PBI.md's PBI-025 design notes).
/// </summary>
public class ChildSessionPaneViewModel : INotifyPropertyChanged
{
    public string SessionId { get; }

    SessionSummary _session;
    public SessionSummary Session
    {
        get => _session;
        set
        {
            _session = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(IsBusy));
        }
    }

    public string Title => Session.DisplayTitle;
    public bool IsBusy => Session.Busy;

    /// <summary>Turn count as of the last full history fetch - lets OrchestratorViewModel's poll
    /// loop skip re-fetching history for children that haven't actually changed since last time.</summary>
    public int LastFetchedTurnCount { get; private set; } = -1;

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    public ChildSessionPaneViewModel(SessionSummary session)
    {
        SessionId = session.Id;
        _session = session;
    }

    /// <summary>Replaces the displayed log with the given turns (same user+assistant flattening as ChatViewModel.ApplyResumedSession).</summary>
    public void ApplyHistory(List<SessionTurn> turns)
    {
        Messages.Clear();
        foreach (var turn in turns)
        {
            if (!string.IsNullOrWhiteSpace(turn.UserMessage))
            {
                Messages.Add(new ChatMessage { Role = ChatRole.User, Text = turn.UserMessage, IsFromOtherSession = turn.FromOtherSession });
            }
            if (!string.IsNullOrWhiteSpace(turn.AssistantResponse))
            {
                Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Text = turn.AssistantResponse, IsFromOtherSession = turn.FromOtherSession });
            }
        }
        LastFetchedTurnCount = Session.TurnCount;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Orchestrator screen's state (PBI-025 Phase 3, extended by PBI-026 for cross-server children):
/// wraps the main session's chat exactly like MainPage does (same ChatViewModel, same
/// ApplyResumedSession), plus a polling-refreshed list of child session panes spawned/attached
/// under it - possibly on a *different* configured server profile (see PBI.md's PBI-026 entry:
/// child sessions aren't limited to the main session's own machine). Polling rather than streaming
/// by design - see PBI.md's PBI-025 entry for why a pub/sub push mechanism was rejected.
/// </summary>
public class OrchestratorViewModel : IAsyncDisposable
{
    readonly string _mainSessionId;
    readonly string _mainProfileId;
    readonly ChatClientService _chatClient;
    /// <summary>Connections to *other* configured server profiles (PBI-026) - lazily created as children on them are discovered/spawned. The main profile's connection is <see cref="_chatClient"/>, not stored here, so DisposeAsync doesn't try to dispose it twice.</summary>
    readonly Dictionary<string, ChatClientService> _peerClients = new();
    Task? _pollLoopTask;
    CancellationTokenSource? _pollCts;

    public ChatViewModel MainChat { get; }
    public ObservableCollection<ChildSessionPaneViewModel> Children { get; } = new();

    /// <summary>The main session's own id - used by OrchestratorPage to exclude it (and already-attached children) from the "attach existing session" picker.</summary>
    public string MainSessionId => _mainSessionId;

    /// <summary>The server profile the main session lives on - used by OrchestratorPage to mark it as "(このセッション)" in the target-server picker.</summary>
    public string MainProfileId => _mainProfileId;

    public OrchestratorViewModel(SessionSummary mainSession, List<SessionTurn> mainTurns, string mainProfileId)
    {
        _mainSessionId = mainSession.Id;
        _mainProfileId = mainProfileId;
        MainChat = new ChatViewModel();
        MainChat.ApplyResumedSession(mainSession, mainTurns);
        // Reuse the exact same (already-connecting) socket the main chat uses, rather than opening a
        // second one - see ChatViewModel.ChatClient's own doc comment for why that matters on iOS.
        _chatClient = MainChat.ChatClient;
    }

    /// <summary>
    /// Opens the Orchestrator screen for a brand-new main session rooted at <paramref name="cwd"/>
    /// (or the server's default workspace, if null) - the "New Chat" flow's Orchestrator option.
    /// The session id is already fixed by the caller via SettingsService.ResetConversation() before
    /// this constructor runs (same pattern MainPage(string cwd) relies on) - see NewChatPage.
    /// </summary>
    public OrchestratorViewModel(string? cwd, string mainProfileId)
    {
        _mainSessionId = SettingsService.ConversationId;
        _mainProfileId = mainProfileId;
        MainChat = new ChatViewModel();
        if (cwd is not null) MainChat.SetPendingCwd(cwd);
        _chatClient = MainChat.ChatClient;
    }

    /// <summary>Gets (connecting lazily if needed isn't done here - see EnsureConnectedAsync) the ChatClientService for a given profile id - the main profile's own client if it matches, otherwise a lazily-created peer connection.</summary>
    public ChatClientService GetOrCreateClient(string profileId)
    {
        if (profileId == _mainProfileId) return _chatClient;
        if (!_peerClients.TryGetValue(profileId, out var client))
        {
            client = new ChatClientService();
            _peerClients[profileId] = client;
        }
        return client;
    }

    /// <summary>Ensures the given profile's connection is up, using a short bounded attempt for peers (an unreachable peer just means we skip its children this round - see RefreshChildrenAsync) or the more patient retry for the main profile.</summary>
    public async Task EnsureConnectedAsync(string profileId)
    {
        var client = GetOrCreateClient(profileId);
        if (client.IsConnected) return;
        var profile = SettingsService.GetProfiles().FirstOrDefault(p => p.Id == profileId);
        if (profile is null || string.IsNullOrWhiteSpace(profile.Url)) throw new InvalidOperationException("This server profile no longer exists.");
        var token = await SettingsService.GetProfileAuthTokenAsync(profileId);
        if (profileId == _mainProfileId)
        {
            await client.ConnectWithRetryAsync(profile.Url, token);
        }
        else
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            await client.ConnectWithRetryAsync(profile.Url, token, maxAttempts: 3, cts.Token);
        }
    }

    /// <summary>Lists sessions on an arbitrary configured profile (for the "既存セッションをアタッチ" picker's target-server step), tagging each with ProfileId/ProfileName same as HomePage's aggregation.</summary>
    public async Task<List<SessionSummary>> ListSessionsOnProfileAsync(string profileId)
    {
        await EnsureConnectedAsync(profileId);
        var profile = SettingsService.GetProfiles().First(p => p.Id == profileId);
        var sessions = await GetOrCreateClient(profileId).ListSessionsAsync();
        foreach (var s in sessions)
        {
            s.ProfileId = profile.Id;
            s.ProfileName = profile.Name;
        }
        return sessions;
    }

    /// <summary>Starts polling children every few seconds. Call once from the page's OnAppearing; stop via DisposeAsync (page disappearing/popped).</summary>
    public void StartPolling()
    {
        if (_pollLoopTask is not null) return;
        _pollCts = new CancellationTokenSource();
        var ct = _pollCts.Token;
        _pollLoopTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await RefreshChildrenAsync(); }
                catch { /* transient connectivity hiccup - just try again next tick */ }

                try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
                catch (OperationCanceledException) { break; }
            }
        }, ct);
    }

    /// <summary>Marks the main session as an Orchestrator "main" session (PBI-027), idempotent - call
    /// once from the page's OnAppearing (after the main chat's own connect) so HomePage knows to
    /// reopen this session directly into the Orchestrator screen next time. Best-effort: swallows
    /// failures (e.g. offline right at open) since it's just a convenience annotation, not something
    /// that should block using the screen.</summary>
    public async Task MarkAsOrchestratorMainAsync()
    {
        try
        {
            await _chatClient.MarkSessionAsOrchestratorMainAsync(_mainSessionId);
        }
        catch
        {
            // best-effort only - see remarks above
        }
    }

    /// <summary>Fetches the current child list + busy flags from *every* configured server profile
    /// (PBI-026 - children aren't limited to the main session's own machine), adds/removes panes to
    /// match, and re-fetches full history only for children that actually changed since last time
    /// (new turn landed) or are currently busy (so we catch the result the moment it lands). An
    /// unreachable peer just contributes zero children this round rather than failing the whole
    /// refresh - same "each server is independent" principle as HomePage's aggregation.</summary>
    public async Task RefreshChildrenAsync()
    {
        var profiles = SettingsService.GetProfiles().Where(p => !string.IsNullOrWhiteSpace(p.Url)).ToList();
        var seen = new HashSet<string>();

        foreach (var profile in profiles)
        {
            List<SessionSummary> summaries;
            try
            {
                await EnsureConnectedAsync(profile.Id);
                summaries = await GetOrCreateClient(profile.Id).GetChildSessionsAsync(_mainSessionId);
            }
            catch
            {
                continue; // this profile is unreachable right now - try again next poll tick
            }

            foreach (var summary in summaries)
            {
                summary.ProfileId = profile.Id;
                summary.ProfileName = profile.Name;
                seen.Add(summary.Id);
                var pane = Children.FirstOrDefault(p => p.SessionId == summary.Id);
                if (pane is null)
                {
                    pane = new ChildSessionPaneViewModel(summary);
                    var newPane = pane;
                    MainThread.BeginInvokeOnMainThread(() => Children.Add(newPane));
                }
                else
                {
                    var existingPane = pane;
                    MainThread.BeginInvokeOnMainThread(() => existingPane.Session = summary);
                }

                if (pane.LastFetchedTurnCount != summary.TurnCount || summary.Busy)
                {
                    try
                    {
                        var turns = await GetOrCreateClient(profile.Id).GetSessionHistoryAsync(summary.Id);
                        var targetPane = pane;
                        MainThread.BeginInvokeOnMainThread(() => targetPane.ApplyHistory(turns));
                    }
                    catch { /* try again next tick */ }
                }
            }
        }

        // Drop panes for children that no longer exist anywhere we could reach (e.g. hard-deleted elsewhere).
        var stale = Children.Where(p => !seen.Contains(p.SessionId)).ToList();
        if (stale.Count > 0)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                foreach (var p in stale) Children.Remove(p);
            });
        }
    }

    /// <summary>Creates a brand-new child session with an initial instruction on <paramref name="targetProfileId"/> (manual "+ add child" flow - may be a different server than the main session's own, per PBI-026).</summary>
    public async Task<string> SpawnNewChildAsync(string targetProfileId, string message, string? cwd)
    {
        await EnsureConnectedAsync(targetProfileId);
        var (sessionId, _) = await GetOrCreateClient(targetProfileId).SpawnChildSessionAsync(_mainSessionId, cwd: cwd, message: message);
        await RefreshChildrenAsync();
        return sessionId;
    }

    /// <summary>Attaches an already-existing session on <paramref name="targetProfileId"/> as a child for visibility only (no message sent).</summary>
    public async Task AttachExistingChildAsync(string targetProfileId, string existingSessionId)
    {
        await EnsureConnectedAsync(targetProfileId);
        await GetOrCreateClient(targetProfileId).SpawnChildSessionAsync(_mainSessionId, existingSessionId: existingSessionId);
        await RefreshChildrenAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _pollCts?.Cancel();
        if (_pollLoopTask is not null)
        {
            try { await _pollLoopTask; } catch { /* ignore */ }
        }
        foreach (var client in _peerClients.Values)
        {
            try { await client.DisposeAsync(); } catch { /* ignore */ }
        }
        _peerClients.Clear();
        await MainChat.DisposeAsync();
    }
}
