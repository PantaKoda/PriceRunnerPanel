using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PriceRunnerPanel.Models;

namespace PriceRunnerPanel.ViewModels;

/// <summary>
/// The Search tab's filters. PriceRunner offers the choices with each search's answer, up to 8 of a kind, and stops
/// offering a kind once it is in use (a brand stops them all). So the last choices stay on offer for as long as the
/// search text is the same, which is what lets a second brand or store be ticked.
/// </summary>
public sealed partial class SearchFiltersViewModel : ObservableObject
{
    private string _query = "";

    public SearchFiltersViewModel()
    {
        CategoryFilter.SelectionChanged += (_, _) => OnChanged();
        BrandFilter.SelectionChanged += (_, _) => OnChanged();
        MerchantFilter.SelectionChanged += (_, _) => OnChanged();
    }

    /// <summary>The ticked choices changed, so the search should run again.</summary>
    public event EventHandler? Changed;

    public DealFilterViewModel CategoryFilter { get; } = new("Category", "Find a category", DealFilterKind.Category, singleChoice: true, hasCounts: false);

    public DealFilterViewModel BrandFilter { get; } = new("Brand", "Find a brand", DealFilterKind.Brand, hasCounts: false);

    public DealFilterViewModel MerchantFilter { get; } = new("Store", "Find a store", DealFilterKind.Store, hasCounts: false);

    /// <summary>Everything ticked, shown as chips that remove it.</summary>
    public ObservableCollection<DealFilterOptionViewModel> ActiveFilters { get; } = [];

    public bool HasFilters => ActiveFilters.Count > 0;

    public bool HasStoreFilter => MerchantFilter.HasSelection;

    public bool HasOptions => CategoryFilter.HasOptions || BrandFilter.HasOptions || MerchantFilter.HasOptions;

    public SearchFilter Current => new(CategoryFilter.SelectedIds is [var category, ..] ? category : null, MerchantFilter.SelectedIds, BrandFilter.SelectedIds);

    /// <summary>Takes the choices that came with a search's answer.</summary>
    public void Offer(string query, DealFacets facets)
    {
        var sameQuery = string.Equals(query, _query, StringComparison.Ordinal);
        _query = query;
        Offer(CategoryFilter, facets.Categories, sameQuery);
        Offer(BrandFilter, facets.Brands, sameQuery);
        Offer(MerchantFilter, facets.Merchants, sameQuery);
        OnPropertyChanged(nameof(HasOptions));
    }

    /// <summary>The search text is gone: nothing ticked, nothing on offer.</summary>
    public void Reset()
    {
        _query = "";
        foreach (var filter in new[] { CategoryFilter, BrandFilter, MerchantFilter })
        {
            filter.ClearSilently();
            filter.SetOptions([]);
        }

        ActiveFilters.Clear();
        OnPropertyChanged(nameof(HasFilters));
        OnPropertyChanged(nameof(HasOptions));
    }

    public void Clear()
    {
        CategoryFilter.ClearSilently();
        BrandFilter.ClearSilently();
        MerchantFilter.ClearSilently();
        OnChanged();
    }

    private static void Offer(DealFilterViewModel filter, IReadOnlyList<DealFacetOption> options, bool sameQuery)
    {
        if (options.Count > 0 || !sameQuery)
        {
            filter.SetOptions(options);
        }
    }

    private void OnChanged()
    {
        ActiveFilters.Clear();
        foreach (var option in CategoryFilter.Selected.Concat(BrandFilter.Selected).Concat(MerchantFilter.Selected))
        {
            ActiveFilters.Add(option);
        }

        OnPropertyChanged(nameof(HasFilters));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
