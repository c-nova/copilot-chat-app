namespace CopilotChatApp.Models;

/// <summary>
/// One configured Copilot chat server (see the "federated" multi-server design in repo memory:
/// each server independently owns its own sessions; the client just aggregates read-only listings
/// across every configured profile - see HomePage). The auth token is deliberately NOT a field
/// here: profiles themselves (id/name/url/conversationId) aren't secret and are stored in plain
/// Preferences as JSON, but the token is a bearer secret kept separately in SettingsService's
/// AES-GCM-encrypted per-profile token store, keyed by Id.
/// </summary>
public class ServerProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;

    /// <summary>Stable per-profile conversation id so "New chat" on this server resumes the same
    /// Copilot CLI session across app restarts, independently of every other configured profile.</summary>
    public string ConversationId { get; set; } = Guid.NewGuid().ToString();
}
