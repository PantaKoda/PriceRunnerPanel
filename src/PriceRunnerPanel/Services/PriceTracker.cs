using PriceRunnerPanel.Models;

namespace PriceRunnerPanel.Services;

/// <summary>What one price check found, compared with what we knew before it.</summary>
public sealed record PriceCheck(decimal? Previous, decimal Current, bool IsNewLow, bool ReachedTarget, string? Merchant)
{
    public decimal? DropPercent => Previous is { } p && p > 0 && Current < p ? (p - Current) / p * 100 : null;
}

/// <summary>Checks a pinned product's price and folds the result into its history.</summary>
public sealed class PriceTracker(PriceRunnerClient client, TimeProvider time)
{
    private const int MaxHistoryPoints = 1000;
    private static readonly TimeSpan MarketHistoryAge = TimeSpan.FromHours(12);

    public async Task<PriceCheck?> CheckAsync(PinnedProduct pin, CancellationToken cancellationToken)
    {
        PriceSnapshot snapshot;
        try
        {
            snapshot = await client.GetPriceAsync(pin.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or PriceRunnerException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            pin.LastError = ex is TaskCanceledException ? "Timed out" : ex.Message;
            pin.LastChecked = time.GetUtcNow();
            return null;
        }

        var now = time.GetUtcNow();
        pin.LastChecked = now;
        if (snapshot.TrackedPrice is not { } price)
        {
            pin.LastError = "No store offers this product right now";
            pin.OfferCount = 0;
            return null;
        }

        var previous = pin.CurrentPrice;
        var isNewLow = pin.LowestSeen is { } low && price < low;
        var reachedTarget = pin.TargetPrice is { } target && price <= target && (previous is null || previous > target);

        pin.LastError = null;
        pin.CurrentPrice = price;
        pin.Currency = snapshot.Currency;
        pin.OfferCount = snapshot.OfferCount;
        pin.CheapestMerchant = snapshot.CheapestMerchant;
        pin.CheapestOfferUrl = snapshot.CheapestOfferUrl;
        if (pin.LowestSeen is null || price < pin.LowestSeen)
        {
            pin.LowestSeen = price;
            pin.LowestSeenAt = now;
        }

        if (pin.HighestSeen is null || price > pin.HighestSeen)
        {
            pin.HighestSeen = price;
        }

        if (pin.History.Count == 0 || pin.History[^1].Price != price)
        {
            pin.History.Add(new PricePoint(now, price));
            if (pin.History.Count > MaxHistoryPoints)
            {
                pin.History.RemoveRange(0, pin.History.Count - MaxHistoryPoints);
            }
        }

        await RefreshMarketHistoryAsync(pin, cancellationToken).ConfigureAwait(false);
        await RefreshReportedDropAsync(pin, cancellationToken).ConfigureAwait(false);
        return new PriceCheck(previous, price, isNewLow, reachedTarget, snapshot.CheapestMerchant);
    }

    /// <summary>
    /// The drop PriceRunner itself shows for the product, which only its product listings carry: it searches for the
    /// product's name and reads its row. Refreshed as seldom as the history.
    /// </summary>
    private async Task RefreshReportedDropAsync(PinnedProduct pin, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (pin.ReportedDropFetched is { } fetched && now - fetched < MarketHistoryAge)
        {
            return;
        }

        try
        {
            var hits = await client.SearchAsync(pin.Name, cancellationToken).ConfigureAwait(false);
            if (hits.FirstOrDefault(h => h.Id == pin.Id) is { } hit)
            {
                pin.ReportedDropPercent = hit.RecentDropPercent;
                pin.ReportedOldPrice = hit.OldPrice;
                pin.ReportedDropFetched = now;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or PriceRunnerException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            // Keep what we have; the next check tries again.
        }
    }

    /// <summary>PriceRunner's year of daily prices changes once a day; fetched at most every <see cref="MarketHistoryAge"/>.</summary>
    private async Task RefreshMarketHistoryAsync(PinnedProduct pin, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (pin.MarketHistoryFetched is { } fetched && now - fetched < MarketHistoryAge)
        {
            return;
        }

        try
        {
            var history = await client.GetPriceHistoryAsync(pin.Id, cancellationToken).ConfigureAwait(false);
            pin.MarketHistory = [.. history];
            pin.MarketHistoryFetched = now;
        }
        catch (Exception ex) when (ex is HttpRequestException or PriceRunnerException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            // Keep the history we have; the next check tries again. The price itself was checked fine.
        }
    }
}
