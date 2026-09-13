using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Workflow.Models;

namespace Workflow.Converters;

/// <summary>Maps a phase status to its indicator colour: grey, yellow, green.</summary>
public sealed class PhaseStatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Pending = Freeze(Color.FromRgb(0x9E, 0x9E, 0x9E));
    private static readonly SolidColorBrush Active = Freeze(Color.FromRgb(0xFB, 0xC0, 0x2D));
    private static readonly SolidColorBrush Completed = Freeze(Color.FromRgb(0x43, 0xA0, 0x47));

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            PhaseStatus.Active => Active,
            PhaseStatus.Completed => Completed,
            _ => Pending,
        };

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
