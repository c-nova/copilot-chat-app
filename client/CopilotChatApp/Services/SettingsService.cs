using System.Security.Cryptography;
using System.Text;

namespace CopilotChatApp.Services;

/// <summary>
/// Persists user-configurable settings (server URL, auth token) and a stable per-install
/// conversation id so the server can resume the same Copilot CLI session.
/// The auth token is a bearer secret. It's stored in an app-local AES-GCM-encrypted file rather
/// than platform SecureStorage/Keychain: on this Mac (MDM-managed), any app entitlement
/// (including keychain-access-groups, required for Keychain access) makes an unsigned/ad-hoc
/// local dev build fail to launch at all ("Launchd job spawn failed" via ConfigurationProfiles).
/// This is weaker than OS Keychain (the encryption key lives on disk next to the ciphertext,
/// just with restrictive file permissions) but far better than plain text, and it doesn't require
/// code signing or entitlements. See PBI-013. Server URL and conversation id aren't secrets, so
/// Preferences is fine for those.
/// </summary>
public static class SettingsService
{
    const string ServerUrlKey = "server_url";
    const string AuthTokenKey = "auth_token";
    const string ConversationIdKey = "conversation_id";
    const string ChatFontSizeKey = "chat_font_size";

    const string AuthKeyFileName = ".authstore.key";
    const string AuthTokenFileName = ".authstore.enc";
    const int AesKeySizeBytes = 32;
    const int AesNonceSizeBytes = 12;
    const int AesTagSizeBytes = 16;

    /// <summary>Key of the dynamic resource that chat text controls bind their FontSize to (see App.xaml).</summary>
    public const string ChatFontSizeResourceKey = "ChatFontSize";
    public const double DefaultChatFontSize = 15.0;
    public const double MinChatFontSize = 12.0;
    public const double MaxChatFontSize = 28.0;

    public static string ServerUrl
    {
        get => Preferences.Default.Get(ServerUrlKey, string.Empty);
        set => Preferences.Default.Set(ServerUrlKey, value);
    }

    /// <summary>
    /// Chat message font size (points). Persisted in Preferences and mirrored into
    /// Application.Current.Resources[ChatFontSizeResourceKey] so every control bound via
    /// {DynamicResource ChatFontSize} updates immediately, without restarting the app.
    /// </summary>
    public static double ChatFontSize
    {
        get => Preferences.Default.Get(ChatFontSizeKey, DefaultChatFontSize);
        set
        {
            var clamped = Math.Clamp(value, MinChatFontSize, MaxChatFontSize);
            Preferences.Default.Set(ChatFontSizeKey, clamped);
            ApplyChatFontSizeResource(clamped);
        }
    }

    /// <summary>Pushes the current (or default) font size into app resources. Call once at startup.</summary>
    public static void ApplyChatFontSizeResource() => ApplyChatFontSizeResource(ChatFontSize);

    static void ApplyChatFontSizeResource(double size)
    {
        if (Application.Current is not null)
        {
            Application.Current.Resources[ChatFontSizeResourceKey] = size;
        }
    }

    /// <summary>Reads the auth token, migrating it from older storage locations (SecureStorage, then plain-text Preferences) if found.</summary>
    public static async Task<string> GetAuthTokenAsync()
    {
        try
        {
            var token = ReadEncryptedToken();
            if (!string.IsNullOrEmpty(token)) return token;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsService] Encrypted token store read failed: {ex}");
        }

