using PriceRunnerPanel.Models;

namespace PriceRunnerPanel.Services;

public enum DealVerdict
{
    /// <summary>Not enough history to say what the product usually costs.</summary>
    Unknown,

    /// <summary>At (or near) its lowest price of the year and clearly below its typical price.</summary>
    Deal,

    /// <summary>Clearly below its typical price.</summary>
    Good,

    /// <summary>About what it usually costs.</summary>
    Typical,

    /// <summary>
    /// The price went up in the last weeks and is now coming down again, but not below what it usually cost before
    /// the rise: the classic Black Friday "discount" from an inflated price.
    /// </summary>
    Inflated,

    /// <summary>Clearly above its typical price.</summary>
    High,
}

/// <summary>How today's price compares with the product's past year on PriceRunner.</summary>
public sealed record DealInsight(
    DealVerdict Verdict,
    decimal? Typical,
    decimal? VsTypicalPercent,
    MarketPoint? YearLow,
    MarketPoint? YearHigh,
    MarketPoint? RecentPeak)
{
    public static readonly DealInsight None = new(DealVerdict.Unknown, null, null, null, null, null);
}

/// <summary>
/// Judges a price against PriceRunner's daily history. The yardstick is the <b>typical price</b>: the median of the
/// daily lowest prices from 3 to 11 weeks ago. The last three weeks are left out on purpose, so a price raised just
/// before a sale cannot pass itself off as the normal price the sale is measured against.
/// </summary>
public static class DealAnalysis
{
    public const int TypicalFromDays = 77;
    public const int TypicalToDays = 21;
    public const int RecentDays = 30;

    private const int MinTypicalDays = 14;

    public static DealInsight Analyze(decimal? current, IReadOnlyList<MarketPoint> history, DateOnly today)
    {
        // Today's point may already be today's price; judge the current price against the days before it.
        var past = history.Where(p => p.Day < today).ToList();
        if (past.Count == 0)
        {
            return DealInsight.None;
        }

        var yearLow = past.MinBy(p => p.Price);
        var yearHigh = past.MaxBy(p => p.Price);
        var recent = past.Where(p => p.Day >= today.AddDays(-RecentDays)).ToList();
        var recentPeak = recent.Count > 0 ? recent.MaxBy(p => p.Price) : null;

        var window = past.Where(p => p.Day >= today.AddDays(-TypicalFromDays) && p.Day < today.AddDays(-TypicalToDays)).ToList();
        if (window.Count < MinTypicalDays)
        {
            // A young product (or a gap in the data): everything before the last three weeks.
            window = [.. past.Where(p => p.Day < today.AddDays(-TypicalToDays))];
        }

        if (window.Count < MinTypicalDays || current is not { } price)
        {
            return new DealInsight(DealVerdict.Unknown, null, null, yearLow, yearHigh, recentPeak);
        }

        var typical = Median(window.Select(p => p.Price));
        var vsTypical = (price - typical) / typical * 100;
        var verdict = Judge(price, typical, vsTypical, yearLow!.Price, recentPeak?.Price);
        return new DealInsight(verdict, typical, vsTypical, yearLow, yearHigh, recentPeak);
    }

    private static DealVerdict Judge(decimal price, decimal typical, decimal vsTypical, decimal yearLow, decimal? recentPeak)
    {
        if (vsTypical <= -12 || (vsTypical <= -3 && price <= yearLow * 1.02m))
        {
            return DealVerdict.Deal;
        }

        // Raised by 5 % or more within the last month, and today's lower price is still no better than before.
        if (recentPeak is { } peak && peak >= typical * 1.05m && price < peak && vsTypical > -3)
        {
            return DealVerdict.Inflated;
        }

        return vsTypical switch
        {
            <= -5 => DealVerdict.Good,
            >= 5 => DealVerdict.High,
            _ => DealVerdict.Typical,
        };
    }

    private static decimal Median(IEnumerable<decimal> values)
    {
        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
