using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PriceRunnerPanel.Models;
using PriceRunnerPanel.Services;

namespace PriceRunnerPanel.ViewModels;

public enum DealFilterKind
{
    Category,
    Store,
    Brand,
}

/// <summary>A quick discount choice on the Black Friday tab, e.g. "30 %+".</summary>
public sealed partial class DealDropPreset(DealsViewModel owner, int percent) : ObservableObject
{
    public string Label => $"{percent} %+";

    public bool IsActive => owner.DropRange == (percent, 90);

    public void Refresh() => OnPropertyChanged(nameof(IsActive));

    [RelayCommand]
    private void Apply() => owner.ApplyDropPreset(percent);
}

/// <summary>A row of the refine box on the Black Friday tab.</summary>
public sealed record DealRefineRow(string Title, DealFilterViewModel Filter);

/// <summary>One choice in a deals filter, with a checkbox.</summary>
public sealed partial class DealFilterOptionViewModel(DealFilterViewModel owner, int id, string name) : ObservableObject
{
    public int Id { get; } = id;

    public string Name { get; } = name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    public partial int Count { get; set; }

    public string CountText => owner.HasCounts ? Format.Count(Count) : "";

    public bool IsCategory => owner.Kind == DealFilterKind.Category;

    public bool IsStore => owner.Kind == DealFilterKind.Store;

    public bool IsBrand => owner.Kind == DealFilterKind.Brand;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Remove() => IsSelected = false;

    partial void OnIsSelectedChanged(bool value) => owner.OnOptionToggled(this, value);
}

/// <summary>
/// A filter (category, store or brand) on the Black Friday or Search tab: a typed search over its choices. Several can be
/// ticked, unless it is single-choice, where ticking one replaces the other.
/// </summary>
public sealed partial class DealFilterViewModel(string title, string placeholder, DealFilterKind kind, bool singleChoice = false, bool hasCounts = true) : ObservableObject
{
    /// <summary>How many of the most popular choices the refine box shows directly.</summary>
    private const int TopCount = 5;

    private Dictionary<int, DealFilterOptionViewModel> _known = [];
    private List<DealFilterOptionViewModel> _options = [];
    private bool _silent;

    /// <summary>The ticked choices changed.</summary>
    public event EventHandler? SelectionChanged;

    public string Placeholder { get; } = placeholder;

    [ObservableProperty]
    public partial IReadOnlyList<DealFilterOptionViewModel> Visible { get; set; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    public DealFilterKind Kind { get; } = kind;

    public bool HasCounts { get; } = hasCounts;

    public bool HasOptions => _options.Count > 0;

    /// <summary>What the refine box shows: the most popular choices with deals, and whatever else is ticked.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<DealFilterOptionViewModel> Top { get; set; } = [];

    public IReadOnlyList<DealFilterOptionViewModel> Selected => [.. _known.Values.Where(o => o.IsSelected)];

    public IReadOnlyList<int> SelectedIds => [.. Selected.Select(o => o.Id)];

    public bool HasSelection => _known.Values.Any(o => o.IsSelected);

    /// <summary>"Category", the one ticked choice's name, or "Category · 3".</summary>
    public string Label
    {
        get
        {
            var selected = _known.Values.Where(o => o.IsSelected).ToList();
            return selected.Count switch
            {
                0 => title,
                1 => selected[0].Name,
                _ => $"{title} · {selected.Count}",
            };
        }
    }

    /// <summary>Takes new choices from PriceRunner. Ticked choices stay, also when they have no deals right now.</summary>
    public void SetOptions(IReadOnlyList<DealFacetOption> options)
    {
        var listed = options.Select(o => o.Id).ToHashSet();
        var ticked = _known.Values.Where(o => o.IsSelected && !listed.Contains(o.Id)).ToList();
        foreach (var option in ticked)
        {
            option.Count = 0;
        }

        var known = new Dictionary<int, DealFilterOptionViewModel>();
        foreach (var option in ticked)
        {
            known[option.Id] = option;
        }

        var list = new List<DealFilterOptionViewModel>(ticked);
        foreach (var option in options)
        {
            var row = _known.GetValueOrDefault(option.Id) ?? new DealFilterOptionViewModel(this, option.Id, option.Name);
            row.Count = option.Count;
            known[option.Id] = row;
            list.Add(row);
        }

        _known = known;
        _options = list;
        OnPropertyChanged(nameof(HasOptions));
        ApplySearch();
        UpdateTop();
    }

    [RelayCommand]
    public void Clear()
    {
        ClearSilently();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void ClearSilently()
    {
        _silent = true;
        foreach (var option in _known.Values)
        {
            option.IsSelected = false;
        }

        _silent = false;
        SearchText = "";
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(HasSelection));
        UpdateTop();
    }

    internal void OnOptionToggled(DealFilterOptionViewModel toggled, bool selected)
    {
        if (_silent)
        {
            return;
        }

        if (singleChoice && selected)
        {
            _silent = true;
            foreach (var other in _known.Values.Where(o => o != toggled))
            {
                other.IsSelected = false;
            }

            _silent = false;
        }

        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(HasSelection));
        UpdateTop();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSearchTextChanged(string value) => ApplySearch();

    /// <summary>The choice with exactly this name, if any.</summary>
    internal DealFilterOptionViewModel? Find(string name) =>
        _options.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.CurrentCultureIgnoreCase));

    private void UpdateTop()
    {
        var top = _options.Where(o => o.Count > 0).Take(TopCount).ToList();
        top.AddRange(_options.Where(o => o.IsSelected && !top.Contains(o)));
        Top = top;
    }

    private void ApplySearch()
    {
        var text = SearchText.Trim();
        Visible = text.Length == 0
            ? _options
            : [.. _options.Where(o => o.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase))];
    }
}
