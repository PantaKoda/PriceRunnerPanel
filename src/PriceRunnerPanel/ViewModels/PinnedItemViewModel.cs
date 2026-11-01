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
        nameof(TargetHint),
    ];

    private readonly MainViewModel _owner;

    public PinnedItemViewModel(MainViewModel owner, PinnedProduct product)
    {
        _owner = owner;
        Product = product;
        TargetInput = product.TargetPrice is { } target ? target.ToString("0.##", CultureInfo.InvariantCulture) : "";
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

    public string LowText => $"Low {Format.Price(Product.LowestSeen, Product.Currency)}";

    public string HighText => $"High {Format.Price(Product.HighestSeen, Product.Currency)}";

    /// <summary>The price sits at the lowest we have seen, and it has actually moved at some point.</summary>
    public bool IsAtLowest => Product.CurrentPrice is { } now && Product.LowestSeen == now && Product.HighestSeen > now;

    public bool IsBelowTarget => Product.TargetPrice is { } target && Product.CurrentPrice is { } now && now <= target;

    public string StoreText => Product.CheapestMerchant is { Length: > 0 } merchant
        ? $"Cheapest at {merchant}  ·  {Product.OfferCount} offers"
        : Product.OfferCount > 0 ? $"{Product.OfferCount} offers" : Product.Category ?? "";

    public bool HasStoreLink => !string.IsNullOrEmpty(Product.CheapestOfferUrl);

    public string CheckedText => $"Checked {Format.Ago(Product.LastChecked, DateTimeOffset.UtcNow)}";

    public bool HasError => Product.LastError is not null;

    public string ErrorText => Product.LastError ?? "";

    public IReadOnlyList<double> History => [.. Product.History.Select(p => (double)p.Price)];

    public double DealPosition => Product.CurrentPrice is { } now && Product.LowestSeen is { } low && Product.HighestSeen is { } high && high > low
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
        foreach (var name in DerivedProperties)
        {
            OnPropertyChanged(name);
        }
    }

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
