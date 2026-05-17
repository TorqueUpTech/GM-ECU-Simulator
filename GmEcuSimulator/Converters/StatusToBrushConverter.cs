using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace GmEcuSimulator.Converters;

// Maps a status string onto a semantic brush so a status pill can light up
// green / amber / red without any extra plumbing in the ViewModel. Used by
// both the J2534 registration pill ("Registered ...", "Not registered",
// "Status check failed: ...") and the ECU security pill ("Locked",
// "Unlocked (level N)", "Locked out - ..."). Resolves brushes from
// App.Current.Resources so it honours the active theme.
[ValueConversion(typeof(string), typeof(Brush))]
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string ?? string.Empty;
        // Order matters: "Locked out" must beat "Locked", and "Not registered"
        // must beat "Registered". Check the more-specific prefixes first.
        var key =
            s.StartsWith("Locked out", StringComparison.OrdinalIgnoreCase)  ? "Status.ErrorBrush"
          : s.StartsWith("Unlocked", StringComparison.OrdinalIgnoreCase)    ? "Status.SuccessBrush"
          : s.StartsWith("Locked", StringComparison.OrdinalIgnoreCase)      ? "Status.WarningBrush"
          : s.StartsWith("Not", StringComparison.OrdinalIgnoreCase)         ? "Status.WarningBrush"
          : s.StartsWith("Registered", StringComparison.OrdinalIgnoreCase)  ? "Status.SuccessBrush"
          : s.StartsWith("OK", StringComparison.OrdinalIgnoreCase)          ? "Status.SuccessBrush"
          : s.Contains("failed", StringComparison.OrdinalIgnoreCase)        ? "Status.ErrorBrush"
          :                                                                   "Text.TertiaryBrush";
        return (Application.Current?.Resources[key] as Brush) ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
