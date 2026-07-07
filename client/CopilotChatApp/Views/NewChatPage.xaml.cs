using System.Collections.ObjectModel;
using CopilotChatApp.Services;

namespace CopilotChatApp.Views;

/// <summary>
/// New-session folder picker, opened from Home's "New chat" button: browse to a folder within the
/// server's configured BROWSE_ROOTS (or clone a git repo into the currently browsed folder), then
/// start a fresh chat rooted there - mirrors VS Code Copilot Chat's "Select Folder / Clone
/// Repository" flow. Reuses the caller's already-connected ChatClientService (typically HomePage's)
/// instead of opening a second connection.
/// </summary>
public partial class NewChatPage : ContentPage
{
    readonly ChatClientService _client;
    string? _currentPath;
    string? _parentPath;
    List<string> _roots = new();

    public ObservableCollection<FsEntry> Entries { get; } = new();

    public NewChatPage(ChatClientService client)
    {
        InitializeComponent();
        BindingContext = this;
        _client = client;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync(null);
    }

    async Task LoadAsync(string? path)
    {
        try
        {
            var result = await _client.ListDirAsync(path);
            _currentPath = result.Path;
            _parentPath = result.ParentPath;
            _roots = result.Roots ?? new List<string>();

            Entries.Clear();
            foreach (var entry in result.Entries ?? new List<FsEntry>())
            {
                Entries.Add(entry);
            }

            PathLabel.Text = _currentPath ?? "開始フォルダを選んでください";
            UpButton.IsVisible = _currentPath is not null && _parentPath is not null;
            RootsButton.IsVisible = _currentPath is not null && _parentPath is null && _roots.Count > 1;
            UseCurrentFolderButton.IsVisible = _currentPath is not null;
            // Only one configured root strongly suggests BROWSE_ROOTS was never set (falls back to
            // just the default workspace - see server/src/config.ts) - nudge towards configuring it
            // rather than leaving the user wondering why there's nothing else to pick.
            BrowseRootsHintLabel.IsVisible = _roots.Count <= 1;
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"フォルダの読み込みに失敗しました: {ex.Message}", "OK");
        }
    }

    async void OnEntryTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not FsEntry entry) return;
        await LoadAsync(entry.Path);
    }

    async void OnUpClicked(object? sender, EventArgs e) => await LoadAsync(_parentPath);

    async void OnRootsClicked(object? sender, EventArgs e) => await LoadAsync(null);

    async void OnUseCurrentFolderClicked(object? sender, EventArgs e)
    {
        if (_currentPath is null) return;
        await StartNewChatAsync(_currentPath);
    }

    async void OnUseDefaultClicked(object? sender, EventArgs e) => await StartNewChatAsync(null);

    async void OnCloneClicked(object? sender, EventArgs e)
    {
        if (_currentPath is null)
        {
            await DisplayAlert("フォルダを選択してください", "クローン先の親フォルダをまず選んでください。", "OK");
            return;
        }

        var repoUrl = await DisplayPromptAsync(
            "Gitリポジトリをクローン",
            $"クローン先: {_currentPath}\n\nリポジトリのURLを入力してください",
            placeholder: "https://github.com/example/repo.git");
        if (string.IsNullOrWhiteSpace(repoUrl)) return;

        try
        {
            var clonedPath = await _client.GitCloneAsync(_currentPath, repoUrl.Trim());
            await StartNewChatAsync(clonedPath);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Clone failed", ex.Message, "OK");
        }
    }

    async Task StartNewChatAsync(string? cwd)
    {
        SettingsService.ResetConversation();
        await Navigation.PushAsync(cwd is null ? new MainPage() : new MainPage(cwd));
        Navigation.RemovePage(this);
    }
}
