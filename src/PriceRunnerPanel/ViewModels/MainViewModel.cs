using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PriceRunnerPanel.Models;
using PriceRunnerPanel.Services;

namespace PriceRunnerPanel.ViewModels;

public sealed record IntervalOption(int Minutes)
{
    public string Label => Format.Interval(Minutes);
}

/// <summary>
/// The panel: search PriceRunner, pin products, and check pinned prices on a schedule. A drop of at least the
/// configured size, a new lowest price, or reaching a target price raises a Windows notification.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(450);

    /// <summary>Pause between products during a full check, to stay a polite client.</summary>
    private static readonly TimeSpan BetweenChecks = TimeSpan.FromSeconds(1.5);

    private readonly StateStore _store;
    private readonly PriceRunnerClient _client;
    private readonly PriceTracker _tracker;
    private readonly ImageLoader _images;
    private readonly ToastNotifier _toasts;
    private readonly AppState _state;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _searchCts;
    private bool _checkingAll;
    private bool _groupsDirty;

    public MainViewModel(StateStore store, PriceRunnerClient client, PriceTracker tracker, ImageLoader images, ToastNotifier toasts)
    {
        _store = store;
        _client = client;
        _tracker = tracker;
        _images = images;
        _toasts = toasts;
        _state = store.Load();
        Deals = new DealsViewModel(this, client);
        SearchFilters.Changed += (_, _) => _ = RunSearchAsync(Query, debounce: true);
        Email = new EmailSettingsViewModel(_state.Settings, SaveAsync);
        Telegram = new TelegramSettingsViewModel(_state.Settings, new TelegramSender(new HttpClient()), SaveAsync);
        Email.PropertyChanged += OnAlertChannelChanged;
        Telegram.PropertyChanged += OnAlertChannelChanged;

        foreach (var pin in _state.Pins)
        {
            Pins.Add(CreatePinned(pin));
        }

        Regroup();
        _images.Prune(_state.Pins.Select(p => PriceRunnerClient.ImageUrl(p.ImagePath)));

        SelectedInterval = Intervals.FirstOrDefault(i => i.Minutes == _state.Settings.RefreshMinutes) ?? Intervals[3];
        IsWatchlistTab = Pins.Count > 0;
        IsSearchTab = !IsWatchlistTab;
        SearchStatus = "Type a product name — results come from pricerunner.se";
        Pins.CollectionChanged += (_, _) => OnPinsChanged();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
        UpdateStatus();
    }

    public ObservableCollection<SearchResultViewModel> Results { get; } = [];

    /// <summary>The Black Friday tab.</summary>
    public DealsViewModel Deals { get; }

    public SearchFiltersViewModel SearchFilters { get; } = new();

    public EmailSettingsViewModel Email { get; }

    public TelegramSettingsViewModel Telegram { get; }

    public bool HasAlertChannel => Email.IsConfigured || Telegram.IsConfigured;

    /// <summary>A card's alert switch was used before Telegram or email is set up.</summary>
    public event EventHandler? AlertSetupRequested;

    public ObservableCollection<PinnedItemViewModel> Pins { get; } = [];

    /// <summary>The watchlist as shown: pins grouped so the variants (colours) of one product share a box.</summary>
    public ObservableCollection<PinGroupViewModel> Groups { get; } = [];

    public IReadOnlyList<IntervalOption> Intervals { get; } =
        [new(15), new(30), new(60), new(120), new(240), new(480), new(1440)];

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial bool IsSearching { get; set; }

    [ObservableProperty]
    public partial string SearchStatus { get; set; }

    [ObservableProperty]
    public partial bool IsSearchTab { get; set; }

    [ObservableProperty]
    public partial bool IsDealsTab { get; set; }

    [ObservableProperty]
    public partial bool IsWatchlistTab { get; set; }

    [ObservableProperty]
    public partial IntervalOption SelectedInterval { get; set; }

    [ObservableProperty]
    public partial bool IsChecking { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial string FooterText { get; set; } = "";

    public string WatchlistTabLabel => Pins.Count > 0 ? $"WATCHLIST · {Pins.Count}" : "WATCHLIST";

    public bool HasPins => Pins.Count > 0;

    public bool HasResults => Results.Count > 0;

    /// <summary>A newer release exists: the header shows the UPDATE pill.</summary>
    [ObservableProperty]
    public partial bool UpdateAvailable { get; set; }

    [ObservableProperty]
    public partial string UpdateTooltip { get; set; } = "";

    /// <summary>The UPDATE pill was clicked; the app opens the update window.</summary>
    public event EventHandler? UpdateRequested;

    [RelayCommand]
    private void OpenUpdate() => UpdateRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>This copy's version, shown in the footer.</summary>
    public string VersionText => $"v{AppInfo.Version}";

    /// <summary>The footer's "Check for updates" link was clicked; the app checks now and opens the update window.</summary>
    public event EventHandler? CheckForUpdatesRequested;

    [RelayCommand]
    private void CheckForUpdates() => CheckForUpdatesRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Records that <paramref name="version"/> was announced; false when it already was.</summary>
    public bool MarkUpdateNotified(string version)
    {
        if (_state.Settings.NotifiedUpdate == version)
        {
            return false;
        }

        _state.Settings.NotifiedUpdate = version;
        _ = SaveAsync();
        return true;
    }

    public void ShowMessage(string message) => FooterText = message;

    public bool StartWithWindows => _state.Settings.StartWithWindows;

    /// <summary>Turns sign-in startup on or off. Returns the resulting state (unchanged if Windows refused).</summary>
    public bool SetStartWithWindows(StartupRegistration startup, bool enabled)
    {
        if (!startup.Set(enabled))
        {
            FooterText = "Windows refused to change the startup setting";
            return _state.Settings.StartWithWindows;
        }

        _state.Settings.StartWithWindows = enabled;
        FooterText = enabled ? "Starts with Windows (hidden in the tray)" : "No longer starts with Windows";
        OnPropertyChanged(nameof(StartWithWindows));
        _ = SaveAsync();
        return enabled;
    }

    /// <summary>
    /// Adds the Start menu shortcut the first time, and keeps it pointing at this copy if the app moved. A shortcut
    /// the user deleted is not brought back.
    /// </summary>
    public void SyncStartMenuShortcut(StartMenuShortcut shortcut)
    {
        var deleted = _state.Settings.StartMenuShortcutCreated && !shortcut.Exists;
        if (deleted || shortcut.IsCurrent || !shortcut.Save())
        {
            return;
        }

        _state.Settings.StartMenuShortcutCreated = true;
        _ = SaveAsync();
    }

    /// <summary>The tray and the window's first show call this: checks right away when the last check is stale.</summary>
    public void Start()
    {
        if (IsDue())
        {
            _ = CheckAllAsync();
        }
        else if (Pins.Any(p => !p.Product.GroupResolved))
        {
            _ = ResolveMissingGroupsAsync();
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        Deals.Dispose();
    }

    public void OpenUrl(string url)
    {
        // Only plain https links reach the browser: no other schemes, no "user@host" disguises.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            FooterText = "Could not open the browser";
        }
    }

    /// <summary>Raised when the search tab opens, so the view can focus the search box.</summary>
    public event EventHandler? SearchRequested;

    [RelayCommand]
    private void ShowSearch()
    {
        SetTab(search: true);
        SearchRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ShowDeals()
    {
        SetTab(deals: true);
        Deals.OnOpened();
    }

    [RelayCommand]
    private void ShowWatchlist() => SetTab(watchlist: true);

    // A click on the already-selected tab toggles the button off locally; re-announcing the values puts it back.
    private void SetTab(bool search = false, bool deals = false, bool watchlist = false)
    {
        IsSearchTab = search;
        IsDealsTab = deals;
        IsWatchlistTab = watchlist;
        OnPropertyChanged(nameof(IsSearchTab));
        OnPropertyChanged(nameof(IsDealsTab));
        OnPropertyChanged(nameof(IsWatchlistTab));
    }

    [RelayCommand]
    private Task SearchAsync() => RunSearchAsync(Query, debounce: false);

    [RelayCommand]
    private void ClearSearch()
    {
        Query = "";
    }

    [RelayCommand]
    private void ClearSearchFilters() => SearchFilters.Clear();

    [RelayCommand]
    public async Task CheckAllAsync()
    {
        if (_checkingAll || Pins.Count == 0)
        {
            return;
        }

        _checkingAll = true;
        IsChecking = true;
        try
        {
            var pins = Pins.ToList();
            for (var i = 0; i < pins.Count; i++)
            {
                StatusText = $"CHECKING {i + 1}/{pins.Count}";
                if (Pins.Contains(pins[i]))
                {
                    await CheckCoreAsync(pins[i]);
                }

                if (i < pins.Count - 1)
                {
                    await Task.Delay(BetweenChecks);
                }
            }

            _state.LastRefresh = DateTimeOffset.UtcNow;
            RegroupIfNeeded();
            await SaveAsync();
        }
        finally
        {
            _checkingAll = false;
            IsChecking = false;
            UpdateStatus();
        }
    }

    internal async Task CheckOneAsync(PinnedItemViewModel item)
    {
        await CheckCoreAsync(item);
        RegroupIfNeeded();
        await SaveAsync();
        UpdateStatus();
    }

    internal async Task TogglePinAsync(SearchResultViewModel result)
    {
        if (Pins.FirstOrDefault(p => p.Id == result.Hit.Id) is { } existing)
        {
            await UnpinAsync(existing);
            return;
        }

        var hit = result.Hit;
        var now = DateTimeOffset.UtcNow;
        var product = new PinnedProduct
        {
            Id = hit.Id,
            Name = hit.Name,
            Url = hit.Url,
            ImagePath = hit.ImagePath,
            Category = hit.Category,
            Currency = hit.Currency,
            PinnedAt = now,
            PinnedPrice = hit.LowestPrice ?? 0,
            CurrentPrice = hit.LowestPrice,
            LowestSeen = hit.LowestPrice,
            HighestSeen = hit.LowestPrice,
            LowestSeenAt = now,
            ReportedDropPercent = hit.RecentDropPercent,
            ReportedOldPrice = hit.OldPrice,
            ReportedDropFetched = now,
        };
        if (hit.LowestPrice is { } price)
        {
            product.History.Add(new PricePoint(now, price));
        }

        _state.Pins.Insert(0, product);
        var item = CreatePinned(product);
        item.Image = result.Image;
        Pins.Insert(0, item);
        ShowPinned(hit.Id, pinned: true);
        FooterText = $"Pinned {hit.Name}";
        await SaveAsync();

        // The first check sets the baseline from the live offers (in-stock price, cheapest store).
        if (await CheckCoreAsync(item, notify: false) is not null && product.CurrentPrice is { } live)
        {
            product.PinnedPrice = live;
            product.LowestSeen = product.HighestSeen = live;
            product.History = [new PricePoint(DateTimeOffset.UtcNow, live)];
            item.Refresh();
        }

        RegroupIfNeeded();
        await SaveAsync();
        UpdateStatus();
    }

    /// <summary>
    /// Stops tracking and forgets the product: its prices, history and target leave state.json, and its picture
    /// and notifications go too. Nothing about it is kept.
    /// </summary>
    internal async Task UnpinAsync(PinnedItemViewModel item)
    {
        Pins.Remove(item);
        _state.Pins.Remove(item.Product);
        ShowPinned(item.Id, pinned: false);

        if (!_state.Pins.Any(p => p.ImagePath == item.Product.ImagePath))
        {
            _images.Delete(PriceRunnerClient.ImageUrl(item.Product.ImagePath));
        }

        _toasts.Remove(item.Id);
        FooterText = $"Unpinned {item.Name} and deleted its data";
        await SaveAsync();
        UpdateStatus();
    }

    internal async Task SaveTargetAsync(PinnedItemViewModel item)
    {
        if (item.CommitTarget())
        {
            FooterText = item.Product.TargetPrice is { } target
                ? $"Alert set at {Format.Price(target, item.Product.Currency)}"
                : "Target alert removed";
            await SaveAsync();
        }
    }

    partial void OnQueryChanged(string value) => _ = RunSearchAsync(value, debounce: true);

    partial void OnSelectedIntervalChanged(IntervalOption value)
    {
        if (_state.Settings.RefreshMinutes != value.Minutes)
        {
            _state.Settings.RefreshMinutes = value.Minutes;
            _ = SaveAsync();
            UpdateStatus();
        }
    }

    private async Task RunSearchAsync(string query, bool debounce)
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        var cts = _searchCts = new CancellationTokenSource();
        query = query.Trim();
        if (query.Length < 2)
        {
            if (query.Length == 0)
            {
                ClearResults();
                SearchFilters.Reset();
                OnPropertyChanged(nameof(HasResults));
                SearchStatus = "Type a product name — results come from pricerunner.se";
            }

            IsSearching = false;
            return;
        }

        try
        {
            if (debounce)
            {
                await Task.Delay(SearchDebounce, cts.Token);
            }

            IsSearching = true;
            SearchStatus = $"Searching for “{query}”…";
            var page = await _client.SearchAsync(query, SearchFilters.Current, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            SearchFilters.Offer(query, page.Facets);
            ClearResults();
            foreach (var hit in page.Hits)
            {
                Results.Add(CreateResultRow(hit));
            }

            OnPropertyChanged(nameof(HasResults));
            var found = page.Hits.Count == 1 ? "1 product" : $"{page.Hits.Count} products";
            SearchStatus = page.Hits.Count == 0
                ? SearchFilters.HasFilters ? $"No products match “{query}” with these filters" : $"No products found for “{query}”"
                : SearchFilters.HasStoreFilter
                    ? $"{found} sold there  ·  the price shown is the lowest at any store"
                    : $"{found}  ·  pin one to track its price";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is HttpRequestException or PriceRunnerException or TaskCanceledException or System.Text.Json.JsonException)
        {
            SearchStatus = ex is TaskCanceledException ? "PriceRunner did not answer in time" : $"Search failed: {ex.Message}";
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                IsSearching = false;
            }
        }
    }

    private async Task<PriceCheck?> CheckCoreAsync(PinnedItemViewModel item, bool notify = true)
    {
        item.IsChecking = true;
        try
        {
            await ResolveGroupAsync(item.Product);
            var check = await _tracker.CheckAsync(item.Product, CancellationToken.None);
            item.Refresh();
            if (notify && check is not null)
            {
                Notify(item, check);
            }

            return check;
        }
        finally
        {
            item.IsChecking = false;
        }
    }

    private void Notify(PinnedItemViewModel item, PriceCheck check)
    {
        var product = item.Product;
        var insight = item.Insight;
        var price = Format.Price(check.Current, product.Currency);
        var where = check.Merchant is { Length: > 0 } merchant ? $" at {merchant}" : "";
        // With PriceRunner's history, "new low" means below anything in the past year, not just since pinning.
        var isNewLow = check.DropPercent is not null && (insight.YearLow is { } yearLow ? check.Current < yearLow.Price : check.IsNewLow);
        string? title = null;
        if (check.ReachedTarget)
        {
            title = "🎯 Target price reached";
        }
        else if (insight.Verdict == DealVerdict.Inflated && check.DropPercent is { } undone)
        {
            // The drop only takes back a recent price rise: not worth a notification.
            FooterText = $"Ignored −{Format.Percent(undone)} on {product.Name}: it only undoes a recent price rise";
            return;
        }
        else if (isNewLow)
        {
            title = insight.YearLow is not null ? "⬇ Lowest price in a year" : "⬇ New lowest price";
        }
        else if (check.DropPercent is { } drop && drop >= _state.Settings.MinDropPercent)
        {
            title = $"⬇ Price drop −{Format.Percent(drop)}";
        }

        if (title is null)
        {
            return;
        }

        var was = check.Previous is { } previous ? $" (was {Format.Price(previous, product.Currency)})" : "";
        // e.g. "REAL DEAL · Typical 4 490 kr · 1y low 3 069 kr (CDON, 27 Aug)"
        var context = item.HasVerdict ? $"\n{item.VerdictText.TrimStart('✓', ' ')}  ·  {item.InsightText}" : item.HasInsight ? $"\n{item.InsightText}" : "";
        FooterText = $"{title}: {product.Name}";
        var url = PriceRunnerClient.ProductPageUrl(product.Url);
        var body = $"{product.Name}\n{price}{where}{was}{context}";
        _toasts.Show(title, body, product.Id, () => OpenUrl(url));
        if (product.RemoteAlerts)
        {
            var text = $"{body}\n\n{url}";
            _ = SendRemoteAlertAsync("Telegram", () => Telegram.SendAsync($"{title}\n{text}"));
            _ = SendRemoteAlertAsync("Email", () => Email.SendAsync($"{title}: {product.Name}", text));
        }
    }

    /// <summary>A failed alert isn't sent again: the toast has shown it, and the next drop sends a new one.</summary>
    private async Task SendRemoteAlertAsync(string channel, Func<Task> send)
    {
        try
        {
            await send();
        }
        catch (Exception ex) when (AlertFailure.Is(ex))
        {
            var reason = AlertFailure.Describe(ex);
            FooterText = $"{channel} alert failed: {reason}";
            _toasts.Show($"{channel} alert failed", reason, $"{channel}-failed", null);
        }
    }

    private void OnAlertChannelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EmailSettingsViewModel.IsConfigured))
        {
            OnPropertyChanged(nameof(HasAlertChannel));
        }
    }

    internal async Task ToggleRemoteAlertsAsync(PinnedItemViewModel item)
    {
        if (!item.Product.RemoteAlerts && !HasAlertChannel)
        {
            AlertSetupRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        item.Product.RemoteAlerts = !item.Product.RemoteAlerts;
        item.RefreshRemoteAlerts();
        FooterText = $"{item.RemoteAlertsLabel}: {item.Name}";
        await SaveAsync();
    }

    /// <summary>Drops the search results and lets go of their pictures, like the Black Friday tab does with its rows.</summary>
    private void ClearResults()
    {
        foreach (var row in Results)
        {
            ReleaseImage(row.Hit.ImagePath);
        }

        Results.Clear();
    }

    /// <summary>A product row for the search or Black Friday tab, with its picture on the way.</summary>
    internal SearchResultViewModel CreateResultRow(SearchHit hit)
    {
        var row = new SearchResultViewModel(this, hit, Pins.Any(p => p.Id == hit.Id));
        _ = LoadImageAsync(hit.ImagePath, bitmap => row.Image = bitmap);
        return row;
    }

    internal void ReleaseImage(string? imagePath) => _images.Release(PriceRunnerClient.ImageUrl(imagePath));

    /// <summary>A product can be listed on both the search and the Black Friday tab; both show its pin.</summary>
    private void ShowPinned(string id, bool pinned)
    {
        foreach (var row in Results.Concat(Deals.Items).Where(r => r.Hit.Id == id))
        {
            row.IsPinned = pinned;
        }
    }

    private PinnedItemViewModel CreatePinned(PinnedProduct product)
    {
        var item = new PinnedItemViewModel(this, product);
        _ = LoadImageAsync(product.ImagePath, bitmap => item.Image ??= bitmap);
        return item;
    }

    private async Task LoadImageAsync(string? imagePath, Action<Avalonia.Media.Imaging.Bitmap> apply)
    {
        if (await _images.LoadAsync(PriceRunnerClient.ImageUrl(imagePath)) is { } bitmap)
        {
            Dispatcher.UIThread.Post(() => apply(bitmap));
        }
    }

    private void OnPinsChanged()
    {
        OnPropertyChanged(nameof(WatchlistTabLabel));
        OnPropertyChanged(nameof(HasPins));
        Regroup();
    }

    /// <summary>Looks up which variant group a pin belongs to, once. A failure is retried with the next check.</summary>
    private async Task ResolveGroupAsync(PinnedProduct pin)
    {
        if (pin.GroupResolved)
        {
            return;
        }

        try
        {
            var group = await _client.GetProductGroupAsync(pin.Id, pin.Url, CancellationToken.None);
            pin.GroupKey = group?.Key;
            pin.GroupName = group?.Name;
            pin.Variant = group is not null && group.Variants.TryGetValue(pin.Id, out var variant) ? variant : null;
            pin.GroupResolved = true;
            _groupsDirty = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or PriceRunnerException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            // Unexpected data shape or network trouble: no grouping for now, the next check tries again.
        }
    }

    /// <summary>Pins from before grouping existed: look their groups up without waiting for the next price check.</summary>
    private async Task ResolveMissingGroupsAsync()
    {
        foreach (var item in Pins.Where(p => !p.Product.GroupResolved).ToList())
        {
            await ResolveGroupAsync(item.Product);
        }

        RegroupIfNeeded();
        await SaveAsync();
    }

    private void RegroupIfNeeded()
    {
        if (_groupsDirty)
        {
            Regroup();
        }
    }

    private void Regroup()
    {
        _groupsDirty = false;
        foreach (var group in Groups)
        {
            group.Detach();
        }

        var items = Pins.ToDictionary(p => p.Product);
        Groups.Clear();
        foreach (var cluster in ProductGrouping.Cluster([.. Pins.Select(p => p.Product)]))
        {
            var members = cluster.Members.Select(m => items[m.Pin]).ToList();
            foreach (var (member, (pin, variant)) in members.Zip(cluster.Members))
            {
                member.VariantLabel = members.Count > 1 ? variant ?? pin.Name : null;
            }

            Groups.Add(new PinGroupViewModel(cluster.Title, members));
        }
    }

    private bool IsDue() =>
        Pins.Count > 0 && (_state.LastRefresh is not { } last || DateTimeOffset.UtcNow - last >= TimeSpan.FromMinutes(_state.Settings.RefreshMinutes));

    private void OnTick()
    {
        if (IsDue())
        {
            _ = CheckAllAsync();
        }
        else if (!IsChecking)
        {
            foreach (var pin in Pins)
            {
                pin.RefreshClock();
            }

            UpdateStatus();
        }
    }

    private void UpdateStatus()
    {
        if (IsChecking)
        {
            return;
        }

        if (Pins.Count == 0)
        {
            StatusText = "NOTHING PINNED";
            return;
        }

        var next = (_state.LastRefresh ?? DateTimeOffset.UtcNow) + TimeSpan.FromMinutes(_state.Settings.RefreshMinutes) - DateTimeOffset.UtcNow;
        var nextText = next <= TimeSpan.Zero ? "NOW" : next.TotalMinutes < 60 ? $"{Math.Ceiling(next.TotalMinutes)}M" : $"{next.TotalHours:0.#}H";
        StatusText = $"{Pins.Count} PINNED · NEXT {nextText}";
        if (string.IsNullOrEmpty(FooterText) || FooterText.StartsWith("Last check", StringComparison.Ordinal))
        {
            FooterText = $"Last check {Format.Ago(_state.LastRefresh, DateTimeOffset.UtcNow)}";
        }
    }

    private Task SaveAsync() => _store.SaveAsync(_state);
}
