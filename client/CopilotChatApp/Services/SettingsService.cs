namespace CopilotChatApp.Services;

/// <summary>
/// Persists user-configurable settings (server URL, auth token) using MAUI Preferences,
/// and a stable per-install conversation id so the server can resume the same Copilot CLI session.
/// </summary>
public static class SettingsService
{
    const string ServerUrlKey = "server_url";
    const string AuthTokenKey = "auth_token";
    const string ConversationIdKey = "conversation_id";

    public static string ServerUrl
    {
        get => Preferences.Default.Get(ServerUrlKey, string.Empty);
        set => Preferences.Default.Set(ServerUrlKey, value);
    }

    public static string AuthToken
    {
        get => Preferences.Default.Get(AuthTokenKey, string.Empty);
        set => Preferences.Default.Set(AuthTokenKey, value);
    }

    public static string ConversationId
    {
        get
        {
            var id = Preferences.Default.Get(ConversationIdKey, string.Empty);
            if (string.IsNullOrEmpty(id))
            {
                id = Guid.NewGuid().ToString();
                Preferences.Default.Set(ConversationIdKey, id);
            }
            return id;
        }
    }

    public static void ResetConversation()
    {
        Preferences.Default.Set(ConversationIdKey, Guid.NewGuid().ToString());
    }

    /// <summary>Switches the active conversation to an existing Copilot CLI session id (used when resuming a past session from the Sessions list).</summary>
    public static void SetConversation(string sessionId)
    {
        Preferences.Default.Set(ConversationIdKey, sessionId);
    }

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(ServerUrl) && !string.IsNullOrWhiteSpace(AuthToken);
}
