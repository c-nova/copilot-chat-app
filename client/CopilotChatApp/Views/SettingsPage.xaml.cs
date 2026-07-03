using CopilotChatApp.Services;

namespace CopilotChatApp.Views;

public partial class SettingsPage : ContentPage
{
    readonly ChatClientService _client;

    public SettingsPage(ChatClientService client)
    {
        InitializeComponent();
        _client = client;
        ServerUrlEntry.Text = SettingsService.ServerUrl;
        FontSizeSlider.Value = SettingsService.ChatFontSize;
        UpdateFontSizeLabel(SettingsService.ChatFontSize);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        AuthTokenEntry.Text = await SettingsService.GetAuthTokenAsync();
    }

    async void OnSaveClicked(object? sender, EventArgs e)
    {
        SettingsService.ServerUrl = ServerUrlEntry.Text?.Trim() ?? string.Empty;
        try
        {
            await SettingsService.SetAuthTokenAsync(AuthTokenEntry.Text?.Trim() ?? string.Empty);
        }
        catch (Exception ex)
        {
            // Surface secure-storage failures instead of silently losing the token (e.g. Keychain
            // access denied on Mac Catalyst - see PBI-013).
            await DisplayAlert("Couldn't save Auth Token", $"The token could not be saved to secure storage: {ex.Message}", "OK");
            return;
        }

        await TryConnectAsync();
    }

    /// <summary>
    /// Connects right here, right after Save, rather than waiting for whatever screen the user
    /// lands on next to discover the server. On iOS/iPadOS this is also the most predictable place
    /// to surface the local-network-access permission prompt (NSLocalNetworkUsageDescription): the
    /// user just explicitly typed in a server address, so they're primed to notice and answer a
    /// system dialog that pops up right now instead of it racing some other page's own connect.
    ///
    /// Even so, our retry budget can run out before the user notices and answers that system
    /// prompt. Offering Retry (rather than just an OK) on failure lets them try again immediately
    /// after answering it, without having to re-open Settings and re-tap Save.
    /// </summary>
    async Task TryConnectAsync()
    {
        SaveButton.IsEnabled = false;
        ConnectingIndicator.IsVisible = true;
        ConnectingIndicator.IsRunning = true;
        try
        {
            await _client.ConnectWithRetryAsync(SettingsService.ServerUrl, await SettingsService.GetAuthTokenAsync());
            SaveButton.IsEnabled = true;
            ConnectingIndicator.IsVisible = false;
            ConnectingIndicator.IsRunning = false;
            await DisplayAlert("Saved", "Settings saved and connected to the server.", "OK");
            await Navigation.PopAsync();
        }
        catch (Exception ex)
        {
            SaveButton.IsEnabled = true;
            ConnectingIndicator.IsVisible = false;
            ConnectingIndicator.IsRunning = false;
            var retry = await DisplayAlert("Saved, but couldn't connect", $"Settings were saved, but connecting to the server failed: {ex.Message}", "Retry", "Cancel");
            if (retry)
            {
                await TryConnectAsync();
            }
        }
    }

    async void OnNewConversationClicked(object? sender, EventArgs e)
    {
        var confirm = await DisplayAlert("New conversation", "This will start a fresh Copilot session on next message.", "Yes", "Cancel");
        if (!confirm) return;
        SettingsService.ResetConversation();
        await DisplayAlert("Done", "A new conversation will start with your next message.", "OK");
    }

    async void OnMcpServersClicked(object? sender, EventArgs e)
    {
        if (!await SettingsService.IsConfiguredAsync())
        {
            await DisplayAlert("Not configured", "Please set the server URL and token first, then Save.", "OK");
            return;
        }
        await Navigation.PushAsync(new McpServersPage());
    }

    // Applied immediately (and persisted) as the user drags, so the sample text below updates live
    // without needing to tap Save - font size isn't a secret worth guarding behind an explicit save.
    void OnFontSizeChanged(object? sender, ValueChangedEventArgs e)
    {
        SettingsService.ChatFontSize = e.NewValue;
        UpdateFontSizeLabel(SettingsService.ChatFontSize);
    }

    void UpdateFontSizeLabel(double size) => FontSizeLabel.Text = $"{size:0}";
}
