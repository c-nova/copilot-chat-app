using Plugin.Maui.Biometric;

namespace CopilotChatApp.Services;

/// <summary>
/// Gates access to the SecureStorage-held AuthToken behind a device-local check (Face ID / Touch ID /
/// fingerprint / Windows Hello, or the device passcode/PIN as a fallback via <c>AllowPasswordAuth</c>).
/// This does NOT replace the server's Bearer Token auth - it only protects against someone picking up
/// an unlocked/stolen device and opening the app to reach the saved token.
///
/// Design decisions (see PBI-009):
/// - Fails OPEN if the device has no biometrics/passcode enrolled or no compatible hardware: this app
///   is for a single person's private use, so we don't want to lock people out of their own device.
/// - Only re-prompts once per foreground session: on app start, and again after returning from the
///   background (see <see cref="Lock"/>, called from the Window's Stopped event in App.xaml.cs).
///   We don't re-prompt on every message send - that would be an unreasonable UX tax for a same-device
///   feature that's already gated at the OS/app level.
/// </summary>
public static class BiometricGateService
{
    /// <summary>Raised when a previously-unlocked session gets locked again (app went to background).</summary>
    public static event Action? Locked;

    static bool _unlockedForForegroundSession;

    /// <summary>Call when the app leaves the foreground, so the next resume re-prompts.</summary>
    public static void Lock()
    {
        if (!_unlockedForForegroundSession) return;
        _unlockedForForegroundSession = false;
        Locked?.Invoke();
    }

    /// <summary>
    /// Ensures the current foreground session is unlocked, prompting for biometrics/passcode if needed.
    /// Returns true if access should be allowed (either already unlocked, authentication succeeded, or
    /// the device has nothing to gate with - fail-open by design).
    /// </summary>
    public static async Task<bool> EnsureUnlockedAsync(CancellationToken ct = default)
    {
        if (_unlockedForForegroundSession) return true;

        var biometric = BiometricAuthenticationService.Default;
        if (!biometric.IsPlatformSupported)
        {
            _unlockedForForegroundSession = true;
            return true;
        }

        BiometricHwStatus status;
        try
        {
            status = await biometric.GetAuthenticationStatusAsync();
        }
        catch
        {
            // Unexpected platform error - fail open rather than locking the user out.
            _unlockedForForegroundSession = true;
            return true;
        }

        if (status != BiometricHwStatus.Success)
        {
            // No biometrics/passcode enrolled, no hardware, or otherwise unavailable: fail open.
            _unlockedForForegroundSession = true;
            return true;
        }

        var request = new AuthenticationRequest
        {
            Title = "Unlock Copilot Chat",
            Subtitle = "Authenticate to access your saved server connection",
            NegativeText = "Cancel",
            AllowPasswordAuth = true, // let the device passcode/PIN work as a fallback, not just biometrics
        };

        try
        {
            var result = await biometric.AuthenticateAsync(request, ct);
            _unlockedForForegroundSession = result.Status == BiometricResponseStatus.Success;
            return _unlockedForForegroundSession;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
