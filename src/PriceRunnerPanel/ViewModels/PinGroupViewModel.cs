using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PriceRunnerPanel.Services;

namespace PriceRunnerPanel.ViewModels;

/// <summary>
/// One entry on the watchlist: a product on its own, or a box around the pinned variants (colours) of one product,
/// headed by the product's name and its cheapest variant.
/// </summary>
public sealed class PinGroupViewModel : ObservableObject
{
    public PinGroupViewModel(string title, IReadOnlyList<PinnedItemViewModel> items)
    {
        Title = title;
        Items = items;
        foreach (var item in items)
        {
            item.PropertyChanged += OnItemChanged;
        }
    }

    public string Title { get; }

    public IReadOnlyList<PinnedItemViewModel> Items { get; }

    /// <summary>More than one variant pinned: drawn as a box with a header.</summary>
    public bool IsGroup => Items.Count > 1;

    public string CountText => $"{Items.Count} VARIANTS";

    public string Summary
    {
        get
        {
            var cheapest = Items
                .Where(i => i.Product.CurrentPrice is not null)
                .OrderBy(i => i.Product.CurrentPrice)
                .FirstOrDefault();
            return cheapest is null
                ? ""
                : $"Cheapest: {cheapest.VariantLabel ?? cheapest.Name} · {Format.Price(cheapest.Product.CurrentPrice, cheapest.Product.Currency)}";
        }
    }

    /// <summary>Stops listening to the cards; called when the watchlist is regrouped.</summary>
    public void Detach()
    {
        foreach (var item in Items)
        {
            item.PropertyChanged -= OnItemChanged;
        }
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PinnedItemViewModel.CurrentPriceText) or nameof(PinnedItemViewModel.VariantLabel))
        {
            OnPropertyChanged(nameof(Summary));
        }
    }
}
