using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PriceRunnerPanel.Models;
using PriceRunnerPanel.Services;

namespace PriceRunnerPanel.ViewModels;

public sealed partial class SearchResultViewModel : ObservableObject
{
    private readonly MainViewModel _owner;

    public SearchResultViewModel(MainViewModel owner, SearchHit hit, bool isPinned)
    {
        _owner = owner;
        Hit = hit;
        IsPinned = isPinned;
    }

    public SearchHit Hit { get; }

    public string Name => Hit.Name;

    public string PriceText => Format.Price(Hit.LowestPrice, Hit.Currency);

    public string Detail
    {
        get
        {
            var parts = new List<string>();
            if (Hit.Category is { } category)
            {
                parts.Add(category);
            }

            if (Hit.StoreCount > 0)
            {
                parts.Add(Hit.StoreCount == 1 ? "1 store" : $"{Hit.StoreCount} stores");
            }

            if (Hit.Rating is { } rating && Hit.RatingCount > 0)
            {
                parts.Add($"★ {rating:0.0} ({Hit.RatingCount})");
            }

            if (Hit.OutOfStock)
            {
                parts.Add("out of stock");
            }

            return string.Join("  ·  ", parts);
        }
    }

    public string? Description => Hit.Description;

    public bool HasRecentDrop => Hit.RecentDropPercent is > 0.5m;

    public string RecentDropText => Hit.RecentDropPercent is { } percent ? $"▼ {Format.Percent(percent)}" : "";

    /// <summary>On a deal filtered by store: the picked store and its own price, e.g. "At CDON: 749 kr (lowest)".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDealStore))]
    public partial string? DealStoreText { get; set; }

    public bool HasDealStore => DealStoreText is not null;

    [ObservableProperty]
    public partial string DealStoreTip { get; set; } = "";

    /// <summary>Shows what the store filter found: its cheapest picked store, or that none of them offers it.</summary>
    public void ShowStorePrice(IReadOnlyList<DealFilterOptionViewModel> picked, IReadOnlyDictionary<int, decimal> prices)
    {
        var best = picked.Where(o => prices.ContainsKey(o.Id)).Select(o => (o.Name, Price: prices[o.Id])).OrderBy(s => s.Price).ToList();
        if (best.Count == 0)
        {
            DealStoreText = $"No new offer at {string.Join(", ", picked.Select(o => o.Name))}";
            DealStoreTip = "PriceRunner lists no new item from Swedish stores for what you picked right now";
            return;
        }

        var (name, price) = best[0];
        var isLowest = Hit.LowestPrice is not { } lowest || price <= lowest;
        DealStoreText = $"At {name}: {Format.Price(price, Hit.Currency)}{(isLowest ? " (lowest)" : "")}";
        DealStoreTip = isLowest
            ? "This store has the lowest price on PriceRunner"
            : $"This store's own price. The {PriceText} on the right is the lowest price at any store.";
    }

    private DealInsight _insight = DealInsight.None;

    public bool HasVerdict => _insight.HasVerdict;

    public string VerdictText => _insight.Label;

    public string VerdictTip => _insight.Explain(Hit.Currency);

    public bool IsGoodDeal => _insight.IsGoodDeal;

    public bool IsInflated => _insight.IsInflated;

    public bool IsOverpriced => _insight.IsOverpriced;

    public string InsightText => _insight.Summary(Hit.Currency);

    public bool HasInsight => InsightText.Length > 0;

    /// <summary>Judges this listing's price against the product's past year. Kept in memory only.</summary>
    public void ShowInsight(IReadOnlyList<MarketPoint> history)
    {
        _insight = DealAnalysis.Analyze(Hit.LowestPrice, history, DateOnly.FromDateTime(DateTime.Now));
        foreach (var name in new[] { nameof(HasVerdict), nameof(VerdictText), nameof(VerdictTip), nameof(IsGoodDeal), nameof(IsInflated), nameof(IsOverpriced), nameof(InsightText), nameof(HasInsight) })
        {
            OnPropertyChanged(name);
        }
    }

    public string? ConditionText => Format.Condition(Hit.Condition);

    public bool HasCondition => ConditionText is not null;

    public bool HasOldPrice => HasRecentDrop && Hit.OldPrice is not null;

    public string OldPriceText => Format.Price(Hit.OldPrice, Hit.Currency);

    [ObservableProperty]
    public partial Bitmap? Image { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinLabel))]
    public partial bool IsPinned { get; set; }

    public string PinLabel => IsPinned ? "PINNED" : "PIN";

    [RelayCommand]
    private Task TogglePinAsync() => _owner.TogglePinAsync(this);

    [RelayCommand]
    private void Open() => _owner.OpenUrl(PriceRunnerClient.ProductPageUrl(Hit.Url));
}
