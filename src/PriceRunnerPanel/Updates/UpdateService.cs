using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace PriceRunnerPanel.Updates;

public enum UpdateStage
{
    Idle,
    Checking,
    UpToDate,
    Available,
    CheckFailed,
    Downloading,
    Verifying,
    Installing,
    /// <summary>The new version took over; this app is quitting.</summary>
    Restarting,
    /// <summary>An install attempt failed and nothing was changed; <see cref="UpdateService.Message"/> says why.</summary>
    InstallFailed,
}

/// <summary>
/// Finds and installs updates from the app's public GitHub releases.
/// <list type="bullet">
/// <item>Checks shortly after startup and then every <see cref="CheckInterval"/>; never downloads on its own.</item>
/// <item>Installing is always the user's choice. The zip is downloaded only from this repository's release URLs,
/// verified against the release's SHA-256 file, unpacked into the data folder, checked to contain this app at the
/// release's version, and handed to the new copy, which replaces the install folder after this app quits
/// (see <see cref="UpdateApplier"/>).</item>
/// </list>
/// The checksum proves the download is the file the release published; it is not a code signature.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(20);
    private const long MaxDownloadBytes = 300L * 1024 * 1024;

    private readonly ReleaseClient _source;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _state = new();
    private CancellationTokenSource? _install;

    public UpdateService(ReleaseClient source, InstallInfo install)
    {
        _source = source;
        Install = install;
    }

    public InstallInfo Install { get; }

    public AppVersion Current => Install.Version;

    public Uri ReleasesPage => _source.ReleasesPage;

    public UpdateStage Stage { get; private set; }

    /// <summary>Why the last check or install failed, in words for the user.</summary>
    public string? Message { get; private set; }

    /// <summary>Download progress, 0 to 1.</summary>
    public double Progress { get; private set; }

    /// <summary>Releases newer than this copy, newest first.</summary>
    public IReadOnlyList<ReleaseInfo> Available { get; private set; } = [];

    public ReleaseInfo? Latest => Available.Count > 0 ? Available[0] : null;

    public bool IsBusy => Stage is UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Verifying or UpdateStage.Installing or UpdateStage.Restarting;

    public bool CanCancelInstall => Stage is UpdateStage.Downloading or UpdateStage.Verifying;

    private string UpdatesDirectory => Path.Combine(AppInfo.DataDirectory, "updates");

    /// <summary>Raised when anything above changes. May be raised on any thread.</summary>
    public event EventHandler? Changed;

    /// <summary>The new version is ready to take over: the app must quit now so it can replace the folder.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Why <see cref="InstallAsync"/> can't run, or null when it can.</summary>
    public string? CannotInstallReason
    {
        get
        {
            if (!Install.IsPortableRelease)
            {
                return "This copy wasn't installed from a release zip (for example, it was built from source), so it can't replace itself. Download the update from GitHub instead.";
            }

            if (IsSameOrInside(AppInfo.DataDirectory, Install.InstallDirectory))
            {
                return $"The app keeps its data inside its own folder ({AppInfo.DataDirectory}), so it can't replace that folder. Move the app to a folder of its own, for example %LOCALAPPDATA%\\Programs\\PriceRunnerPanel.";
            }

            if (InstallInfo.WhyNotReplaceable(Install.InstallDirectory) is { } notReplaceable)
            {
                return $"The app can't update itself here: {notReplaceable}. Extract the release zip to a folder of its own, for example %LOCALAPPDATA%\\Programs (giving %LOCALAPPDATA%\\Programs\\PriceRunnerPanel), or download the update from GitHub.";
            }

            if (Latest is not { } latest)
            {
                return "There is no update to install.";
            }

            if (!latest.CanInstallOnWindows)
            {
                return $"Release {latest.Version} has no Windows download.";
            }

            return CanWrite(Install.InstallDirectory) && CanWrite(Path.GetDirectoryName(Install.InstallDirectory))
                ? null
                : $"The app can't change its folder ({Install.InstallDirectory}). Download the update from GitHub and replace the folder yourself.";
        }
    }

    /// <summary>Starts the periodic check and tidies up after an update.</summary>
    public void Start() => _ = RunAsync(_stop.Token);

    public async Task CheckNowAsync(CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            Set(UpdateStage.Checking, null);
            var result = await _source.GetReleasesAsync(cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                AppInfo.Log($"Checking for updates failed: {result.Error}");
                Set(UpdateStage.CheckFailed, result.Error);
                return;
            }

            var newer = UpdatePolicy.Newer(result.Releases!, Current);
            lock (_state)
            {
                Available = newer;
            }

            Set(newer.Count > 0 ? UpdateStage.Available : UpdateStage.UpToDate, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Downloads, verifies and stages the latest release, then hands over to it and asks the app to quit.
    /// Any failure leaves the installed app untouched and is reported in <see cref="Message"/>.
    /// </summary>
    public async Task InstallAsync()
    {
        if (CannotInstallReason is { } reason)
        {
            Set(UpdateStage.InstallFailed, reason);
            return;
        }

        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        using var install = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        lock (_state)
        {
            _install = install;
        }

        var token = install.Token;
        var release = Latest!;
        var zipName = release.ZipName;
        var root = UpdatesDirectory;
        try
        {
            Progress = 0;
            Set(UpdateStage.Downloading, null);
            DeleteQuietly(root);
            Directory.CreateDirectory(root);

            using var checksumBuffer = new MemoryStream();
            await _source.DownloadAsync(release.WindowsChecksum!.DownloadUrl, checksumBuffer, 4096, null, token).ConfigureAwait(false);
            var expected = UpdatePolicy.ParseChecksum(Encoding.UTF8.GetString(checksumBuffer.ToArray()), zipName)
                ?? throw new InvalidDataException("the release's checksum file is not in the expected format");

            var zipPath = Path.Combine(root, zipName);
            var file = File.Create(zipPath);
            await using (file.ConfigureAwait(false))
            {
                await _source.DownloadAsync(release.WindowsZip!.DownloadUrl, file, MaxDownloadBytes, new ThrottledProgress(this), token).ConfigureAwait(false);
            }

            Set(UpdateStage.Verifying, null);
            string actual;
            var read = File.OpenRead(zipPath);
            await using (read.ConfigureAwait(false))
            {
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(read, token).ConfigureAwait(false));
            }

            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expected)))
            {
                DeleteQuietly(root);
                AppInfo.Log($"Update {release.Version}: checksum mismatch");
                Set(UpdateStage.InstallFailed, "The download doesn't match the checksum published with the release, so it wasn't installed. Nothing was changed.");
                return;
            }

            Set(UpdateStage.Installing, null);
            var unpacked = Path.Combine(root, release.Version.ToString());
            ZipFile.ExtractToDirectory(zipPath, unpacked); // rejects entries that would land outside the folder
            var staged = Path.Combine(unpacked, "PriceRunnerPanel");
            var executable = Path.Combine(staged, AppInfo.ExecutableName);
            if (!File.Exists(executable) || InstallInfo.ReadManifestVersion(staged) != release.Version)
            {
                throw new InvalidDataException($"the download doesn't contain PriceRunner Panel {release.Version}");
            }

            string[] arguments =
            [
                UpdateArguments.Apply,
                UpdateArguments.Target, Install.InstallDirectory,
                UpdateArguments.WaitPid, Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                UpdateArguments.From, Current.ToString(),
            ];
            if (!ProcessLauncher.Start(executable, arguments))
            {
                throw new IOException("the new version couldn't be started");
            }

            AppInfo.Log($"Update {release.Version} staged and handed over; quitting so it can replace {Install.InstallDirectory}");
            Set(UpdateStage.Restarting, null);
            ExitRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            DeleteQuietly(root);
            Set(UpdateStage.InstallFailed, "The update was cancelled. Nothing was changed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException
            or HttpRequestException or TimeoutException or TaskCanceledException)
        {
            AppInfo.Log($"Installing update {release.Version} failed: {ex}");
            DeleteQuietly(root);
            Set(UpdateStage.InstallFailed, $"Couldn't install the update ({ex.Message}). Nothing was changed.");
        }
        finally
        {
            lock (_state)
            {
                _install = null;
            }

            _gate.Release();
        }
    }

    /// <summary>Stops a download in progress. Once the new version has taken over, it's too late.</summary>
    public void CancelInstall()
    {
        lock (_state)
        {
            if (CanCancelInstall)
            {
                _install?.Cancel();
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
        _source.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(FirstCheckDelay, cancellationToken).ConfigureAwait(false);
            if (Install.UpdatedFrom is not null)
            {
                DeleteQuietly(UpdatesDirectory); // the hand-over copy has finished by now
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                await CheckNowAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(CheckInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Quitting.
        }
    }

    private void Set(UpdateStage stage, string? message)
    {
        lock (_state)
        {
            Stage = stage;
            Message = message;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsSameOrInside(string path, string folder)
    {
        var inner = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var outer = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return string.Equals(inner, outer, StringComparison.OrdinalIgnoreCase)
            || inner.StartsWith(outer + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanWrite(string? directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        try
        {
            var probe = Path.Combine(directory, $".prp-write-test-{Guid.NewGuid():N}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void DeleteQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Reports download progress in whole percent only, so the UI isn't flooded with changes.</summary>
    private sealed class ThrottledProgress(UpdateService owner) : IProgress<double>
    {
        private int _percent = -1;

        public void Report(double value)
        {
            var percent = (int)Math.Clamp(value * 100, 0, 100);
            if (Interlocked.Exchange(ref _percent, percent) != percent)
            {
                owner.Progress = percent / 100.0;
                owner.Changed?.Invoke(owner, EventArgs.Empty);
            }
        }
    }
}