        // One-time migration: earlier versions stored the token in platform SecureStorage (Keychain).
        // GetAsync can throw if secure storage is unavailable on this device - treat as "not found".
        string? legacySecureToken = null;
        try
        {
            legacySecureToken = await SecureStorage.Default.GetAsync(AuthTokenKey);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsService] SecureStorage.GetAsync failed: {ex}");
        }
        if (!string.IsNullOrEmpty(legacySecureToken))
        {
            WriteEncryptedToken(legacySecureToken);
            try { SecureStorage.Default.Remove(AuthTokenKey); } catch { /* best-effort cleanup */ }
            return legacySecureToken;
        }

        // Even older migration: plain-text Preferences entry.
        var legacyPrefsToken = Preferences.Default.Get(AuthTokenKey, string.Empty);
        if (!string.IsNullOrEmpty(legacyPrefsToken))
        {
            WriteEncryptedToken(legacyPrefsToken);
            Preferences.Default.Remove(AuthTokenKey);
            return legacyPrefsToken;
        }
        return string.Empty;
    }

    /// <summary>
    /// Writes the auth token to the app-local AES-GCM-encrypted file. Throws if the file can't be
    /// written - callers should surface this to the user rather than silently swallowing it, since
    /// a failed save here means the token was NOT persisted.
    /// </summary>
    public static Task SetAuthTokenAsync(string value)
    {
        WriteEncryptedToken(value);
        return Task.CompletedTask;
    }

    static string AuthKeyFilePath => Path.Combine(FileSystem.AppDataDirectory, AuthKeyFileName);
    static string AuthTokenFilePath => Path.Combine(FileSystem.AppDataDirectory, AuthTokenFileName);

    /// <summary>Loads the AES key from disk, generating and persisting a new random one on first use.</summary>
    static byte[] GetOrCreateEncryptionKey()
    {
        if (File.Exists(AuthKeyFilePath))
        {
            var existing = File.ReadAllBytes(AuthKeyFilePath);
            if (existing.Length == AesKeySizeBytes) return existing;
        }

        Directory.CreateDirectory(FileSystem.AppDataDirectory);
        var key = RandomNumberGenerator.GetBytes(AesKeySizeBytes);
        File.WriteAllBytes(AuthKeyFilePath, key);
        TryRestrictToOwnerOnly(AuthKeyFilePath);
        return key;
    }

    /// <summary>Decrypts and returns the stored token, or null if no token file exists yet.</summary>
    static string? ReadEncryptedToken()
    {
        if (!File.Exists(AuthTokenFilePath)) return null;

        var blob = File.ReadAllBytes(AuthTokenFilePath);
        if (blob.Length < AesNonceSizeBytes + AesTagSizeBytes) return null;

        var nonce = blob.AsSpan(0, AesNonceSizeBytes);
        var tag = blob.AsSpan(AesNonceSizeBytes, AesTagSizeBytes);
        var ciphertext = blob.AsSpan(AesNonceSizeBytes + AesTagSizeBytes);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(GetOrCreateEncryptionKey(), AesTagSizeBytes);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>Encrypts and writes the token to disk (or deletes the file if value is empty).</summary>
    static void WriteEncryptedToken(string value)
    {
        Directory.CreateDirectory(FileSystem.AppDataDirectory);

        if (string.IsNullOrEmpty(value))
        {
            if (File.Exists(AuthTokenFilePath)) File.Delete(AuthTokenFilePath);
            return;
        }

        var plaintext = Encoding.UTF8.GetBytes(value);
        var nonce = RandomNumberGenerator.GetBytes(AesNonceSizeBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[AesTagSizeBytes];

        using (var aes = new AesGcm(GetOrCreateEncryptionKey(), AesTagSizeBytes))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        var blob = new byte[AesNonceSizeBytes + AesTagSizeBytes + ciphertext.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, AesNonceSizeBytes);
        ciphertext.CopyTo(blob, AesNonceSizeBytes + AesTagSizeBytes);
        File.WriteAllBytes(AuthTokenFilePath, blob);
        TryRestrictToOwnerOnly(AuthTokenFilePath);
    }

    /// <summary>Best-effort: restrict the file to owner read/write only (no-op on platforms without POSIX permissions, e.g. Windows).</summary>
    static void TryRestrictToOwnerOnly(string path)
    {
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (PlatformNotSupportedException)
        {
            // Windows - no POSIX file modes. NTFS ACLs already restrict AppData to the current user.
        }
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

    public static async Task<bool> IsConfiguredAsync()
    {
        var token = await GetAuthTokenAsync();
        return !string.IsNullOrWhiteSpace(ServerUrl) && !string.IsNullOrWhiteSpace(token);
    }
}
