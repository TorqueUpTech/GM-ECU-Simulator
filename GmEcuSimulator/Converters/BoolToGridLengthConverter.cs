using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GmEcuSimulator.Converters;

// Bool -> GridLength. Lets a RowDefinition.Height be driven by a VM flag.
// ConverterParameter is "trueLen|falseLen" where each token is "*", "Auto",
// or a number (pixel). Used for the Selected ECU inspector: PID row is "*"
// when the PID grid is visible (fills remaining pane space) and "Auto" when
// it is hidden (collapses to zero so the form fields don't float above empty
// space).
[ValueConversion(typeof(bool), typeof(GridLength))]
public sealed class BoolToGridLengthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isTrue = value is bool b && b;
        var parts = (parameter as string)?.Split('|') ?? new[] { "*", "Auto" };
        var pick  = isTrue ? parts[0] : (parts.Length > 1 ? parts[1] : "Auto");
        return Parse(pick);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;

    private static GridLength Parse(string s) => s.Trim() switch
    {
        "*"    => new GridLength(1, GridUnitType.Star),
        "Auto" => GridLength.Auto,
        _ when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
               => new GridLength(d, GridUnitType.Pixel),
        _      => GridLength.Auto,
    };
}
