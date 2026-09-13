using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;
using Workflow.Converters;
using Workflow.Models;

namespace Workflow.Tests;

public class ConverterTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(PhaseStatus.Pending, "#FF9E9E9E")]
    [InlineData(PhaseStatus.Active, "#FFFBC02D")]
    [InlineData(PhaseStatus.Completed, "#FF43A047")]
    public void PhaseStatusToBrush_MapsEveryStatus(PhaseStatus status, string expected)
    {
        var converter = new PhaseStatusToBrushConverter();

        var brush = Assert.IsType<SolidColorBrush>(converter.Convert(status, typeof(Brush), null, Culture));

        Assert.Equal(expected, brush.Color.ToString(Culture));
    }

    [Fact]
    public void PhaseStatusToBrush_FallsBackToGreyForNullAndUnset()
    {
        var converter = new PhaseStatusToBrushConverter();

        var fromNull = Assert.IsType<SolidColorBrush>(converter.Convert(null, typeof(Brush), null, Culture));
        var fromUnset = Assert.IsType<SolidColorBrush>(
            converter.Convert(DependencyProperty.UnsetValue, typeof(Brush), null, Culture));

        Assert.Equal("#FF9E9E9E", fromNull.Color.ToString(Culture));
        Assert.Equal("#FF9E9E9E", fromUnset.Color.ToString(Culture));
    }

    [Theory]
    [InlineData(PhaseStatus.Pending, PackIconKind.CircleOutline)]
    [InlineData(PhaseStatus.Active, PackIconKind.ProgressClock)]
    [InlineData(PhaseStatus.Completed, PackIconKind.CheckCircle)]
    public void PhaseStatusToIconKind_MapsEveryStatus(PhaseStatus status, PackIconKind expected)
    {
        var converter = new PhaseStatusToIconKindConverter();

        Assert.Equal(expected, converter.Convert(status, typeof(PackIconKind), null, Culture));
    }

    [Fact]
    public void PhaseStatusToIconKind_FallsBackForNull()
    {
        var converter = new PhaseStatusToIconKindConverter();

        Assert.Equal(PackIconKind.CircleOutline, converter.Convert(null, typeof(PackIconKind), null, Culture));
    }

    [Theory]
    [InlineData(true, Visibility.Collapsed)]
    [InlineData(false, Visibility.Visible)]
    public void InverseBooleanToVisibility_Inverts(bool value, Visibility expected)
    {
        var converter = new InverseBooleanToVisibilityConverter();

        Assert.Equal(expected, converter.Convert(value, typeof(Visibility), null, Culture));
    }

    [Fact]
    public void InverseBooleanToVisibility_TreatsNullAsFalse()
    {
        var converter = new InverseBooleanToVisibilityConverter();

        Assert.Equal(Visibility.Visible, converter.Convert(null, typeof(Visibility), null, Culture));
    }

    [Fact]
    public void ConvertBack_IsNotSupportedOnAnyConverter()
    {
        Assert.Throws<NotSupportedException>(
            () => new PhaseStatusToBrushConverter().ConvertBack(null, typeof(object), null, Culture));
        Assert.Throws<NotSupportedException>(
            () => new PhaseStatusToIconKindConverter().ConvertBack(null, typeof(object), null, Culture));
        Assert.Throws<NotSupportedException>(
            () => new InverseBooleanToVisibilityConverter().ConvertBack(null, typeof(object), null, Culture));
    }
}
