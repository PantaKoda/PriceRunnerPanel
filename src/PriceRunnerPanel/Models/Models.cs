namespace PriceRunnerPanel.Models;

/// <summary>One product row from a PriceRunner search.</summary>
public sealed record SearchHit(
    string Id,
    string Name,
    string? Description,
    string? Category,
    string Url,
    string? ImagePath,
    decimal? LowestPrice,
    string Currency,
    decimal? RecentDropPercent,
    double? Rating,
    int RatingCount,
    int StoreCount,
    bool OutOfStock);

/// <summary>The current offers for one product: what it costs right now and where.</summary>
public sealed record PriceSnapshot(
    decimal? LowestInStock,
    decimal? LowestOverall,
    string Currency,
    int OfferCount,
    string? CheapestMerchant,
    string? CheapestOfferUrl)
{
    /// <summary>The price we track: the cheapest offer in stock, else the cheapest offer at all.</summary>
    public decimal? TrackedPrice => LowestInStock ?? LowestOverall;
}

public sealed record PricePoint(DateTimeOffset At, decimal Price);

/// <summary>A pinned product and everything we have learned about its price. Persisted as JSON.</summary>
public sealed class PinnedProduct
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required string Url { get; set; }
    public string? ImagePath { get; set; }
    public string? Category { get; set; }
    public string Currency { get; set; } = "SEK";

    public DateTimeOffset PinnedAt { get; init; }
    public decimal PinnedPrice { get; set; }

    public decimal? CurrentPrice { get; set; }
    public decimal? LowestSeen { get; set; }
    public DateTimeOffset? LowestSeenAt { get; set; }
    public decimal? HighestSeen { get; set; }
    public decimal? TargetPrice { get; set; }

    public DateTimeOffset? LastChecked { get; set; }
    public string? LastError { get; set; }
    public string? CheapestMerchant { get; set; }
    public string? CheapestOfferUrl { get; set; }
    public int OfferCount { get; set; }

    /// <summary>One point per observed price change (plus the first observation).</summary>
    public List<PricePoint> History { get; set; } = [];
}

public sealed class AppSettings
{
    public int RefreshMinutes { get; set; } = 60;

    /// <summary>Start hidden in the tray when the user signs in to Windows.</summary>
    public bool StartWithWindows { get; set; } = true;

    /// <summary>Drops smaller than this (in percent of the previous price) do not notify.</summary>
    public decimal MinDropPercent { get; set; } = 1;
}

public sealed class AppState
{
    public AppSettings Settings { get; set; } = new();
    public List<PinnedProduct> Pins { get; set; } = [];
    public DateTimeOffset? LastRefresh { get; set; }
}
