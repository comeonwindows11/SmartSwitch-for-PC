using System.Globalization;
using System.Windows.Data;
using SmartSwitch.App.Utilities;

namespace SmartSwitch.App.Converters;

public sealed class ByteSizeConverter : IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        value is long bytes ? FormatUtilities.FormatBytes(bytes) : "0 octet";

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}
