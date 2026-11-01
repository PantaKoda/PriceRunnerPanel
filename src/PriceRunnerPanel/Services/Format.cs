using System.Globalization;
using System.Text.RegularExpressions;

namespace PriceRunnerPanel.Services;

/// <summary>Swedish price formatting ("4 272 kr") and short relative times.</summary>
public static partial class Format
{
    private static readonly CultureInfo Swedish = CultureInfo.GetCultureInfo("sv-SE");

    public static string Price(decimal? amount, string currency = "SEK")
    {
        if (amount is not { } value)
        {
            return "—";
        }

        var number = value == decimal.Truncate(value) ? value.ToString("N0", Swedish) : value.ToString("N2", Swedish);
        return currency == "SEK" ? $"{number} kr" : $"{number} {currency}";
    }

    /// <summary>PriceRunner's words for a condition that isn't new; null for new or unknown.</summary>
    public static string? Condition(string? condition) => condition switch
    {
        "REFURBISHED" => "RENOVERAD",
        "USED" => "BEGAGNAD",
        _ => null,
    };

    /// <summary>Stores also sell used items with no label saying so, but their titles give it away: "128GB BEGAGNAD", "Bra skick".</summary>
    public static bool LooksUsed(string? offerTitle) => offerTitle is not null && UsedWords().IsMatch(offerTitle);

    [GeneratedRegex(@"\b(begagnad|beg|renoverad|refurbished|skick|nyskick)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UsedWords();

    public static string Count(int value) => value.ToString("N0", Swedish);

    public static string Percent(decimal value) => value.ToString("0.#", Swedish) + " %";

    public static string Ago(DateTimeOffset? when, DateTimeOffset now)
    {
        if (when is not { } at)
        {
            return "never";
        }

        var span = now - at;
        return span.TotalSeconds switch
        {
            < 60 => "just now",
            < 3600 => $"{(int)span.TotalMinutes} min ago",
            < 86400 => $"{(int)span.TotalHours} h ago",
            _ => $"{(int)span.TotalDays} d ago",
        };
    }

    public static string Interval(int minutes) => minutes < 60 ? $"{minutes} min" : minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60.0:0.#} h";
}
