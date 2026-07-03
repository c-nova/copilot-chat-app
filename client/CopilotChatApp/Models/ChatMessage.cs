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
        }
    }

    public bool IsUser => Role == ChatRole.User;
    public bool IsAssistant => Role == ChatRole.Assistant;
    public bool IsSystem => Role == ChatRole.System;
    public bool IsTool => Role == ChatRole.Tool;

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

    public event PropertyChangedEventHandler? PropertyChanged;

    void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
