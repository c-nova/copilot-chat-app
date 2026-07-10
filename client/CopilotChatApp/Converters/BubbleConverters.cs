using System.Globalization;
using System.Linq;
using CopilotChatApp.Models;

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

/// <summary>
/// Bubble background color for a whole ChatMessage: a distinct amber/orange for turns dispatched by
/// another session (see ChatMessage.IsFromOtherSession) so they're never mistaken for something this
/// session's own user typed, falling back to the normal user/assistant colors otherwise.
/// </summary>
public class MessageToBubbleColorConverter : IValueConverter
{
    public static readonly Color OtherSessionColor = Color.FromArgb("#FFE0B2");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ChatMessage { IsFromOtherSession: true })
        {
            return OtherSessionColor;
        }
        return value is ChatMessage { IsUser: true } ? BoolToBubbleColorConverter.UserColor : BoolToBubbleColorConverter.AssistantColor;
    }

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

/// <summary>True when the bound string is non-empty - used to hide a badge Label entirely rather than showing it with empty text (e.g. HomePage's per-server-profile badge).</summary>
public class StringNotEmptyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => !string.IsNullOrWhiteSpace(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
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
/// Superseded the earlier flat "count of table rows" heuristic: that undercounted badly whenever a
/// table's own cell text was long enough to *word-wrap* within its column - a table with few rows
/// but long cell text needed just as much extra headroom as a table with many short rows, and the
/// old heuristic had no way to know that without knowing the actual rendered width. This
/// IMultiValueConverter takes both the message and the bubble's actual available content width (see
/// MainPage.xaml's MultiBinding) so it can estimate, per table row, how many wrapped lines each
/// cell's text will actually take at the current font size and column width - and sum up the extra
/// wrapped lines across the whole table, not just count rows.
/// </summary>
public class MessageToBubbleBottomPaddingConverter : IMultiValueConverter
{
    const double MinimalBottomPadding = 4d;
    const double BaseRatio = 2.4;
    /// <summary>Rough line-height multiple (over the raw font size) used per estimated wrapped line - text lines render taller than their bare font size due to leading/line-spacing.</summary>
    const double LineHeightRatio = 1.35;
    /// <summary>Fallback per-column width (points) used when the bubble's real width isn't available (e.g. not yet laid out) - better to have *some* extra buffer than none.</summary>
    const double FallbackColumnWidth = 140d;
    /// <summary>Horizontal padding/border overhead (points) subtracted from the bubble's outer max width to approximate the actual content area MarkdownView has to lay text out in (Frame's own Padding="10,8" - see MainPage.xaml).</summary>
    const double BubbleHorizontalChrome = 20d;
    /// <summary>Rough extra chrome (points) per table column for its own internal cell padding/border, subtracted from the naive equal-share-of-width-per-column estimate.</summary>
    const double PerColumnChrome = 16d;
    /// <summary>Safety cap so a pathological input (huge table, tiny width) can't blow up the bubble's layout - a bit of extra blank space beyond this is preferable to an unbounded/broken height.</summary>
    const double MaxPadding = 2000d;

    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is not [CopilotChatApp.Models.ChatMessage { NeedsBottomBuffer: true } msg, ..])
        {
            return MinimalBottomPadding;
        }

        var fontSize = Application.Current?.Resources[CopilotChatApp.Services.SettingsService.ChatFontSizeResourceKey] is double fs
            ? fs
            : CopilotChatApp.Services.SettingsService.DefaultChatFontSize;

        var bubbleWidth = values.Length > 1 && values[1] is double w && w > 0 ? w : 0d;
        var contentWidth = bubbleWidth > BubbleHorizontalChrome ? bubbleWidth - BubbleHorizontalChrome : 0d;

        var extraWrappedLines = EstimateExtraWrappedTableLines(msg.Text, fontSize, contentWidth);

        var padding = fontSize * BaseRatio + fontSize * LineHeightRatio * extraWrappedLines;
        return Math.Min(padding, MaxPadding);
    }

    /// <summary>
    /// Walks the message's raw Markdown text looking for pipe-table blocks, and for each data row
    /// (skipping the `---`/`:--:` separator row) estimates how many wrapped lines the *tallest* cell
    /// in that row will actually take, given a naive equal per-column width share. Returns the sum of
    /// (estimated row height in lines - 1) across every row - i.e. purely the *extra* lines beyond
    /// what a single-line-per-row assumption would already budget for via BaseRatio.
    /// </summary>
    static double EstimateExtraWrappedTableLines(string text, double fontSize, double contentWidth)
    {
        double extraLines = 0;
        var currentTableRows = new List<string[]>();

        void FlushTable()
        {
            if (currentTableRows.Count == 0) return;
            var columnCount = currentTableRows.Max(r => r.Length);
            if (columnCount == 0) { currentTableRows.Clear(); return; }

            var perColumnWidth = contentWidth > 0
                ? Math.Max(24d, contentWidth / columnCount - PerColumnChrome)
                : FallbackColumnWidth;

            foreach (var cells in currentTableRows)
            {
                var maxCellLines = 1d;
                foreach (var cell in cells)
                {
                    var estimatedTextWidth = EstimateTextWidth(cell, fontSize);
                    var cellLines = Math.Max(1d, Math.Ceiling(estimatedTextWidth / perColumnWidth));
                    if (cellLines > maxCellLines) maxCellLines = cellLines;
                }
                extraLines += maxCellLines - 1;
            }
            currentTableRows.Clear();
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (!line.StartsWith('|'))
            {
                FlushTable();
                continue;
            }

            var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            // The header/body separator row (e.g. "---|:--:|--:") carries no real content/width.
            var isSeparatorRow = cells.Length > 0 && cells.All(c => c.Length > 0 && c.All(ch => ch is '-' or ':' or ' '));
            if (isSeparatorRow) continue;

            currentTableRows.Add(cells);
        }
        FlushTable();

        return extraLines;
    }

    /// <summary>Rough estimated rendered width (points) of a string at the given font size - wide (CJK/fullwidth) characters are treated as roughly a full em, everything else as a bit over half an em, which is a reasonable average for proportional Latin text.</summary>
    static double EstimateTextWidth(string text, double fontSize)
    {
        double width = 0;
        foreach (var ch in text)
        {
            width += IsWideCharacter(ch) ? fontSize * 0.95 : fontSize * 0.55;
        }
        return width;
    }

    /// <summary>True for characters commonly rendered at roughly double the width of Latin characters (Hiragana, Katakana, CJK Unified Ideographs, fullwidth forms, etc.) - a coarse but effective approximation for mixed Japanese/English cell text.</summary>
    static bool IsWideCharacter(char ch)
    {
        var code = (int)ch;
        return (code is >= 0x1100 and <= 0x115F)   // Hangul Jamo
            || (code is >= 0x2E80 and <= 0xA4CF)   // CJK radicals, Hiragana, Katakana, CJK Unified Ideographs, etc.
            || (code is >= 0xAC00 and <= 0xD7A3)   // Hangul Syllables
            || (code is >= 0xF900 and <= 0xFAFF)   // CJK Compatibility Ideographs
            || (code is >= 0xFF00 and <= 0xFF60)   // Fullwidth forms
            || (code is >= 0xFFE0 and <= 0xFFE6);  // Fullwidth signs
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
