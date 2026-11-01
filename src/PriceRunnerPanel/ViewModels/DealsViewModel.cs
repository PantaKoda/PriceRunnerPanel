using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PriceRunnerPanel.Models;
using PriceRunnerPanel.Services;

namespace PriceRunnerPanel.ViewModels;

/// <summary>
/// The Black Friday tab: PriceRunner's current deals, filtered by category, store and brand. Nothing here is saved; deals
/// are fetched when the tab opens and live in memory until a product is pinned to the watchlist.
/// </summary>
public sealed partial class DealsViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan FilterDebounce = TimeSpan.FromMilliseconds(400);

    /// <summary>Deals change through the day: reopening the tab after this long fetches them again.</summary>
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(30);

    /// <summary>Pause between requests when filling in listings, to stay a polite client.</summary>
    private static readonly TimeSpan BetweenDetails = TimeSpan.FromMilliseconds(300);

    /// <summary>PriceRunner's deals start at a 10 % drop, and its ranges go up to 90 %.</summary>
    private const int DefaultMinDrop = 10;
    private const int MaxDropLimit = 90;

    private readonly MainViewModel _owner;
    private readonly PriceRunnerClient _client;
    private readonly Queue<(SearchResultViewModel Row, IReadOnlyList<DealFilterOptionViewModel> Picked)> _detailsPending = new();
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _detailsWorker;
    private DateTimeOffset? _loadedAt;
    private bool _settingDrop;
    private int _offset;
    private int _total;

    public DealsViewModel(MainViewModel owner, PriceRunnerClient client)
    {
        _owner = owner;
        _client = client;
        CategoryFilter.SelectionChanged += (_, _) => OnFiltersChanged();
        MerchantFilter.SelectionChanged += (_, _) => OnFiltersChanged();
        BrandFilter.SelectionChanged += (_, _) => OnFiltersChanged();
        DropPresets = [new(this, 10), new(this, 20), new(this, 30), new(this, 50)];
    }

    public DealFilterViewModel CategoryFilter { get; } = new("Category", "Find a category", DealFilterKind.Category);

    public DealFilterViewModel MerchantFilter { get; } = new("Store", "Find a store", DealFilterKind.Store);

    public DealFilterViewModel BrandFilter { get; } = new("Brand", "Find a brand", DealFilterKind.Brand);

    public DealRefineRow BrandRow => new("Brands", BrandFilter);

    public DealRefineRow StoreRow => new("Stores", MerchantFilter);

    /// <summary>Everything ticked in either filter, shown as chips that remove it.</summary>
    public ObservableCollection<DealFilterOptionViewModel> ActiveFilters { get; } = [];

    public ObservableCollection<SearchResultViewModel> Items { get; } = [];

    /// <summary>The discount range as typed, in percent.</summary>
    [ObservableProperty]
    public partial string MinDropText { get; set; } = DefaultMinDrop.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    public partial string MaxDropText { get; set; } = MaxDropLimit.ToString(CultureInfo.InvariantCulture);

    public IReadOnlyList<DealDropPreset> DropPresets { get; }

    /// <summary>The range to ask for: anything unreadable counts as its default, and a reversed range is turned around.</summary>
    public (int Min, int Max) DropRange
    {
        get
        {
            var min = ReadPercent(MinDropText, DefaultMinDrop);
            var max = ReadPercent(MaxDropText, MaxDropLimit);
            return min <= max ? (min, max) : (max, min);
        }
    }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    public bool CanLoadMore => !IsLoading && Items.Count > 0 && Items.Count < _total;

    public bool HasFilters => CategoryFilter.HasSelection || MerchantFilter.HasSelection || BrandFilter.HasSelection || DropRange != (DefaultMinDrop, MaxDropLimit);

    /// <summary>The tab was opened: fetches the deals the first time, and again when they have gone stale.</summary>
    public void OnOpened()
    {
        if (_loadedAt is not { } at || DateTimeOffset.UtcNow - at >= Stale)
        {
            _ = ReloadAsync(debounce: false);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }

    [RelayCommand]
    private Task RefreshAsync() => ReloadAsync(debounce: false);

    [RelayCommand]
    private Task ClearFiltersAsync()
    {
        CategoryFilter.ClearSilently();
        MerchantFilter.ClearSilently();
        BrandFilter.ClearSilently();
        SetDropRange(DefaultMinDrop, MaxDropLimit);
        UpdateActiveFilters();
        return ReloadAsync(debounce: false);
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (IsLoading || _cts is not { } cts)
        {
            return;
        }

        IsLoading = true;
        OnPropertyChanged(nameof(CanLoadMore));
        try
        {
            var offset = _offset + PriceRunnerClient.DealsPageSize;
            var page = await _client.GetDealsAsync(CurrentFilter(), offset, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            _offset = offset;
            _total = page.Hits.Count == 0 ? Items.Count : page.Total;
            Append(page.Hits);
            ShowCount();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (IsFetchFailure(ex))
        {
            Status = Failure(ex);
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                IsLoading = false;
                OnPropertyChanged(nameof(CanLoadMore));
            }
        }
    }

    internal void ApplyDropPreset(int min)
    {
        SetDropRange(min, MaxDropLimit);
        OnFiltersChanged();
    }

    partial void OnMinDropTextChanged(string value) => OnDropTyped();

    partial void OnMaxDropTextChanged(string value) => OnDropTyped();

    private void OnDropTyped()
    {
        if (!_settingDrop)
        {
            OnFiltersChanged();
        }
    }

    private void SetDropRange(int min, int max)
    {
        _settingDrop = true;
        MinDropText = min.ToString(CultureInfo.InvariantCulture);
        MaxDropText = max.ToString(CultureInfo.InvariantCulture);
        _settingDrop = false;
    }

    private static int ReadPercent(string text, int fallback) =>
        int.TryParse(text.Replace("%", "", StringComparison.Ordinal).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var percent)
            ? Math.Clamp(percent, 1, MaxDropLimit)
            : fallback;

    private DealFilter CurrentFilter()
    {
        var (min, max) = DropRange;
        return new DealFilter(CategoryFilter.SelectedIds, MerchantFilter.SelectedIds, BrandFilter.SelectedIds, min, max);
    }

    private void OnFiltersChanged()
    {
        UpdateActiveFilters();
        _ = ReloadAsync(debounce: true);
    }

    private async Task ReloadAsync(bool debounce)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = _cts = new CancellationTokenSource();
        _detailsPending.Clear();
        try
        {
            IsLoading = true;
            OnPropertyChanged(nameof(CanLoadMore));
            if (debounce)
            {
                await Task.Delay(FilterDebounce, cts.Token);
            }

            Status = "Fetching deals…";
            var filter = CurrentFilter();
            var pageTask = _client.GetDealsAsync(filter, 0, cts.Token);
            var facetsTask = _client.GetDealFacetsAsync(filter, cts.Token);
            await Task.WhenAll(pageTask, facetsTask);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            var page = await pageTask;
            var facets = await facetsTask;
            CategoryFilter.SetOptions(facets.Categories);
            MerchantFilter.SetOptions(facets.Merchants);
            BrandFilter.SetOptions(facets.Brands);

            foreach (var row in Items)
            {
                _owner.ReleaseImage(row.Hit.ImagePath);
            }

            Items.Clear();
            _offset = 0;
            _total = page.Total;
            Append(page.Hits);
            _loadedAt = DateTimeOffset.UtcNow;
            ShowCount();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (IsFetchFailure(ex))
        {
            Status = Failure(ex);
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                IsLoading = false;
                OnPropertyChanged(nameof(CanLoadMore));
            }
        }
    }

    private void Append(IEnumerable<SearchHit> hits)
    {
        var shown = Items.Select(r => r.Hit.Id).ToHashSet();
        var picked = MerchantFilter.Selected;
        foreach (var hit in hits.Where(h => shown.Add(h.Id)))
        {
            var row = _owner.CreateResultRow(hit);
            Items.Add(row);
            if (picked.Count > 0)
            {
                row.DealStoreText = $"At {string.Join(", ", picked.Select(o => o.Name))}…";
            }

            if (HasFilters)
            {
                _detailsPending.Enqueue((row, picked));
            }
        }

        if (_detailsPending.Count > 0 && _cts is { } cts && _detailsWorker != cts)
        {
            _ = LoadDetailsAsync(cts);
        }
    }

    /// <summary>
    /// Fills in what the deals list doesn't carry, only while a filter narrows the list: each product's past year for
    /// the verdict, and with stores picked, what they ask, which also tells which of several has the listed price.
    /// One product at a time with a pause, top of the list first.
    /// </summary>
    private async Task LoadDetailsAsync(CancellationTokenSource cts)
    {
        _detailsWorker = cts;
        try
        {
            while (!cts.IsCancellationRequested && _detailsPending.TryDequeue(out var next))
            {
                var (row, picked) = next;
                if (picked.Count > 0)
                {
                    try
                    {
                        row.ShowStorePrice(picked, await _client.GetStorePricesAsync(row.Hit.Id, cts.Token));
                    }
                    catch (Exception ex) when (IsFetchFailure(ex) && !cts.IsCancellationRequested)
                    {
                        row.DealStoreText = $"At {string.Join(", ", picked.Select(o => o.Name))}";
                        row.DealStoreTip = "Couldn't fetch this store's own price";
                    }

                    await Task.Delay(BetweenDetails, cts.Token);
                }

                try
                {
                    row.ShowInsight(await _client.GetPriceHistoryAsync(row.Hit.Id, cts.Token));
                }
                catch (Exception ex) when (IsFetchFailure(ex) && !cts.IsCancellationRequested)
                {
                    // No verdict for this listing; pinning it tries again.
                }

                await Task.Delay(BetweenDetails, cts.Token);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // The filters changed; the rows this was for are gone.
        }
        finally
        {
            if (_detailsWorker == cts)
            {
                _detailsWorker = null;
            }
        }
    }

    private void UpdateActiveFilters()
    {
        ActiveFilters.Clear();
        foreach (var option in CategoryFilter.Selected.Concat(MerchantFilter.Selected).Concat(BrandFilter.Selected))
        {
            ActiveFilters.Add(option);
        }

        OnPropertyChanged(nameof(HasFilters));
        foreach (var preset in DropPresets)
        {
            preset.Refresh();
        }
    }

    private void ShowCount()
    {
        Status = _total == 0
            ? "No deals match these filters"
            : $"{(_total >= 10000 ? "10 000+" : Format.Count(_total))} deals  ·  pin one to track its price";
    }

    private static bool IsFetchFailure(Exception ex) =>
        ex is HttpRequestException or PriceRunnerException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException;

    private static string Failure(Exception ex) =>
        ex is TaskCanceledException ? "PriceRunner did not answer in time" : $"Could not fetch deals: {ex.Message}";
}
