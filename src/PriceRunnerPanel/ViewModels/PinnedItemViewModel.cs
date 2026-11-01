using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PriceRunnerPanel.Models;
using PriceRunnerPanel.Services;

namespace PriceRunnerPanel.ViewModels;

public enum PriceTrend
{
    Flat,
    Down,
    Up,
}

/// <summary>A pinned product card: current price, how it moved since pinning, history and an optional target.</summary>
public sealed partial class PinnedItemViewModel : ObservableObject
{
    private static readonly string[] DerivedProperties =
    [
        nameof(Name), nameof(CurrentPriceText), nameof(ChangeText), nameof(Trend), nameof(IsDown), nameof(IsUp), nameof(IsFlat),
        nameof(PinnedText), nameof(LowText), nameof(HighText), nameof(IsAtLowest), nameof(IsBelowTarget), nameof(StoreText),
        nameof(CheckedText), nameof(HasError), nameof(ErrorText), nameof(History), nameof(DealPosition), nameof(HasStoreLink),
        nameof(TargetHint), nameof(LowestChipText), nameof(Verdict), nameof(HasVerdict), nameof(VerdictText), nameof(VerdictTip),
        nameof(IsGoodDeal), nameof(IsInflated), nameof(IsOverpriced), nameof(InsightText), nameof(HasInsight), nameof(HasReportedDrop),
        nameof(ReportedDropText), nameof(ReportedDropTip), nameof(ConditionText), nameof(HasCondition), nameof(LooksUsed), nameof(LooksUsedTip),
    ];

    /// <summary>Days of PriceRunner's history the sparkline shows.</summary>
    private const int SparklineDays = 90;

    private readonly MainViewModel _owner;
    private DealInsight _insight = DealInsight.None;

    public PinnedItemViewModel(MainViewModel owner, PinnedProduct product)
    {
        _owner = owner;
        Product = product;
        TargetInput = product.TargetPrice is { } target ? target.ToString("0.##", CultureInfo.InvariantCulture) : "";
        _insight = Analyze();
    }

    public PinnedProduct Product { get; }

    public string Id => Product.Id;

    public string Name => Product.Name;

    public string CurrentPriceText => Format.Price(Product.CurrentPrice ?? Product.PinnedPrice, Product.Currency);

    /// <summary>Change since the product was pinned.</summary>
    public string ChangeText
    {
        get
        {
            if (Product.CurrentPrice is not { } now || Product.PinnedPrice <= 0 || now == Product.PinnedPrice)
            {
                return "± 0 since pinned";
            }

            var delta = now - Product.PinnedPrice;
            var percent = Math.Abs(delta) / Product.PinnedPrice * 100;
            return $"{(delta < 0 ? "▼" : "▲")} {Format.Price(Math.Abs(delta), Product.Currency)} ({Format.Percent(percent)})";
        }
    }

    public PriceTrend Trend => Product.CurrentPrice is { } now && now != Product.PinnedPrice
        ? now < Product.PinnedPrice ? PriceTrend.Down : PriceTrend.Up
        : PriceTrend.Flat;

    public bool IsDown => Trend == PriceTrend.Down;

    public bool IsUp => Trend == PriceTrend.Up;

    public bool IsFlat => Trend == PriceTrend.Flat;

    public string PinnedText => $"Pinned {Format.Price(Product.PinnedPrice, Product.Currency)}";

    /// <summary>How today's price compares with the past year on PriceRunner.</summary>
    public DealInsight Insight => _insight;

    /// <summary>Lowest price: over PriceRunner's past year when we have it, else since pinning.</summary>
    private decimal? Low => Min(_insight.YearLow?.Price, Product.LowestSeen);

    private decimal? High => Max(_insight.YearHigh?.Price, Product.HighestSeen);

    private bool HasYear => _insight.YearLow is not null;

    public string LowText => $"{(HasYear ? "1y low" : "Low")} {Format.Price(Low, Product.Currency)}";

    public string HighText => $"{(HasYear ? "1y high" : "High")} {Format.Price(High, Product.Currency)}";

    /// <summary>The price sits at the lowest seen (over the year when known), and it has actually moved at some point.</summary>
    public bool IsAtLowest => Product.CurrentPrice is { } now && now <= Low && High > now;

    public string LowestChipText => HasYear ? "1-YEAR LOW" : "LOWEST SEEN";

    public DealVerdict Verdict => _insight.Verdict;

    public bool HasVerdict => _insight.HasVerdict;

    public string VerdictText => _insight.Label;

    public bool IsGoodDeal => _insight.IsGoodDeal;

    public bool IsInflated => _insight.IsInflated;

    public bool IsOverpriced => _insight.IsOverpriced;

    public string VerdictTip => _insight.Explain(Product.Currency);

    public string InsightText => _insight.Summary(Product.Currency);

    /// <summary>What the price being tracked is for, when it isn't a new item.</summary>
    public string? ConditionText => Format.Condition(Product.CheapestCondition);

    public bool HasCondition => ConditionText is not null;

