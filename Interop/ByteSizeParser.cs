using System.Globalization;

namespace SizeMonitor.Interop;

public static class ByteSizeParser
{
    static readonly IReadOnlyDictionary<string, decimal> Multipliers =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            [""] = 1,
            ["B"] = 1,
            ["KB"] = 1_000m,
            ["MB"] = 1_000_000m,
            ["GB"] = 1_000_000_000m,
            ["TB"] = 1_000_000_000_000m,
            ["PB"] = 1_000_000_000_000_000m,
            ["KIB"] = 1_024m,
            ["MIB"] = 1_048_576m,
            ["GIB"] = 1_073_741_824m,
            ["TIB"] = 1_099_511_627_776m,
            ["PIB"] = 1_125_899_906_842_624m,
        };

    public static bool TryParse(string? text, out ulong bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string value = text.Trim();
        int unitStart = 0;
        while (unitStart < value.Length &&
               (char.IsDigit(value[unitStart]) || char.IsWhiteSpace(value[unitStart]) ||
                value[unitStart] is '.' or ',' or '+' or '-'))
            unitStart++;

        string numberPart = value[..unitStart].Trim();
        string unitPart = value[unitStart..].Trim();
        if (!Multipliers.TryGetValue(unitPart, out decimal multiplier)) return false;
        if (!decimal.TryParse(numberPart, NumberStyles.Number, CultureInfo.CurrentCulture,
                out decimal number) &&
            !decimal.TryParse(numberPart, NumberStyles.Number, CultureInfo.InvariantCulture,
                out number)) return false;
        if (number < 0) return false;

        try
        {
            decimal rounded = decimal.Round(number * multiplier, 0, MidpointRounding.AwayFromZero);
            if (rounded > ulong.MaxValue) return false;
            bytes = (ulong)rounded;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    public static ulong Parse(string text) =>
        TryParse(text, out ulong bytes)
            ? bytes
            : throw new FormatException($"'{text}' is not a valid byte size.");
}
