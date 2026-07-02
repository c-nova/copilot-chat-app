using CopilotChatApp.Services;

namespace CopilotChatApp.Views;

public partial class SettingsPage : ContentPage
{
    public SettingsPage()
    {
        InitializeComponent();
        ServerUrlEntry.Text = SettingsService.ServerUrl;
        AuthTokenEntry.Text = SettingsService.AuthToken;
    }

    async void OnSaveClicked(object? sender, EventArgs e)
    {
        SettingsService.ServerUrl = ServerUrlEntry.Text?.Trim() ?? string.Empty;
        SettingsService.AuthToken = AuthTokenEntry.Text?.Trim() ?? string.Empty;
        await DisplayAlert("Saved", "Settings saved.", "OK");
        await Navigation.PopAsync();
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
        if (!SettingsService.IsConfigured)
        {
            await DisplayAlert("Not configured", "Please set the server URL and token first, then Save.", "OK");
            return;
        }
        await Navigation.PushAsync(new McpServersPage());
    }
}
