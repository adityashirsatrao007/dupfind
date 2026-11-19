using System.Globalization;

namespace DupFind.ViewModels;

/// <summary>Human-readable byte sizes for the UI.</summary>
public static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(long bytes)
    {
        double value = Math.Abs(bytes);
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var text = value.ToString(unit == 0 ? "0" : "0.#", CultureInfo.InvariantCulture);
        return bytes < 0 ? $"-{text} {Units[unit]}" : $"{text} {Units[unit]}";
    }
}
