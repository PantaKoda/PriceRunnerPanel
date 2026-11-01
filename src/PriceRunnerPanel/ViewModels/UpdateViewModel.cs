using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PriceRunnerPanel.Updates;

namespace PriceRunnerPanel.ViewModels;

/// <summary>One release's notes in the update window.</summary>
public sealed record ReleaseNotesItem(string Title, string DateText, string Notes);

/// <summary>
/// The update window: every release newer than this copy with its notes (plain text: they are written on GitHub and
/// never rendered as markup), Install update, and the release page on GitHub.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject, IDisposable
{
    private readonly UpdateService _updates;
    private readonly Action<string> _openUrl;
    private IReadOnlyList<ReleaseInfo>? _shownReleases;
    private UpdateStage? _shownStage;

    public UpdateViewModel(UpdateService updates, Action<string> openUrl)
    {
        _updates = updates;
        _openUrl = openUrl;
        _updates.Changed += OnChanged;
        Refresh();
    }

    [ObservableProperty]
    public partial string Heading { get; private set; } = "";

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<ReleaseNotesItem> Releases { get; private set; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial bool CanInstall { get; private set; }

    /// <summary>Why Install update is unavailable, shown under the button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInstallHint))]
    public partial string? InstallHint { get; private set; }

    public bool HasInstallHint => InstallHint is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string? Status { get; private set; }

    public bool HasStatus => Status is not null;

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool CanCancel { get; private set; }

    /// <summary>The previous attempt to install an update couldn't be applied; shown until the next install.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviousFailure))]
    public partial string? PreviousFailure { get; private set; }

    public bool HasPreviousFailure => PreviousFailure is not null;

    [ObservableProperty]
    public partial bool ShowProgress { get; private set; }

    [ObservableProperty]
    public partial double ProgressPercent { get; private set; }

    public void Dispose() => _updates.Changed -= OnChanged;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private Task InstallAsync() => _updates.InstallAsync();

    [RelayCommand]
    private void ViewOnGitHub() => _openUrl((_updates.Latest?.HtmlUrl ?? _updates.ReleasesPage).AbsoluteUri);

    [RelayCommand]
    private Task CheckAgainAsync() => _updates.CheckNowAsync();

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _updates.CancelInstall();

    private void OnChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Refresh);

    private void Refresh()
    {
        var latest = _updates.Latest;
        var stage = _updates.Stage;

        // Download progress raises many small changes: the notes are rebuilt only when the release list changes, and
        // the install checks (which touch the disk) run only when the stage changes.
        if (!ReferenceEquals(_shownReleases, _updates.Available))
        {
            _shownReleases = _updates.Available;
            Heading = latest is null ? "PriceRunner Panel is up to date" : $"PriceRunner Panel {latest.Version} is available";
            Summary = latest is null
                ? $"You have version {_updates.Current}. There is no newer release."
                : _updates.Available.Count == 1
                    ? $"You have version {_updates.Current}. Here is what changed:"
                    : $"You have version {_updates.Current}. {_updates.Available.Count} releases came out since then; here is what changed in each:";
            Releases = [.. _updates.Available.Select(r => new ReleaseNotesItem(
                r.Title,
                r.PublishedAt is { } at ? at.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture) : "",
                ReleaseNotesText.ToPlainText(r.Notes)))];
            _shownStage = null;
        }

        if (_shownStage != stage)
        {
            _shownStage = stage;
            IsBusy = _updates.IsBusy;
            CanCancel = _updates.CanCancelInstall;
            var reason = IsBusy ? null : _updates.CannotInstallReason;
            CanInstall = latest is not null && reason is null && !IsBusy;
            InstallHint = latest is not null && reason is not null ? reason : null;
            PreviousFailure = stage is UpdateStage.Downloading or UpdateStage.Verifying or UpdateStage.Installing or UpdateStage.Restarting
                ? null
                : _updates.Install.UpdateFailed;
        }

        ShowProgress = stage == UpdateStage.Downloading;
        ProgressPercent = Math.Round(_updates.Progress * 100);
        Status = stage switch
        {
            UpdateStage.Checking => "Checking for updates…",
            UpdateStage.Downloading => $"Downloading {latest?.Version}… {ProgressPercent:0}%",
            UpdateStage.Verifying => "Checking the download against its published checksum…",
            UpdateStage.Installing => "Preparing the new version…",
            UpdateStage.Restarting => "Restarting into the new version…",
            UpdateStage.CheckFailed or UpdateStage.InstallFailed => _updates.Message,
            _ => null,
        };
    }
}

/// <summary>
/// Turns release notes (GitHub Markdown, untrusted) into readable plain text: headings lose their '#', list markers
/// become bullets, emphasis and link syntax are removed. Nothing is ever interpreted as markup.
/// </summary>
public static partial class ReleaseNotesText
{
    public static string ToPlainText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return "No notes were published for this release.";
        }

        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line =>
        {
            var text = Heading().Replace(line, "");
            text = Bullet().Replace(text, "$1• ");
            text = Link().Replace(text, "$1");
            return text.Replace("**", "", StringComparison.Ordinal).Replace("__", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal).TrimEnd();
        });
        return string.Join('\n', lines).Trim();
    }

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s*")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(\s*)[-*+]\s+")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Link();
}
