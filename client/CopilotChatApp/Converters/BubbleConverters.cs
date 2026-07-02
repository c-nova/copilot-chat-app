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
