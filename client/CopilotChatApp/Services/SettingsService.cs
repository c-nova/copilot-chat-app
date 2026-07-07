using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotChatApp.Models;

namespace CopilotChatApp.Services;

/// <summary>
/// Persists user-configurable settings. Server connections are modeled as a list of
/// <see cref="ServerProfile"/> (Phase 1 of the multi-server redesign - see repo memory's "federated"
/// design: each server independently owns its own sessions, the client just aggregates read-only
/// listings across every configured profile). A single legacy server_url/auth_token/conversation_id
/// setup (from before profiles existed) is migrated into a lone "Default" profile on first access,
/// so upgrading doesn't lose an already-configured server.
///
/// The back-compat ServerUrl/ConversationId/GetAuthTokenAsync/etc. properties below all operate on
/// the *active* profile, so every call site written before profiles existed (HomePage,
/// ChatViewModel, SettingsPage, McpServersPage) keeps working completely unchanged against a single
/// server until those call sites are updated to be profile-aware (later increments).
///
/// Auth tokens are bearer secrets. They're stored in an app-local AES-GCM-encrypted file rather
/// than platform SecureStorage/Keychain: on this Mac (MDM-managed), any app entitlement
/// (including keychain-access-groups, required for Keychain access) makes an unsigned/ad-hoc
/// local dev build fail to launch at all ("Launchd job spawn failed" via ConfigurationProfiles).
/// This is weaker than OS Keychain (the encryption key lives on disk next to the ciphertext,
/// just with restrictive file permissions) but far better than plain text, and it doesn't require
/// code signing or entitlements. See PBI-013. Profile metadata (name/url/conversationId) isn't
/// secret, so plain Preferences (as JSON) is fine for that.
/// </summary>
public static class SettingsService
{
    const string LegacyServerUrlKey = "server_url";
    const string LegacyAuthTokenKey = "auth_token";
    const string LegacyConversationIdKey = "conversation_id";
    const string ChatFontSizeKey = "chat_font_size";

    const string ServerProfilesKey = "server_profiles";
    const string ActiveProfileIdKey = "active_profile_id";
    /// <summary>Id of the profile created by one-time legacy migration, if any - the *only* profile
    /// allowed to fall back to the old single-token encrypted store when its per-profile token entry
    /// is missing (see GetProfileAuthTokenAsync). Prevents a brand-new profile from accidentally
    /// picking up leftover legacy secrets.</summary>
    const string LegacyMigratedProfileIdKey = "legacy_migrated_profile_id";

    const string AuthKeyFileName = ".authstore.key";
    const string LegacyAuthTokenFileName = ".authstore.enc";
    const string ProfileTokensFileName = ".authstore.profiles.enc";
    const int AesKeySizeBytes = 32;
    const int AesNonceSizeBytes = 12;
    const int AesTagSizeBytes = 16;

    /// <summary>Key of the dynamic resource that chat text controls bind their FontSize to (see App.xaml).</summary>
    public const string ChatFontSizeResourceKey = "ChatFontSize";
    public const double DefaultChatFontSize = 15.0;
    public const double MinChatFontSize = 12.0;
    public const double MaxChatFontSize = 28.0;

    static bool _profilesMigrated;

    /// <summary>
    /// One-time: if no profile list exists yet, create one from whatever legacy server_url/
    /// conversation_id settings are already in Preferences (empty strings if this is a genuinely
    /// fresh install - GetActiveProfile/ServerUrl etc. all tolerate an empty Url). The auth token
    /// itself is migrated lazily, the first time it's actually requested (see
    /// GetProfileAuthTokenAsync) - it needs async/exception-handling work (SecureStorage fallback)
    /// that doesn't belong in this synchronous bookkeeping step.
    /// </summary>
    static void EnsureProfilesMigrated()
    {
        if (_profilesMigrated) return;
        _profilesMigrated = true;

        if (!string.IsNullOrEmpty(Preferences.Default.Get(ServerProfilesKey, string.Empty))) return;

        var legacyUrl = Preferences.Default.Get(LegacyServerUrlKey, string.Empty);
        var legacyConversationId = Preferences.Default.Get(LegacyConversationIdKey, string.Empty);
        var profile = new ServerProfile
        {
            Name = "Default",
            Url = legacyUrl,
            ConversationId = string.IsNullOrEmpty(legacyConversationId) ? Guid.NewGuid().ToString() : legacyConversationId,
        };
        SaveProfiles(new List<ServerProfile> { profile });
        Preferences.Default.Set(ActiveProfileIdKey, profile.Id);
        Preferences.Default.Set(LegacyMigratedProfileIdKey, profile.Id);
        Preferences.Default.Remove(LegacyServerUrlKey);
        Preferences.Default.Remove(LegacyConversationIdKey);
    }

