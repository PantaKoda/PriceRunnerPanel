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

/// <summary>PriceRunner's lowest price on one day (Swedish stores), and the store that had it.</summary>
public sealed record MarketPoint(DateOnly Day, decimal Price, string? Merchant);

/// <summary>
/// PriceRunner's grouping of one product's variants (usually colours): a stable key, the shared base name and each
/// member's variant label by product id.
/// </summary>
public sealed record ProductGroup(string Key, string Name, IReadOnlyDictionary<string, string> Variants);

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

    /// <summary>PriceRunner's variant group (same product, other colours); null when the product has no variants.</summary>
    public string? GroupKey { get; set; }

    /// <summary>The product's name without its variant, e.g. "Sony WH-1000XM6".</summary>
    public string? GroupName { get; set; }

    /// <summary>This product's variant within its group, e.g. "Midnight Blue".</summary>
    public string? Variant { get; set; }

    /// <summary>The variant group was looked up (successfully); old pins are looked up on the next check.</summary>
    public bool GroupResolved { get; set; }

    /// <summary>One point per observed price change (plus the first observation).</summary>
    public List<PricePoint> History { get; set; } = [];

    /// <summary>
    /// PriceRunner's own daily lowest price for the past year, including the time before the pin. It shows what the
    /// product usually costs, so a "discount" that only undoes a recent price hike can be told from a real one.
    /// </summary>
    public List<MarketPoint> MarketHistory { get; set; } = [];

    public DateTimeOffset? MarketHistoryFetched { get; set; }
}

public sealed class AppSettings
{
    public int RefreshMinutes { get; set; } = 60;

    /// <summary>Start hidden in the tray when the user signs in to Windows.</summary>
    public bool StartWithWindows { get; set; } = true;

    /// <summary>The newest release a notification was shown for, so each update notifies once.</summary>
    public string? NotifiedUpdate { get; set; }

    /// <summary>Drops smaller than this (in percent of the previous price) do not notify.</summary>
    public decimal MinDropPercent { get; set; } = 1;
}

public sealed class AppState
{
    public AppSettings Settings { get; set; } = new();
    public List<PinnedProduct> Pins { get; set; } = [];
    public DateTimeOffset? LastRefresh { get; set; }
}
