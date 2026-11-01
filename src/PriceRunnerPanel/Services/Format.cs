using System.Globalization;

namespace PriceRunnerPanel.Services;

/// <summary>Swedish price formatting ("4 272 kr") and short relative times.</summary>
public static class Format
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
