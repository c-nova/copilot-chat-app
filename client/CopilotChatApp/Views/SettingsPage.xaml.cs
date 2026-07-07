using CopilotChatApp.Models;
using CopilotChatApp.Services;

namespace CopilotChatApp.Views;

public partial class SettingsPage : ContentPage
{
    readonly ChatClientService _client;

    /// <summary>The profile currently loaded into the Name/URL/Token fields below the list. Null
    /// means "adding a brand-new server" rather than editing an existing one.</summary>
    ServerProfile? _editingProfile;

    public SettingsPage(ChatClientService client)
    {
        InitializeComponent();
        _client = client;
        FontSizeSlider.Value = SettingsService.ChatFontSize;
        UpdateFontSizeLabel(SettingsService.ChatFontSize);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        LoadProfilesList();

        var active = SettingsService.GetActiveProfile();
        if (active is not null)
        {
            await SelectProfileForEditingAsync(active);
        }
        else
        {
            StartAddingNewProfile();
        }
    }

    /// <summary>Rebuilds the list of configured server profiles (see ProfilesList in the XAML) -
    /// plain code-behind-built rows rather than a CollectionView, since a ScrollView + CollectionView
    /// combination has its own known height/virtualization quirks on some platforms and profile
    /// counts here are always small enough that virtualization buys nothing.</summary>
    void LoadProfilesList()
    {
        ProfilesList.Children.Clear();
        var activeId = SettingsService.ActiveProfileId;

        foreach (var profile in SettingsService.GetProfiles())
        {
            var isActive = profile.Id == activeId;

            var nameLabel = new Label
            {
                Text = (isActive ? "✓ " : "") + (string.IsNullOrWhiteSpace(profile.Name) ? "(no name)" : profile.Name),
                FontAttributes = isActive ? FontAttributes.Bold : FontAttributes.None,
            };
            var urlLabel = new Label
            {
                Text = string.IsNullOrWhiteSpace(profile.Url) ? "(no URL set)" : profile.Url,
                FontSize = 12,
                TextColor = Colors.Gray,
                LineBreakMode = LineBreakMode.TailTruncation,
            };
            var textStack = new VerticalStackLayout { Spacing = 2, Children = { nameLabel, urlLabel } };

            var deleteButton = new Button
            {
                Text = "削除",
                TextColor = Colors.Red,
                BackgroundColor = Colors.Transparent,
                VerticalOptions = LayoutOptions.Center,
            };
            deleteButton.Clicked += async (_, _) => await OnDeleteProfileClickedAsync(profile);

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitionCollection { new(GridLength.Star), new(GridLength.Auto) } };
            grid.Add(textStack, 0);
            grid.Add(deleteButton, 1);

            var border = new Border
            {
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
                Stroke = isActive ? Colors.CornflowerBlue : Colors.LightGray,
                StrokeThickness = isActive ? 2 : 1,
                Padding = 10,
                Content = grid,
            };
            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) => await SelectProfileForEditingAsync(profile);
            border.GestureRecognizers.Add(tap);

            ProfilesList.Children.Add(border);
        }
    }

    /// <summary>Tapping a row in the profile list both loads it into the edit form below AND
    /// immediately switches the active profile (rather than waiting for Save) - the ✓/highlight in
    /// the list is tied to ActiveProfileId, so without this, tapping a different row visually did
    /// nothing until Save was pressed, which read as "selection isn't working".</summary>
    async Task SelectProfileForEditingAsync(ServerProfile profile)
    {
        _editingProfile = profile;
        SettingsService.ActiveProfileId = profile.Id;
        EditingProfileLabel.Text = $"編集中: {(string.IsNullOrWhiteSpace(profile.Name) ? "(no name)" : profile.Name)}";
        ProfileNameEntry.Text = profile.Name;
        ServerUrlEntry.Text = profile.Url;
        AuthTokenEntry.Text = await SettingsService.GetProfileAuthTokenAsync(profile.Id);
        LoadProfilesList();
    }

    void StartAddingNewProfile()
    {
        _editingProfile = null;
        EditingProfileLabel.Text = "新しいサーバーを追加";
        ProfileNameEntry.Text = string.Empty;
        ServerUrlEntry.Text = string.Empty;
        AuthTokenEntry.Text = string.Empty;
    }

    void OnAddProfileClicked(object? sender, EventArgs e) => StartAddingNewProfile();

    async Task OnDeleteProfileClickedAsync(ServerProfile profile)
    {
        var confirm = await DisplayAlert("サーバーを削除", $"「{profile.Name}」をこのアプリの設定から削除しますか?(サーバー自体やセッション履歴は消えません)", "削除", "キャンセル");
        if (!confirm) return;

        var wasEditing = _editingProfile?.Id == profile.Id;
        SettingsService.RemoveProfile(profile.Id);
        LoadProfilesList();

        if (wasEditing)
        {
            var active = SettingsService.GetActiveProfile();
            if (active is not null) await SelectProfileForEditingAsync(active);
            else StartAddingNewProfile();
        }
    }

    async void OnSaveClicked(object? sender, EventArgs e)
    {
        var name = ProfileNameEntry.Text?.Trim();
        if (string.IsNullOrEmpty(name)) name = "Server";
        var url = ServerUrlEntry.Text?.Trim() ?? string.Empty;

        ServerProfile profile;
        if (_editingProfile is null)
        {
            profile = SettingsService.AddProfile(name, url);
        }
        else
        {
            profile = _editingProfile;
            profile.Name = name;
            profile.Url = url;
            SettingsService.UpdateProfile(profile);
        }
        // Saving from this screen means "use this server now" - matches the pre-multi-server Save
        // button's behavior of immediately switching to whatever was just entered.
        SettingsService.ActiveProfileId = profile.Id;
        _editingProfile = profile;

        try
        {
            await SettingsService.SetProfileAuthTokenAsync(profile.Id, AuthTokenEntry.Text?.Trim() ?? string.Empty);
        }
        catch (Exception ex)
        {
            // Surface secure-storage failures instead of silently losing the token (e.g. Keychain
            // access denied on Mac Catalyst - see PBI-013).
            await DisplayAlert("Couldn't save Auth Token", $"The token could not be saved to secure storage: {ex.Message}", "OK");
            return;
        }

        LoadProfilesList();
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
