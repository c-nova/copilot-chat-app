using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CopilotChatApp.Models;

public enum ChatRole
{
    User,
    Assistant,
    System,
    Tool
}

public class ChatMessage : INotifyPropertyChanged
{
    string _text = string.Empty;

    public ChatRole Role { get; set; }

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NeedsBottomBuffer));
        }
    }

    /// <summary>
    /// True when this is a non-user message spanning more than one line. MarkdownView's custom-drawn
    /// height calculation lands about one line short specifically on multi-line/complex assistant
    /// content (tables, lists, wrapped paragraphs). User messages are excluded even when they happen to
    /// span multiple lines (e.g. a sentence plus a pasted URL on its own line) - they're plain typed
    /// text with none of MarkdownView's complex layout, so they never trip the bug, and the buffer would
    /// just read as a stray blank line underneath.
    /// </summary>
    public bool NeedsBottomBuffer => Role != ChatRole.User && _text.Contains('\n');

    public bool IsUser => Role == ChatRole.User;
    public bool IsAssistant => Role == ChatRole.Assistant;
    public bool IsSystem => Role == ChatRole.System;
    public bool IsTool => Role == ChatRole.Tool;

    /// <summary>
    /// True when this turn was dispatched by another session via the session-control MCP's
    /// run_turn_on_session tool rather than typed by this session's own human user - drives a
    /// distinct bubble color + "Message from other Agent" badge so it's never confused for
    /// something the user themselves sent (see server/src/sessionMeta.ts).
    /// </summary>
    public bool IsFromOtherSession { get; set; }

    /// <summary>Full tool call arguments/status text shown when a tool-activity bubble is tapped.</summary>
    public string? ToolDetail { get; set; }

    /// <summary>Images pasted alongside this message's text (PBI-019), shown as thumbnails in the bubble.</summary>
    public List<PendingAttachment>? Attachments { get; set; }

    public bool HasAttachments => Attachments is { Count: > 0 };

    bool _isRunning;

    /// <summary>True while a tool call is in progress (drives the spinner next to the tool row).</summary>
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (_isRunning == value) return;
            _isRunning = value;
            OnPropertyChanged();
        }
    }

    bool _isCopied;

    /// <summary>Briefly true right after the user copies this message's text, to show a "Copied" confirmation.</summary>
    public bool IsCopied
    {
        get => _isCopied;
        set
        {
            if (_isCopied == value) return;
            _isCopied = value;
            OnPropertyChanged();
        }
    }

    bool _isSearchHighlighted;

    /// <summary>True for the single message currently focused by the in-chat "find" bar (MainPage) -
    /// drives a highlighted border on its bubble so the user can see which of possibly several matches
    /// they've scrolled to.</summary>
    public bool IsSearchHighlighted
    {
        get => _isSearchHighlighted;
        set
        {
            if (_isSearchHighlighted == value) return;
            _isSearchHighlighted = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