    public bool LooksUsed => Product.CheapestLooksUsedTitle is not null;

    public string LooksUsedTip => $"The cheapest offer may not be new, although nothing labels it as used. Its title: {Product.CheapestLooksUsedTitle}";

    /// <summary>The drop PriceRunner itself reports, shown next to our own verdict: both can be true at once.</summary>
    public bool HasReportedDrop => Product.ReportedDropPercent is > 0.5m;

    public string ReportedDropText => Product.ReportedDropPercent is { } percent ? $"PRICERUNNER −{Format.Percent(percent)}" : "";

    public string ReportedDropTip => Product.ReportedOldPrice is { } old
        ? $"PriceRunner reports this drop, from an earlier price of {Format.Price(old, Product.Currency)}"
        : "The drop PriceRunner reports";

    public bool HasInsight => HasYear;

    public bool IsBelowTarget => Product.TargetPrice is { } target && Product.CurrentPrice is { } now && now <= target;

    public string StoreText => Product.CheapestMerchant is { Length: > 0 } merchant
        ? $"Cheapest at {merchant}  ·  {Product.OfferCount} offers"
        : Product.OfferCount > 0 ? $"{Product.OfferCount} offers" : Product.Category ?? "";

    public bool HasStoreLink => !string.IsNullOrEmpty(Product.CheapestOfferUrl);

    public string CheckedText => $"Checked {Format.Ago(Product.LastChecked, DateTimeOffset.UtcNow)}";

    public bool HasError => Product.LastError is not null;

    public string ErrorText => Product.LastError ?? "";

    /// <summary>The last 90 days of PriceRunner's history and today's price; our own checks until that is fetched.</summary>
    public IReadOnlyList<double> History
    {
        get
        {
            if (Product.MarketHistory.Count == 0)
            {
                return [.. Product.History.Select(p => (double)p.Price)];
            }

            var from = Today.AddDays(-SparklineDays);
            List<double> values = [.. Product.MarketHistory.Where(p => p.Day >= from && p.Day < Today).Select(p => (double)p.Price)];
            if (Product.CurrentPrice is { } now)
            {
                values.Add((double)now);
            }

            return values;
        }
    }

    public double DealPosition => Product.CurrentPrice is { } now && Low is { } low && High is { } high && high > low
        ? (double)((now - low) / (high - low))
        : double.NaN;

    public string TargetHint => Product.TargetPrice is { } target
        ? IsBelowTarget ? "Target reached!" : $"{Format.Price(Product.CurrentPrice - target, Product.Currency)} to go"
        : "Alert when the price is at or below";

    [ObservableProperty]
    public partial Bitmap? Image { get; set; }

    [ObservableProperty]
    public partial bool IsChecking { get; set; }

    /// <summary>This card's variant inside a group box (e.g. "Midnight Blue"); null when it stands alone.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVariant))]
    public partial string? VariantLabel { get; set; }

    public bool HasVariant => VariantLabel is not null;

    [ObservableProperty]
    public partial string TargetInput { get; set; }

    /// <summary>Re-reads everything derived from <see cref="Product"/> after a price check.</summary>
    public void Refresh()
    {
        _insight = Analyze();
        foreach (var name in DerivedProperties)
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>Only the "Checked … ago" text depends on the clock; the rest is re-read after a check (<see cref="Refresh"/>).</summary>
    public void RefreshClock() => OnPropertyChanged(nameof(CheckedText));

    /// <summary>Applies the typed target price (empty clears it). Returns whether it changed.</summary>
    public bool CommitTarget()
    {
        var text = TargetInput.Replace(" ", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).Replace("kr", "", StringComparison.OrdinalIgnoreCase).Replace(',', '.');
        decimal? target = null;
        if (text.Length > 0)
        {
            if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) || value <= 0)
            {
                TargetInput = Product.TargetPrice?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
                return false;
            }

            target = value;
        }

        if (target == Product.TargetPrice)
        {
            return false;
        }

        Product.TargetPrice = target;
        Refresh();
        return true;
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private DealInsight Analyze() => DealAnalysis.Analyze(Product.CurrentPrice, Product.MarketHistory, Today);

    private static decimal? Min(decimal? a, decimal? b) => a is { } x && b is { } y ? Math.Min(x, y) : a ?? b;

    private static decimal? Max(decimal? a, decimal? b) => a is { } x && b is { } y ? Math.Max(x, y) : a ?? b;

    [RelayCommand]
    private Task SaveTargetAsync() => _owner.SaveTargetAsync(this);

    [RelayCommand]
    private Task CheckAsync() => _owner.CheckOneAsync(this);

    [RelayCommand]
    private Task UnpinAsync() => _owner.UnpinAsync(this);

    [RelayCommand]
    private void Open() => _owner.OpenUrl(PriceRunnerClient.ProductPageUrl(Product.Url));

    [RelayCommand]
    private void OpenStore()
    {
        if (Product.CheapestOfferUrl is { } url)
        {
            _owner.OpenUrl(url);
        }
    }
}
