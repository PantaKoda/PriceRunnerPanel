using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using PriceRunnerPanel.Models;

namespace PriceRunnerPanel.Services;

/// <summary>
/// Talks to the public JSON endpoints the pricerunner.se web app uses: product search and the offers of one
/// product. Read-only and anonymous; no cookies or sign-in.
/// </summary>
public sealed class PriceRunnerClient : IDisposable
{
    public const string Site = "https://www.pricerunner.se";
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

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var url = $"/se/api/search-edge-rest/public/search/v6/{Market}?q={Uri.EscapeDataString(query)}&facetsSize=10";
        using var document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("products", out var products) || products.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var hits = new List<SearchHit>();
        foreach (var p in products.EnumerateArray())
        {
            if (Str(p, "id") is not { } id || Str(p, "name") is not { } name || Str(p, "url") is not { } productUrl)
            {
                continue;
            }

            var lowest = Money(p, "lowestPrice");
            var rating = Obj(p, "rating");
            hits.Add(new SearchHit(
                id,
                name,
                Str(p, "description"),
                Str(Obj(p, "category"), "name"),
                productUrl,
                Str(Obj(p, "image"), "path"),
                lowest?.Amount,
                lowest?.Currency ?? "SEK",
                Dec(Obj(p, "priceDrop"), "percent"),
                (double?)Dec(rating, "average"),
                Int(rating, "count"),
                Int(Obj(p, "previewMerchants"), "count"),
                p.TryGetProperty("outOfStock", out var outOfStock) && outOfStock.ValueKind == JsonValueKind.True));
        }

        return hits;
    }

    public async Task<PriceSnapshot> GetPriceAsync(string productId, CancellationToken cancellationToken)
    {
        var url = $"/se/api/product-detail-edge-rest/public/product-detail/v0/offers/{Market}/{Uri.EscapeDataString(productId)}"
            + "?af_ORIGIN=NATIONAL&af_ITEM_CONDITION=NEW,UNKNOWN&af_STOCK_STATUS=IN_STOCK,BACKORDER,PREORDER,SPECIAL_ORDER,COLLECT_IN_STORE,UNKNOWN";
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
                if (Money(offer, "price") is not { } price)
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
        if (best is { } chosen)
        {
            offerUrl = Str(chosen.Offer, "url");
            if (Str(chosen.Offer, "merchantId") is { } merchantId)
            {
                merchantNames.TryGetValue(merchantId, out merchant);
            }
        }

        return new PriceSnapshot(lowestInStock, lowestOverall, currency, count, merchant, offerUrl);
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
