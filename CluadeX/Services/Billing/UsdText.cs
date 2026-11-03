using System.Globalization;

namespace CluadeX.Services.Billing;

/// <summary>Dollars on the Billing page: shown without rounding a cheap request to zero, and read back from what the
/// user typed off a provider's billing page.</summary>
public static class UsdText
{
    /// <summary>Keep sub-cent API costs visible instead of rounding a cheap request to zero.</summary>
    public static string Format(decimal v) =>
        "$" + (Math.Abs(v) < 0.01m && v != 0 ? v.ToString("0.########", CultureInfo.InvariantCulture) : v.ToString("N2", CultureInfo.InvariantCulture));

    /// <summary>
    /// A non-negative amount, read the same under every culture. "25", "$12.50", "12,50", "1,000", "1,234.56", and the
    /// comma-decimal habit "1.234,56" (with both marks the last one is the decimal point, the other groups thousands).
    /// Refused instead of guessed: separators in impossible places ("1,2,3", "1,23.45", "1.234.56,7", "1,234,56"), a
    /// lone "1.000" (one dollar, or one thousand?), "abc" and "-3".
    /// </summary>
    public static bool TryParse(string? text, out decimal usd)
    {
        usd = 0;
        string t = (text ?? "").Trim().TrimStart('$').Replace(" ", "").Replace("\u00A0", "").Replace("\u202F", "");
        if (t.Length == 0 || !t.All(ch => char.IsAsciiDigit(ch) || ch is ',' or '.')) return false;

        int lastDot = t.LastIndexOf('.'), lastComma = t.LastIndexOf(',');
        string whole, fraction = "";
        if (lastDot >= 0 && lastComma >= 0)
        {
            // Both marks: the last one is the decimal point and appears once; everything before it groups thousands.
            int at = Math.Max(lastDot, lastComma);
            char dec = t[at], group = dec == '.' ? ',' : '.';
            fraction = t[(at + 1)..];
            if (fraction.Length == 0 || t.IndexOf(dec) != at) return false;
            var groups = t[..at].Split(group);
            if (!IsGrouped(groups)) return false;
            whole = string.Concat(groups);
        }
        else if (lastDot >= 0 || lastComma >= 0)
        {
            char sep = lastComma >= 0 ? ',' : '.';
            var parts = t.Split(sep);
            if (parts.Length > 2)
            {
                if (!IsGrouped(parts)) return false;             // "1,234,567" / "1.234.567" — else "1,2,3"
                whole = string.Concat(parts);
            }
            else if (IsGrouped(parts))
            {
                if (sep == '.') return false;                    // "1.000": $1 or $1,000 — ask, do not guess
                whole = string.Concat(parts);                    // "1,000": the dollar habit of the billing pages
            }
            else
            {
                whole = parts[0];                                // "12,50", "12.5", "0,125", ".5"
                fraction = parts[1];
                if (whole.Length == 0 && fraction.Length == 0) return false;
            }
        }
        else whole = t;

        string norm = fraction.Length > 0 ? (whole.Length > 0 ? whole : "0") + "." + fraction : whole;
        return norm.Length > 0 && decimal.TryParse(norm, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out usd) && usd >= 0;
    }

    /// <summary>Thousands groups: a lead of 1–3 digits other than "0", then groups of exactly 3.</summary>
    private static bool IsGrouped(string[] parts) =>
        parts.Length >= 2 && parts[0].Length is >= 1 and <= 3 && parts[0] != "0"
        && parts.Skip(1).All(p => p.Length == 3);
}
