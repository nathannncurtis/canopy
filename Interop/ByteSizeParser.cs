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
        const NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
        CultureInfo culture = CultureInfo.CurrentCulture;
        NumberFormatInfo format = culture.NumberFormat;
        bool hasDecimal = ContainsSeparator(numberPart, format.NumberDecimalSeparator);
        bool hasGroup = ContainsSeparator(numberPart, format.NumberGroupSeparator);
        bool validCurrentGrouping = !hasGroup || HasValidGrouping(numberPart, format);
        decimal number = 0;
        string currentNumber = validCurrentGrouping && hasGroup
            ? numberPart.Replace(format.NumberGroupSeparator, string.Empty, StringComparison.Ordinal)
            : numberPart;
        if ((!validCurrentGrouping || !decimal.TryParse(currentNumber, styles, culture,
                 out number)) &&
            // Never reinterpret a separator that has meaning in the user's culture
            // under invariant rules; ambiguous input must be corrected by the user.
            (hasDecimal || hasGroup || !decimal.TryParse(numberPart, styles,
                 CultureInfo.InvariantCulture, out number))) return false;
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

    static bool ContainsSeparator(string value, string separator) =>
        separator.Length != 0 && value.Contains(separator, StringComparison.Ordinal);

    static bool HasValidGrouping(string value, NumberFormatInfo format)
    {
        string unsigned = value.TrimStart('+', '-');
        int decimalIndex = format.NumberDecimalSeparator.Length == 0
            ? -1
            : unsigned.IndexOf(format.NumberDecimalSeparator, StringComparison.Ordinal);
        string integer = decimalIndex < 0 ? unsigned : unsigned[..decimalIndex];
        string fraction = decimalIndex < 0 ? string.Empty :
            unsigned[(decimalIndex + format.NumberDecimalSeparator.Length)..];
        if (ContainsSeparator(fraction, format.NumberGroupSeparator)) return false;
        string[] groups = integer.Split(format.NumberGroupSeparator, StringSplitOptions.None);
        if (groups.Length < 2 || groups[0].Length is < 1 or > 3) return false;
        return groups[0].All(char.IsDigit) &&
            groups.Skip(1).All(group => group.Length == 3 && group.All(char.IsDigit));
    }
}