    public static List<ServerProfile> GetProfiles()
    {
        EnsureProfilesMigrated();
        var json = Preferences.Default.Get(ServerProfilesKey, string.Empty);
        if (string.IsNullOrEmpty(json)) return new List<ServerProfile>();
        try
        {
            return JsonSerializer.Deserialize<List<ServerProfile>>(json) ?? new List<ServerProfile>();
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsService] Corrupt server_profiles, resetting: {ex}");
            return new List<ServerProfile>();
        }
    }

    static void SaveProfiles(List<ServerProfile> profiles)
        => Preferences.Default.Set(ServerProfilesKey, JsonSerializer.Serialize(profiles));

    /// <summary>The profile every back-compat single-server API operates on. Falls back to the first
    /// configured profile if the stored active id doesn't match any (e.g. that profile was removed).</summary>
    public static string ActiveProfileId
    {
        get
        {
            EnsureProfilesMigrated();
            return Preferences.Default.Get(ActiveProfileIdKey, string.Empty);
        }
        set => Preferences.Default.Set(ActiveProfileIdKey, value);
    }

    public static ServerProfile? GetActiveProfile()
    {
        var profiles = GetProfiles();
        return profiles.FirstOrDefault(p => p.Id == ActiveProfileId) ?? profiles.FirstOrDefault();
    }

    public static ServerProfile AddProfile(string name, string url)
    {
        var profiles = GetProfiles();
        var profile = new ServerProfile { Name = name, Url = url };
        profiles.Add(profile);
        SaveProfiles(profiles);
        if (profiles.Count == 1) ActiveProfileId = profile.Id;
        return profile;
    }

    public static void UpdateProfile(ServerProfile updated)
    {
        var profiles = GetProfiles();
        var index = profiles.FindIndex(p => p.Id == updated.Id);
        if (index < 0) return;
        profiles[index] = updated;
        SaveProfiles(profiles);
    }

    public static void RemoveProfile(string profileId)
    {
        var profiles = GetProfiles();
        profiles.RemoveAll(p => p.Id == profileId);
        SaveProfiles(profiles);
        RemoveProfileAuthToken(profileId);
        if (ActiveProfileId == profileId)
        {
            ActiveProfileId = profiles.FirstOrDefault()?.Id ?? string.Empty;
        }
    }

    // --- Back-compat single-profile API: delegates to the active profile ---

    public static string ServerUrl
    {
        get => GetActiveProfile()?.Url ?? string.Empty;
        set
        {
            var active = GetActiveProfile();
            if (active is null)
            {
                active = AddProfile("Default", value);
            }
            else
            {
                active.Url = value;
                UpdateProfile(active);
            }
        }
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

    /// <summary>Reads the active profile's auth token (see GetProfileAuthTokenAsync).</summary>
    public static Task<string> GetAuthTokenAsync() => GetProfileAuthTokenAsync(ActiveProfileId);

    /// <summary>
    /// Reads one profile's auth token from the per-profile encrypted store. If it's missing there
    /// AND this is the profile created by legacy migration, falls back to the pre-profiles storage
    /// chain (its own encrypted file, then SecureStorage, then plain-text Preferences - in that
    /// order, each is what earlier app versions used) and migrates whatever's found into the new
    /// per-profile store so this fallback only ever runs once per install.
    /// </summary>
    public static async Task<string> GetProfileAuthTokenAsync(string profileId)
    {
        if (string.IsNullOrEmpty(profileId)) return string.Empty;

        var tokens = ReadEncryptedProfileTokens();
        if (tokens.TryGetValue(profileId, out var token) && !string.IsNullOrEmpty(token))
        {
            return token;
        }

        if (profileId != Preferences.Default.Get(LegacyMigratedProfileIdKey, string.Empty))
        {
            return string.Empty;
        }

        var legacyToken = await ReadAndMigrateLegacyAuthTokenAsync();
        if (string.IsNullOrEmpty(legacyToken)) return string.Empty;

        await SetProfileAuthTokenAsync(profileId, legacyToken);
        return legacyToken;
    }

    /// <summary>Walks the pre-profiles token storage chain (own encrypted file -> SecureStorage -> plain-text Preferences) and clears each source once its value has been read, so this only ever runs once.</summary>
    static async Task<string> ReadAndMigrateLegacyAuthTokenAsync()
    {
        try
        {
            var token = ReadLegacyEncryptedToken();
            if (!string.IsNullOrEmpty(token))
            {
                try { File.Delete(LegacyAuthTokenFilePath); } catch { /* best-effort cleanup */ }
                return token;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsService] Legacy encrypted token store read failed: {ex}");
        }

        // One-time migration: earlier versions stored the token in platform SecureStorage (Keychain).
        // GetAsync can throw if secure storage is unavailable on this device - treat as "not found".
        string? legacySecureToken = null;
        try
        {
            legacySecureToken = await SecureStorage.Default.GetAsync(LegacyAuthTokenKey);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsService] SecureStorage.GetAsync failed: {ex}");
        }
        if (!string.IsNullOrEmpty(legacySecureToken))
        {
            try { SecureStorage.Default.Remove(LegacyAuthTokenKey); } catch { /* best-effort cleanup */ }
            return legacySecureToken;
        }

        // Even older migration: plain-text Preferences entry.
        var legacyPrefsToken = Preferences.Default.Get(LegacyAuthTokenKey, string.Empty);
        if (!string.IsNullOrEmpty(legacyPrefsToken))
        {
            Preferences.Default.Remove(LegacyAuthTokenKey);
            return legacyPrefsToken;
        }
        return string.Empty;
    }

    /// <summary>Sets the active profile's auth token (see SetProfileAuthTokenAsync).</summary>
    public static Task SetAuthTokenAsync(string value) => SetProfileAuthTokenAsync(ActiveProfileId, value);

    /// <summary>
    /// Writes one profile's auth token into the shared per-profile encrypted store. Throws if the
    /// file can't be written - callers should surface this to the user rather than silently
    /// swallowing it, since a failed save here means the token was NOT persisted.
    /// </summary>
    public static Task SetProfileAuthTokenAsync(string profileId, string value)
    {
        if (string.IsNullOrEmpty(profileId)) return Task.CompletedTask;

        var tokens = ReadEncryptedProfileTokens();
        if (string.IsNullOrEmpty(value)) tokens.Remove(profileId);
        else tokens[profileId] = value;
        WriteEncryptedProfileTokens(tokens);
        return Task.CompletedTask;
    }

    /// <summary>Best-effort: removes a profile's token when the profile itself is deleted.</summary>
    static void RemoveProfileAuthToken(string profileId)
    {
        try
        {
            var tokens = ReadEncryptedProfileTokens();
            if (tokens.Remove(profileId)) WriteEncryptedProfileTokens(tokens);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsService] RemoveProfileAuthToken failed: {ex}");
        }
    }

    static string AuthKeyFilePath => Path.Combine(FileSystem.AppDataDirectory, AuthKeyFileName);
    static string LegacyAuthTokenFilePath => Path.Combine(FileSystem.AppDataDirectory, LegacyAuthTokenFileName);
    static string ProfileTokensFilePath => Path.Combine(FileSystem.AppDataDirectory, ProfileTokensFileName);

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

    /// <summary>Decrypts and returns bytes from an AES-GCM blob file (nonce + tag + ciphertext), or null if the file doesn't exist yet.</summary>
    static byte[]? ReadEncryptedBlob(string path)
    {
        if (!File.Exists(path)) return null;

        var blob = File.ReadAllBytes(path);
        if (blob.Length < AesNonceSizeBytes + AesTagSizeBytes) return null;

        var nonce = blob.AsSpan(0, AesNonceSizeBytes);
        var tag = blob.AsSpan(AesNonceSizeBytes, AesTagSizeBytes);
        var ciphertext = blob.AsSpan(AesNonceSizeBytes + AesTagSizeBytes);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(GetOrCreateEncryptionKey(), AesTagSizeBytes);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    /// <summary>Encrypts and writes bytes to an AES-GCM blob file (or deletes the file if bytes is empty).</summary>
    static void WriteEncryptedBlob(string path, byte[] plaintext)
    {
        Directory.CreateDirectory(FileSystem.AppDataDirectory);

        if (plaintext.Length == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

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
        File.WriteAllBytes(path, blob);
        TryRestrictToOwnerOnly(path);
    }

    /// <summary>Decrypts and returns the legacy (pre-profiles) single token, or null if that file doesn't exist.</summary>
    static string? ReadLegacyEncryptedToken()
    {
        var plaintext = ReadEncryptedBlob(LegacyAuthTokenFilePath);
        return plaintext is null ? null : Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>Reads the profileId -> token map from the shared encrypted store (empty dictionary if it doesn't exist yet or is corrupt).</summary>
    static Dictionary<string, string> ReadEncryptedProfileTokens()
    {
        try
        {
            var plaintext = ReadEncryptedBlob(ProfileTokensFilePath);
            if (plaintext is null) return new Dictionary<string, string>();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(plaintext))
                ?? new Dictionary<string, string>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsService] Corrupt profile token store, resetting: {ex}");
            return new Dictionary<string, string>();
        }
    }

    static void WriteEncryptedProfileTokens(Dictionary<string, string> tokens)
        => WriteEncryptedBlob(ProfileTokensFilePath, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tokens)));


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

    /// <summary>The active profile's stable conversation id (see ServerProfile.ConversationId).</summary>
    public static string ConversationId => GetActiveProfile()?.ConversationId ?? string.Empty;

    public static void ResetConversation()
    {
        var active = GetActiveProfile();
        if (active is null) return;
        active.ConversationId = Guid.NewGuid().ToString();
        UpdateProfile(active);
    }

    /// <summary>Switches the active profile's conversation to an existing Copilot CLI session id (used when resuming a past session from the Sessions list).</summary>
    public static void SetConversation(string sessionId)
    {
        var active = GetActiveProfile();
        if (active is null) return;
        active.ConversationId = sessionId;
        UpdateProfile(active);
    }

    public static async Task<bool> IsConfiguredAsync()
    {
        var token = await GetAuthTokenAsync();
        return !string.IsNullOrWhiteSpace(ServerUrl) && !string.IsNullOrWhiteSpace(token);
    }
}
