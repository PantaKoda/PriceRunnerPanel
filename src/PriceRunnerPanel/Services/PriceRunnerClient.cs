using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using PriceRunnerPanel.Models;

namespace PriceRunnerPanel.Services;

/// <summary>
/// Talks to the public JSON endpoints the pricerunner.se web app uses: product search, current deals and the offers
/// of one product, its variants and its price history. Read-only and anonymous; no cookies or sign-in.
/// </summary>
public sealed class PriceRunnerClient : IDisposable
{
    public const string Site = "https://www.pricerunner.se";
    public const int DealsPageSize = 32;

    /// <summary>The most deals PriceRunner returns in one request (200 is refused).</summary>
    public const int DealsMaxPageSize = 100;
    private const string Market = "SE";
    private const string ImageHost = "https://owp.klarna.com";

    private readonly HttpClient _http;

    public PriceRunnerClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri(Site), Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/156.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("sv-SE,sv;q=0.9,en;q=0.8");
        _http.DefaultRequestHeaders.Add("x-client-service", "owp");
    }

    /// <summary>Shared with the image loader so both use one connection pool.</summary>
    public HttpClient Http => _http;

    /// <summary>
    /// A product's page on pricerunner.se. The path comes from PriceRunner's data, so it is resolved as a URL (never
    /// glued onto the site as text, where "@evil.example" would change the host) and must stay on pricerunner.se.
    /// </summary>
    public static string ProductPageUrl(string relativeUrl)
    {
        var site = new Uri(Site);
        return Uri.TryCreate(site, relativeUrl, out var url) && url.Scheme == Uri.UriSchemeHttps
            && string.Equals(url.IdnHost, site.IdnHost, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(url.UserInfo)
            ? url.AbsoluteUri
            : Site;
    }

    /// <summary>Thumbnail URL for an image path such as <c>/product/3216619431/Name.jpg</c>.</summary>
    public static string? ImageUrl(string? imagePath, int size = 112)
    {
        if (string.IsNullOrEmpty(imagePath))
        {
            return null;
        }

        var rest = imagePath.StartsWith("/product/", StringComparison.Ordinal) ? imagePath["/product".Length..] : imagePath;
        return $"{ImageHost}/product/{size}x{size}{rest}?ph=true";
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, CancellationToken cancellationToken) =>
        (await SearchAsync(query, SearchFilter.None, cancellationToken).ConfigureAwait(false)).Hits;

    /// <summary>
    /// PriceRunner applies the filter itself, so it fills its at most 20 listings from everything that matches, not just
    /// from the listings shown before. The answer also carries the filter choices, without a request of their own.
    /// </summary>
    public async Task<SearchPage> SearchAsync(string query, SearchFilter filter, CancellationToken cancellationToken)
    {
        var url = $"/se/api/search-edge-rest/public/search/v6/{Market}?q={Uri.EscapeDataString(query)}&facetsSize=10";
        if (filter.Category is { } category)
        {
            url += $"&af_CATEGORY=cl{category}";
        }

        if (filter.Merchants.Count > 0)
        {
            url += $"&af_MERCHANT={string.Join(',', filter.Merchants)}";
        }

        if (filter.Brands.Count > 0)
        {
            url += $"&af_BRAND={string.Join(',', filter.Brands)}";
        }

        using var document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var hits = root.TryGetProperty("products", out var products) && products.ValueKind == JsonValueKind.Array ? ParseHits(products) : [];
        return new SearchPage(hits, new DealFacets(ParseSearchCategories(root), ParseQuickFilter(root, "MERCHANT"), ParseQuickFilter(root, "BRAND")));
    }

    /// <summary>
    /// Current deals: products at least 10 % below their earlier price, newest drops first. This is the list on
    /// pricerunner.se/c/black-friday, which shows PriceRunner's deals all year round.
    /// </summary>
    public async Task<DealsPage> GetDealsAsync(DealFilter filter, int offset, CancellationToken cancellationToken, int size = DealsPageSize)
    {
        var url = $"/se/api/deals-edge-rest/public/search/deals/products/v3/{Market}?{DealFilters(filter)}"
            + $"&sorting=LATESTPRICEDROP_DESC,GLOBALRANK_ASC&size={size}&offset={offset}";
        using var document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var hits = root.TryGetProperty("products", out var products) && products.ValueKind == JsonValueKind.Array ? ParseHits(products) : [];
        return new DealsPage(Int(root, "totalHits"), hits);
    }

    /// <summary>
    /// The filter choices for the deals. PriceRunner narrows each filter by the others' selections (the stores
    /// that have deals in the chosen categories), and lists the popular choices separately from the rest.
    /// </summary>
    public async Task<DealFacets> GetDealFacetsAsync(DealFilter filter, CancellationToken cancellationToken)
    {
        var url = $"/se/api/deals-edge-rest/public/search/deals/facets/{Market}?{DealFilters(filter)}&size={DealsPageSize}";
        using var document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        var facets = Obj(document.RootElement, "facets");
        return new DealFacets(ParseFacet(Obj(facets, "CATEGORY")), ParseFacet(Obj(facets, "MERCHANT_DEAL")), ParseFacet(Obj(facets, "BRAND")));
    }

    /// <summary>What each store asks for a product (new, Swedish stores): its cheapest offer in stock, else its cheapest.</summary>
    public async Task<IReadOnlyDictionary<int, decimal>> GetStorePricesAsync(string productId, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(OffersUrl(productId), cancellationToken).ConfigureAwait(false);
        var best = new Dictionary<int, (decimal Price, bool InStock)>();
        if (document.RootElement.TryGetProperty("offers", out var offers) && offers.ValueKind == JsonValueKind.Array)
        {
            foreach (var offer in offers.EnumerateArray())
            {
                if (OfferPrice(offer) is not { } price || !int.TryParse(Str(offer, "merchantId"), out var store))
                {
                    continue;
                }

                var inStock = Str(offer, "stockStatus") == "IN_STOCK";
                if (!best.TryGetValue(store, out var current) || (inStock && !current.InStock) || (inStock == current.InStock && price.Amount < current.Price))
                {
                    best[store] = (price.Amount, inStock);
                }
            }
        }

        return best.ToDictionary(s => s.Key, s => s.Value.Price);
    }

    /// <summary>
    /// A product's current price from new offers. A refurbished listing has none, so that takes a second request for
    /// its used and refurbished offers.
    /// </summary>
    public async Task<PriceSnapshot> GetPriceAsync(string productId, CancellationToken cancellationToken)
    {
        var snapshot = await ReadPriceAsync(OffersUrl(productId), cancellationToken).ConfigureAwait(false);
        return snapshot.OfferCount > 0
            ? snapshot
            : await ReadPriceAsync(OffersUrl(productId, "USED,REFURBISHED"), cancellationToken).ConfigureAwait(false);
    }

    private async Task<PriceSnapshot> ReadPriceAsync(string url, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;

        var merchantNames = new Dictionary<string, string>();
        if (Obj(root, "merchants") is { ValueKind: JsonValueKind.Object } merchants)
        {
            foreach (var m in merchants.EnumerateObject())
            {
                if (Str(m.Value, "name") is { } name)
                {
                    merchantNames[m.Name] = name;
                }
            }
        }

        // Cheapest offer, preferring offers that are in stock. Computed from the filtered offer list (new condition,
        // Swedish stores), not from "offersSummary", whose minimums include used and refurbished offers.
        (decimal Price, bool InStock, JsonElement Offer)? best = null;
        decimal? lowestOverall = null;
        var count = 0;
        var currency = "SEK";
        if (root.TryGetProperty("offers", out var offers) && offers.ValueKind == JsonValueKind.Array)
        {
            foreach (var offer in offers.EnumerateArray())
            {
                if (OfferPrice(offer) is not { } price)
                {
                    continue;
                }

                count++;
                currency = price.Currency;
                lowestOverall = lowestOverall is { } low ? Math.Min(low, price.Amount) : price.Amount;
                var inStock = Str(offer, "stockStatus") == "IN_STOCK";
                if (best is null || (inStock && !best.Value.InStock) || (inStock == best.Value.InStock && price.Amount < best.Value.Price))
                {
                    best = (price.Amount, inStock, offer);
                }
            }
        }

        var lowestInStock = best is { InStock: true } b ? b.Price : (decimal?)null;

        string? merchant = null;
        string? offerUrl = null;
        string? condition = null;
        string? offerName = null;
        if (best is { } chosen)
        {
            offerUrl = Str(chosen.Offer, "url");
            condition = OfferCondition(chosen.Offer);
            offerName = Str(chosen.Offer, "name");
            if (Str(chosen.Offer, "merchantId") is { } merchantId)
            {
                merchantNames.TryGetValue(merchantId, out merchant);
            }
        }

        return new PriceSnapshot(lowestInStock, lowestOverall, currency, count, merchant, offerUrl, condition, offerName);
    }

    /// <summary>
    /// The product's lowest price per day over the past year, from Swedish stores: the data behind the
    /// "Prisutveckling" tab on the product page. Oldest first.
    /// </summary>
    public async Task<IReadOnlyList<MarketPoint>> GetPriceHistoryAsync(string productId, CancellationToken cancellationToken)
    {
        var url = $"/se/api/product-information-edge-rest/public/pricehistory/product/{Uri.EscapeDataString(productId)}/{Market}/DAY"
            + "?merchantId=&selectedInterval=ONE_YEAR&filter=NATIONAL";
        using var document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("history", out var history) || history.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        // One point per day; the timestamp is the day's start in Swedish time, e.g. "2026-08-27T00:00:00.000+02:00".
        var days = new SortedDictionary<DateOnly, MarketPoint>();
        foreach (var point in history.EnumerateArray())
        {
            if (Str(point, "timestamp") is { Length: >= 10 } timestamp
                && DateOnly.TryParseExact(timestamp[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                && Dec(point, "price") is { } price && price > 0
                && (!days.TryGetValue(day, out var existing) || price < existing.Price))
            {
                days[day] = new MarketPoint(day, price, Str(point, "merchantName"));
            }
        }

        return [.. days.Values];
    }

    /// <summary>
    /// The product's variant group (e.g. its colours), or null when PriceRunner lists it on its own. Products that
    /// a single store lists under its own name usually have no group.
    /// </summary>
    public async Task<ProductGroup?> GetProductGroupAsync(string productId, string productUrl, CancellationToken cancellationToken)
    {
        // Product URLs look like /pl/94-3410100348/...; 94 is the category the endpoint expects before the id.
        var match = Regex.Match(productUrl, @"/pl/(\d+)-");
        var category = match.Success ? match.Groups[1].Value : "1";
        var url = $"/se/api/product-detail-edge-rest/public/product-detail/v0/initial/{Market}/{category}/{Uri.EscapeDataString(productId)}?languageCode=sv";
        using var document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        // "productGroup" is null for products without variants.
        var group = Obj(document.RootElement, "productGroup");
        if (group.ValueKind != JsonValueKind.Object
            || !group.TryGetProperty("members", out var members) || members.ValueKind != JsonValueKind.Array || members.GetArrayLength() < 2)
        {
            return null;
        }

        var variants = new Dictionary<string, string>();
        var bases = new List<string>();
        foreach (var member in members.EnumerateArray())
        {
            if (Str(member, "id") is not { } id || Str(member, "name") is not { } name)
            {
                continue;
            }

            var label = member.TryGetProperty("attributeValues", out var values) && values.ValueKind == JsonValueKind.Array
                ? string.Join(" / ", values.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()))
                : "";
            variants[id] = label.Length > 0 ? label : name;
            bases.Add(label.Length > 0 ? ProductGrouping.Trim(name.Replace(label, "", StringComparison.OrdinalIgnoreCase)) : name);
        }

        if (variants.Count < 2)
        {
            return null;
        }

        // Every member without its variant label should leave the same base name; otherwise use their common words.
        var baseName = bases.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? bases[0] : ProductGrouping.CommonPrefix(bases);
        var key = "pg:" + variants.Keys.OrderBy(k => k.Length).ThenBy(k => k, StringComparer.Ordinal).First();
        return new ProductGroup(key, baseName.Length > 0 ? baseName : bases[0], variants);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// What an offer costs now. During a store's sale "price" stays the regular price and the sale price sits in
    /// "campaignPrice", valid between two dates; that's what PriceRunner lists. A price only for a store's members
    /// isn't for everyone, so it doesn't count.
    /// </summary>
    private static (decimal Amount, string Currency)? OfferPrice(JsonElement offer)
    {
        var campaign = Obj(offer, "campaignPrice");
        return Money(campaign, "price") is { } sale
            && Obj(offer, "campaignMembership").ValueKind != JsonValueKind.Object
            && IsCurrent(Obj(campaign, "salePriceEffectiveDate"))
            ? sale
            : Money(offer, "price");
    }

    private static bool IsCurrent(JsonElement period)
    {
        var now = DateTimeOffset.UtcNow;
        return !(TryDate(period, "start") > now) && !(TryDate(period, "end") < now);
    }

    private static DateTimeOffset? TryDate(JsonElement element, string name) =>
        DateTimeOffset.TryParse(Str(element, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;

    /// <summary>The condition an offer is labelled with (NEW, USED, REFURBISHED); many offers carry no label.</summary>
    private static string? OfferCondition(JsonElement offer)
    {
        var labels = Obj(offer, "labels");
        if (labels.ValueKind != JsonValueKind.Object || !labels.TryGetProperty("attributeLabels", out var attributes) || attributes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return attributes.EnumerateArray().Where(a => Str(a, "name") == "itemCondition").Select(a => Str(a, "value")).FirstOrDefault();
    }

    private static string OffersUrl(string productId, string conditions = "NEW,UNKNOWN") =>
        $"/se/api/product-detail-edge-rest/public/product-detail/v0/offers/{Market}/{Uri.EscapeDataString(productId)}"
        + $"?af_ORIGIN=NATIONAL&af_ITEM_CONDITION={conditions}&af_STOCK_STATUS=IN_STOCK,BACKORDER,PREORDER,SPECIAL_ORDER,COLLECT_IN_STORE,UNKNOWN";

    private static string DealFilters(DealFilter filter)
    {
        // PriceRunner writes a range of drops as negative percentages, largest drop first: 30-50 % is -50_-30.
        var filters = $"af_PRICE_DROP=-{filter.MaxDropPercent}_-{filter.MinDropPercent}";
        if (filter.Categories.Count > 0)
        {
            filters += $"&af_CATEGORY={string.Join(',', filter.Categories)}";
        }

        if (filter.Merchants.Count > 0)
        {
            filters += $"&af_MERCHANT_DEAL={string.Join(',', filter.Merchants)}";
        }

        if (filter.Brands.Count > 0)
        {
            filters += $"&af_BRAND={string.Join(',', filter.Brands)}";
        }

        return filters;
    }

    private static List<SearchHit> ParseHits(JsonElement products)
    {
        var hits = new List<SearchHit>();
        foreach (var p in products.EnumerateArray())
        {
            if (Str(p, "id") is not { } id || Str(p, "name") is not { } name || Str(p, "url") is not { } productUrl)
            {
                continue;
            }

            var lowest = Money(p, "lowestPrice");
            var rating = Obj(p, "rating");
            var drop = Obj(p, "priceDrop");
            hits.Add(new SearchHit(
                id,
                name,
                Str(p, "description"),
                Str(Obj(p, "category"), "name"),
                productUrl,
                Str(Obj(p, "image"), "path"),
                lowest?.Amount,
                lowest?.Currency ?? "SEK",
                Dec(drop, "percent"),
                Money(drop, "oldPrice")?.Amount,
                (double?)Dec(rating, "average"),
                Int(rating, "count"),
                Int(Obj(p, "previewMerchants"), "count"),
                p.TryGetProperty("outOfStock", out var outOfStock) && outOfStock.ValueKind == JsonValueKind.True,
                Str(p, "condition")));
        }

        return hits;
    }

    /// <summary>The popular choices in PriceRunner's order ("rankedCounts"), then all the others, largest first.</summary>
    /// <summary>A search lists its categories with ids like "cl38".</summary>
    private static List<DealFacetOption> ParseSearchCategories(JsonElement root)
    {
        var options = new List<DealFacetOption>();
        if (root.TryGetProperty("categories", out var categories) && categories.ValueKind == JsonValueKind.Array)
        {
            foreach (var category in categories.EnumerateArray())
            {
                var id = Str(category, "id");
                if (id is not null && id.StartsWith("cl", StringComparison.Ordinal) && int.TryParse(id.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                    && Str(category, "name") is { } name)
                {
                    options.Add(new DealFacetOption(number, name, 0));
                }
            }
        }

        return options;
    }

    private static List<DealFacetOption> ParseQuickFilter(JsonElement root, string filterId)
    {
        var options = new List<DealFacetOption>();
        if (!root.TryGetProperty("quickFilters", out var filters) || filters.ValueKind != JsonValueKind.Array)
        {
            return options;
        }

        foreach (var filter in filters.EnumerateArray().Where(f => Str(f, "id") == filterId))
        {
            if (!filter.TryGetProperty("options", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var option in list.EnumerateArray())
            {
                if (int.TryParse(Str(option, "id"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && Str(option, "value") is { } name)
                {
                    options.Add(new DealFacetOption(id, name, 0));
                }
            }
        }

        return options;
    }

    private static List<DealFacetOption> ParseFacet(JsonElement container)
    {
        var facet = Obj(container, "facet");
        var options = new List<DealFacetOption>();
        var seen = new HashSet<int>();
        foreach (var (list, sort) in new[] { ("rankedCounts", false), ("counts", true) })
        {
            if (facet.ValueKind != JsonValueKind.Object || !facet.TryGetProperty(list, out var counts) || counts.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var parsed = counts.EnumerateArray()
                .Where(c => Str(c, "optionValue") is not null && Int(c, "optionId") > 0)
                .Select(c => new DealFacetOption(Int(c, "optionId"), Str(c, "optionValue")!, Int(c, "count")));
            options.AddRange((sort ? parsed.OrderByDescending(o => o.Count) : parsed).Where(o => seen.Add(o.Id)));
        }

        return options;
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new PriceRunnerException($"PriceRunner answered {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        if (response.Content.Headers.ContentType?.MediaType is { } type && !type.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            throw new PriceRunnerException("PriceRunner returned a web page instead of data (possibly rate limited)");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private static JsonElement Obj(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static decimal? Dec(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    private static int Int(JsonElement element, string name) => (int)(Dec(element, name) ?? 0);

    private static (decimal Amount, string Currency)? Money(JsonElement element, string name)
    {
        var money = Obj(element, name);
        return Dec(money, "amount") is { } amount ? (amount, Str(money, "currency") ?? "SEK") : null;
    }
}

public sealed class PriceRunnerException(string message) : Exception(message);
