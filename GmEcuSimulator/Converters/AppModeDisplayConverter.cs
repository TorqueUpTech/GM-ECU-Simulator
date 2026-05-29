using Common;
using System.Globalization;
using System.Windows.Data;

namespace GmEcuSimulator.Converters;

// One-way AppMode -> human-readable string for the mode-selector ComboBox.
// Delegates to AppModeExtensions.DisplayName so the wording stays in one place.
[ValueConversion(typeof(AppMode), typeof(string))]
public sealed class AppModeDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is AppMode m ? m.DisplayName() : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
