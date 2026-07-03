namespace CopilotChatApp.Models;

/// <summary>
/// An image staged in the composer (via clipboard paste) waiting to be sent alongside the next
/// message. Held in memory as raw bytes until send time, when it's base64-encoded onto the wire
/// (PBI-019 - the CLI's --attachment flag only accepts filesystem paths, so the server writes
/// these to a temp file; the client never has to touch disk itself).
/// </summary>
public class PendingAttachment
{
    public byte[] Bytes { get; }
    public string MimeType { get; }
    public ImageSource Preview { get; }

    public PendingAttachment(byte[] bytes, string mimeType)
    {
        Bytes = bytes;
        MimeType = mimeType;
        Preview = ImageSource.FromStream(() => new MemoryStream(bytes));
    }
}
