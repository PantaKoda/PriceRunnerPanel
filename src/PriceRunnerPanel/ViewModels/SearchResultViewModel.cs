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
