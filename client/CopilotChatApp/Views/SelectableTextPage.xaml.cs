using Microsoft.Maui.Controls;

namespace CopilotChatApp.Views;

/// <summary>
/// Shows one chat message's raw text in a read-only Editor so the user can drag-select any
/// portion (not just the whole message) and copy it via the native text-selection context menu -
/// something the Markdown-rendered chat bubble itself doesn't support.
/// </summary>
public partial class SelectableTextPage : ContentPage
{
    readonly string _text;

    public SelectableTextPage(string text)
    {
        InitializeComponent();
        _text = text;
        TextEditor.Text = text;
    }

    async void OnCopyAllClicked(object? sender, EventArgs e)
    {
        await Clipboard.SetTextAsync(_text);
    }
}
