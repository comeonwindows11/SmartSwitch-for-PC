namespace SmartSwitch.App.Utilities;

public static class FormatUtilities
{
    private static readonly string[] Units = ["octets", "Ko", "Mo", "Go", "To"];

    public static string FormatBytes(long bytes)
    {
        var value = (double)Math.Max(0, bytes);
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < Units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{value:N0} {Units[unitIndex]}"
            : $"{value:N1} {Units[unitIndex]}";
    }
}
