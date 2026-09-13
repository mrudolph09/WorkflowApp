using System.Globalization;
using System.Windows.Data;
using MaterialDesignThemes.Wpf;
using Workflow.Models;

namespace Workflow.Converters;

/// <summary>Maps a phase status to its Material Design icon.</summary>
public sealed class PhaseStatusToIconKindConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            PhaseStatus.Active => PackIconKind.ProgressClock,
            PhaseStatus.Completed => PackIconKind.CheckCircle,
            _ => PackIconKind.CircleOutline,
        };

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
