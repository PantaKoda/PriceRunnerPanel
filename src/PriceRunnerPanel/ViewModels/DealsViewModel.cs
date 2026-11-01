using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PriceRunnerPanel.Services;

namespace PriceRunnerPanel.ViewModels;

/// <summary>
/// The Black Friday tab: PriceRunner's current deals, filtered by category and store. Nothing here is saved; deals
/// are fetched when the tab opens and live in memory until a product is pinned to the watchlist.
/// </summary>
public sealed partial class DealsViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan FilterDebounce = TimeSpan.FromMilliseconds(400);

    /// <summary>Deals change through the day: reopening the tab after this long fetches them again.</summary>
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(30);

    private readonly MainViewModel _owner;
    private readonly PriceRunnerClient _client;
    private CancellationTokenSource? _cts;
    private DateTimeOffset? _loadedAt;
    private int _offset;
    private int _total;

    public DealsViewModel(MainViewModel owner, PriceRunnerClient client)
    {
        _owner = owner;
        _client = client;
        CategoryFilter.SelectionChanged += (_, _) => OnFiltersChanged();
        MerchantFilter.SelectionChanged += (_, _) => OnFiltersChanged();
    }

    public DealFilterViewModel CategoryFilter { get; } = new("Category", "Find a category");

    public DealFilterViewModel MerchantFilter { get; } = new("Store", "Find a store");

    public ObservableCollection<SearchResultViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    public bool CanLoadMore => !IsLoading && Items.Count > 0 && Items.Count < _total;

    public bool HasFilters => CategoryFilter.HasSelection || MerchantFilter.HasSelection;

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
        OnPropertyChanged(nameof(HasFilters));
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
            var page = await _client.GetDealsAsync(CategoryFilter.SelectedIds, MerchantFilter.SelectedIds, offset, cts.Token);
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

    private void OnFiltersChanged()
    {
        OnPropertyChanged(nameof(HasFilters));
        _ = ReloadAsync(debounce: true);
    }

    private async Task ReloadAsync(bool debounce)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = _cts = new CancellationTokenSource();
        try
        {
            IsLoading = true;
            OnPropertyChanged(nameof(CanLoadMore));
            if (debounce)
            {
                await Task.Delay(FilterDebounce, cts.Token);
            }

            Status = "Fetching deals…";
            var categories = CategoryFilter.SelectedIds;
            var merchants = MerchantFilter.SelectedIds;
            var pageTask = _client.GetDealsAsync(categories, merchants, 0, cts.Token);
            var facetsTask = _client.GetDealFacetsAsync(categories, merchants, cts.Token);
            await Task.WhenAll(pageTask, facetsTask);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            var page = await pageTask;
            var facets = await facetsTask;
            CategoryFilter.SetOptions(facets.Categories);
            MerchantFilter.SetOptions(facets.Merchants);

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

    private void Append(IEnumerable<Models.SearchHit> hits)
    {
        var shown = Items.Select(r => r.Hit.Id).ToHashSet();
        foreach (var hit in hits.Where(h => shown.Add(h.Id)))
        {
            Items.Add(_owner.CreateResultRow(hit));
        }
    }

    private void ShowCount()
    {
        Status = _total == 0
            ? "No deals match these filters"
            : $"{(_total >= 10000 ? "10 000+" : Format.Count(_total))} deals  ·  pin one to see if the discount is real";
    }

    private static bool IsFetchFailure(Exception ex) =>
        ex is HttpRequestException or PriceRunnerException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException;

    private static string Failure(Exception ex) =>
        ex is TaskCanceledException ? "PriceRunner did not answer in time" : $"Could not fetch deals: {ex.Message}";
}
