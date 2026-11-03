using System.Globalization;

namespace CluadeX.Services.Billing;

/// <summary>Dollars on the Billing page: shown without rounding a cheap request to zero, and read back from what the
/// user typed off a provider's billing page.</summary>
public static class UsdText
{
    /// <summary>Keep sub-cent API costs visible instead of rounding a cheap request to zero.</summary>
    public static string Format(decimal v) =>
        "$" + (Math.Abs(v) < 0.01m && v != 0 ? v.ToString("0.########", CultureInfo.InvariantCulture) : v.ToString("N2", CultureInfo.InvariantCulture));

    /// <summary>"25", "$12.50", "12,50", "1,000", "1,234.56": a non-negative amount. "1,2,3", "abc" and "-3" are refused.</summary>
    public static bool TryParse(string? text, out decimal usd)
    {
        string t = (text ?? "").Trim().TrimStart('$').Replace(" ", "");
        if (t.Contains(',') && !t.Contains('.'))
        {
            var parts = t.Split(',');
            bool grouped = parts[0].Length is >= 1 and <= 3 && parts[0] != "0" && parts.Skip(1).All(p => p.Length == 3 && p.All(char.IsDigit));
            if (grouped) t = t.Replace(",", "");
            else if (parts.Length == 2) t = t.Replace(',', '.');
            else { usd = 0; return false; }
        }
        else t = t.Replace(",", "");
        return decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out usd) && usd >= 0;
    }
}
