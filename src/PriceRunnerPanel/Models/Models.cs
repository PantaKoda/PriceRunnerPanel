using System.Text.Json.Serialization;

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
    decimal? OldPrice,
    double? Rating,
    int RatingCount,
    int StoreCount,
    bool OutOfStock,
    string? Condition);

/// <summary>What to list from PriceRunner's deals: any of each kind ticked, and a discount range in percent.</summary>
public sealed record DealFilter(
    IReadOnlyCollection<int> Categories,
    IReadOnlyCollection<int> Merchants,
    IReadOnlyCollection<int> Brands,
    int MinDropPercent,
    int MaxDropPercent);

/// <summary>One page of PriceRunner's deals. <see cref="Total"/> is capped by PriceRunner at 10 000.</summary>
public sealed record DealsPage(int Total, IReadOnlyList<SearchHit> Hits);

/// <summary>One choice in a deals filter: a category or a store, and how many deals it has.</summary>
public sealed record DealFacetOption(int Id, string Name, int Count);

/// <summary>What the deals can be filtered by, most popular first.</summary>
public sealed record DealFacets(IReadOnlyList<DealFacetOption> Categories, IReadOnlyList<DealFacetOption> Merchants, IReadOnlyList<DealFacetOption> Brands);

/// <summary>What to narrow a product search to. PriceRunner takes one category (it drops the filter if given more), and any number of stores and brands.</summary>
public sealed record SearchFilter(int? Category, IReadOnlyCollection<int> Merchants, IReadOnlyCollection<int> Brands)
{
    public static SearchFilter None { get; } = new(null, [], []);
}

/// <summary>A product search's listings, and what they can be filtered by. Its choices come without counts.</summary>
public sealed record SearchPage(IReadOnlyList<SearchHit> Hits, DealFacets Facets);

/// <summary>The current offers for one product: what it costs right now and where.</summary>
public sealed record PriceSnapshot(
    decimal? LowestInStock,
    decimal? LowestOverall,
    string Currency,
    int OfferCount,
    string? CheapestMerchant,
    string? CheapestOfferUrl,
    string? CheapestCondition,
    string? CheapestOfferName)
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

    /// <summary>Also send this product's price alerts to Telegram and email, whichever are set up. Saved under its first name.</summary>
    [JsonPropertyName("EmailAlerts")]
    public bool RemoteAlerts { get; set; }

    public DateTimeOffset? LastChecked { get; set; }
    public string? LastError { get; set; }
    public string? CheapestMerchant { get; set; }
    public string? CheapestOfferUrl { get; set; }
    public int OfferCount { get; set; }

    /// <summary>The cheapest offer's labelled condition (USED, REFURBISHED), when it has one other than new.</summary>
    public string? CheapestCondition { get; set; }

    /// <summary>The cheapest offer's title, when it reads as used although nothing labels it so.</summary>
    public string? CheapestLooksUsedTitle { get; set; }

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

    /// <summary>The drop PriceRunner itself reports for the product now, in percent; null when it reports none.</summary>
    public decimal? ReportedDropPercent { get; set; }

    /// <summary>The earlier price PriceRunner measures that drop from.</summary>
    public decimal? ReportedOldPrice { get; set; }

    public DateTimeOffset? ReportedDropFetched { get; set; }
}

public sealed class AppSettings
{
    public int RefreshMinutes { get; set; } = 60;

    /// <summary>Start hidden in the tray when the user signs in to Windows.</summary>
    public bool StartWithWindows { get; set; } = true;

    /// <summary>The Start menu shortcut was created once; if the user deletes it, it stays deleted.</summary>
    public bool StartMenuShortcutCreated { get; set; }

    /// <summary>The newest release a notification was shown for, so each update notifies once.</summary>
    public string? NotifiedUpdate { get; set; }

    /// <summary>Drops smaller than this (in percent of the previous price) do not notify.</summary>
    public decimal MinDropPercent { get; set; } = 1;

    /// <summary>The mail account for email alerts; null when none is set up.</summary>
    public EmailSettings? Email { get; set; }

    /// <summary>The bot for Telegram alerts; null when none is set up.</summary>
    public TelegramSettings? Telegram { get; set; }
}

public sealed class TelegramSettings
{
    /// <summary>The bot token, encrypted for this Windows user. Never stored in the clear.</summary>
    public required string ProtectedToken { get; set; }

    public required long ChatId { get; set; }
}

public sealed class EmailSettings
{
    public required string Address { get; set; }

    /// <summary>Where alerts go; null sends them to <see cref="Address"/>.</summary>
    public string? SendTo { get; set; }

    public required string Server { get; set; }

    public int Port { get; set; } = 587;

    /// <summary>The password, encrypted for this Windows user. Never stored in the clear.</summary>
    public required string ProtectedPassword { get; set; }
}

public sealed class AppState
{
    public AppSettings Settings { get; set; } = new();
    public List<PinnedProduct> Pins { get; set; } = [];
    public DateTimeOffset? LastRefresh { get; set; }
}
