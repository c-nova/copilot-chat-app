using CopilotChatApp.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotChatApp;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		// Apply the user's saved chat font size (or the default) so it's in effect before any page loads.
		SettingsService.ApplyChatFontSizeResource();
		// Kick off the local-network-access permission prompt (iOS/iPadOS) as early as possible,
		// well before the chat WebSocket's own connect attempt would otherwise trigger it. See
		// LocalNetworkAccessTrigger for why.
		LocalNetworkAccessTrigger.FireAndForget();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new AppShell());
		// Re-lock the biometric gate whenever the app leaves the foreground (backgrounded/minimized),
		// so the next resume re-prompts for Face ID / Touch ID / Windows Hello / passcode. See PBI-009.
		window.Stopped += (_, _) => BiometricGateService.Lock();
		return window;
	}
}