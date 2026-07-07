using System.Collections.ObjectModel;
using CopilotChatApp.Services;

namespace CopilotChatApp.Views;

public partial class McpServersPage : ContentPage
{
    readonly ChatClientService _client = new();
    bool _connected;

    public ObservableCollection<McpServerSummary> Servers { get; } = new();

    public McpServersPage()
    {
        InitializeComponent();
        BindingContext = this;
        TransportPicker.SelectedIndex = 0;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        var active = SettingsService.GetActiveProfile();
        var name = active is null || string.IsNullOrWhiteSpace(active.Name) ? "(no name)" : active.Name;
        TargetServerLabel.Text = $"管理対象サーバー: {name}";
        await EnsureConnectedAsync();
        await RefreshAsync();
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        await _client.DisconnectAsync();
        _connected = false;
    }

    async Task EnsureConnectedAsync()
    {
        if (_connected) return;
        if (!await SettingsService.IsConfiguredAsync())
        {
            await DisplayAlert("Not configured", "Please set the server URL and token in Settings first.", "OK");
            await Navigation.PopAsync();
            return;
        }
        await _client.ConnectAsync(SettingsService.ServerUrl, await SettingsService.GetAuthTokenAsync());
        _connected = true;
    }

    async Task RefreshAsync()
    {
        LoadingIndicator.IsVisible = true;
        LoadingIndicator.IsRunning = true;
        try
        {
            var servers = await _client.ListMcpServersAsync();
            Servers.Clear();
            foreach (var s in servers) Servers.Add(s);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Failed to load MCP servers: {ex.Message}", "OK");
        }
        finally
        {
            LoadingIndicator.IsVisible = false;
            LoadingIndicator.IsRunning = false;
        }
    }

    void OnTransportChanged(object? sender, EventArgs e)
    {
        var isHttp = TransportPicker.SelectedIndex == 1;
        CommandLabel.IsVisible = !isHttp;
        CommandEntry.IsVisible = !isHttp;
        ArgsLabel.IsVisible = !isHttp;
        ArgsEntry.IsVisible = !isHttp;
        UrlLabel.IsVisible = isHttp;
        UrlEntry.IsVisible = isHttp;
    }

    async void OnAddClicked(object? sender, EventArgs e)
    {
        var name = NameEntry.Text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            await DisplayAlert("Missing name", "Please enter a server name.", "OK");
            return;
        }

        var isHttp = TransportPicker.SelectedIndex == 1;
        try
        {
            await EnsureConnectedAsync();
            if (isHttp)
            {
                var url = UrlEntry.Text?.Trim();
                if (string.IsNullOrEmpty(url))
                {
                    await DisplayAlert("Missing URL", "Please enter a server URL.", "OK");
                    return;
                }
                await _client.AddMcpServerAsync(name, "http", url: url);
            }
            else
            {
                var command = CommandEntry.Text?.Trim();
                if (string.IsNullOrEmpty(command))
                {
                    await DisplayAlert("Missing command", "Please enter a command.", "OK");
                    return;
                }
                var args = (ArgsEntry.Text ?? string.Empty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .ToList();
                await _client.AddMcpServerAsync(name, "stdio", command: command, args: args);
            }

            NameEntry.Text = string.Empty;
            CommandEntry.Text = string.Empty;
            ArgsEntry.Text = string.Empty;
            UrlEntry.Text = string.Empty;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Failed to add MCP server: {ex.Message}", "OK");
        }
    }

    async void OnRemoveClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string name }) return;
        var confirm = await DisplayAlert("Remove server", $"Remove MCP server \"{name}\"?", "Yes", "Cancel");
        if (!confirm) return;

        try
        {
            await EnsureConnectedAsync();
            await _client.RemoveMcpServerAsync(name);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Failed to remove MCP server: {ex.Message}", "OK");
        }
    }
}
