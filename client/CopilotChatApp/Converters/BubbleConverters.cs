using System.Globalization;

namespace CopilotChatApp.Converters;

public class BoolToBubbleColorConverter : IValueConverter
{
    // Light backgrounds for both roles so default (dark-text) Markdown rendering reads well on either bubble.
    public static readonly Color UserColor = Color.FromArgb("#DCE6FF");
    public static readonly Color AssistantColor = Color.FromArgb("#E6E6E6");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? UserColor : AssistantColor;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class BoolToTextColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Colors.White : Colors.Black;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class BoolToAlignConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? LayoutOptions.End : LayoutOptions.Start;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class InvertedBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => !(value is true);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => !(value is true);
}

/// <summary>
/// Converts the message list's own width into a generous max-width for chat bubbles (90%, min 480) so
/// wide content like Markdown tables gets real room on desktop instead of always wrapping at a fixed 480px.
/// </summary>
public class WidthToBubbleMaxWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var width = value is double d ? d : 0;
        if (width <= 0) return 480d;
        return Math.Max(480d, width * 0.9);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Border color for the in-chat "find" bar's currently-focused match (see ChatMessage.IsSearchHighlighted / MainPage search).</summary>
public class BoolToSearchHighlightBorderConverter : IValueConverter
{
    public static readonly Color HighlightColor = Color.FromArgb("#FFA000");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? HighlightColor : Colors.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Supplies the height of each chat bubble's bottom spacer (see MainPage.xaml / PBI history on the
/// message-clipping bug). MarkdownView's height calculation only lands short on multi-line/complex
/// content (tables, lists, wrapped paragraphs) - a short single-line message (see
/// ChatMessage.NeedsBottomBuffer) never needs the buffer, and giving it one just reads as a stray
/// blank line underneath.
///
/// A flat per-message buffer (fontSize * a fixed ratio) turned out to still fall short on messages
/// with large Markdown tables - the shortfall seems to compound with how much wrapped/tabular
/// content there is, not just be a fixed "off by one line" amount. This scales extra buffer with a
/// rough count of table rows (lines starting with "|") on top of the base per-font-size amount, so
/// bigger tables get proportionally more headroom instead of all tables getting the same fixed one.
/// </summary>
public class MessageToBubbleBottomPaddingConverter : IValueConverter
{
    const double MinimalBottomPadding = 4d;
    const double BaseRatio = 2.4;
    /// <summary>Extra fontSize-multiples of buffer per table row beyond the first couple - see class summary.</summary>
    const double PerTableRowRatio = 0.5;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not CopilotChatApp.Models.ChatMessage msg || !msg.NeedsBottomBuffer)
        {
            return MinimalBottomPadding;
        }

        var fontSize = Application.Current?.Resources[CopilotChatApp.Services.SettingsService.ChatFontSizeResourceKey] is double fs
            ? fs
            : CopilotChatApp.Services.SettingsService.DefaultChatFontSize;

        var tableRowCount = 0;
        foreach (var line in msg.Text.Split('\n'))
        {
            if (line.TrimStart().StartsWith('|')) tableRowCount++;
        }
        var ratio = BaseRatio + Math.Max(0, tableRowCount - 2) * PerTableRowRatio;
        return fontSize * ratio;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
