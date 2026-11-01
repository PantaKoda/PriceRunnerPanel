using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PriceRunnerPanel.Models;
using PriceRunnerPanel.Services;

namespace PriceRunnerPanel.ViewModels;

/// <summary>One choice in a deals filter, with a checkbox.</summary>
public sealed partial class DealFilterOptionViewModel(DealFilterViewModel owner, int id, string name) : ObservableObject
{
    public int Id { get; } = id;

    public string Name { get; } = name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    public partial int Count { get; set; }

    public string CountText => Format.Count(Count);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value) => owner.OnOptionToggled();
}

/// <summary>A deals filter (category or store): a typed search over hundreds of choices, several can be ticked.</summary>
public sealed partial class DealFilterViewModel(string title, string placeholder) : ObservableObject
{
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

    public IReadOnlyList<int> SelectedIds => [.. _known.Values.Where(o => o.IsSelected).Select(o => o.Id)];

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
        ApplySearch();
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
    }

    internal void OnOptionToggled()
    {
        if (_silent)
        {
            return;
        }

        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(HasSelection));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSearchTextChanged(string value) => ApplySearch();

    private void ApplySearch()
    {
        var text = SearchText.Trim();
        Visible = text.Length == 0
            ? _options
            : [.. _options.Where(o => o.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase))];
    }
}
