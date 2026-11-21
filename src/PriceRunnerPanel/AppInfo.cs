using System.Globalization;
using System.Reflection;

namespace PriceRunnerPanel;

/// <summary>What this build is, where its data lives, and a small append-only log in the data folder.</summary>
public static class AppInfo
{
    /// <summary>The public GitHub repository whose releases are this app's updates.</summary>
    public const string Repository = "PantaKoda/PriceRunnerPanel";

    /// <summary>The Flatpak app ID, also the D-Bus name and the notification/autostart identity on Linux.</summary>
    public const string AppId = "io.github.PantaKoda.PriceRunnerPanel";

    /// <summary>Passed by the system's autostart entry: start hidden in the tray.</summary>
    public const string AtLoginArgument = "--startup";

    public static string ExecutableName { get; } = OperatingSystem.IsWindows() ? "PriceRunnerPanel.exe" : "PriceRunnerPanel";

    /// <summary>The version from Directory.Build.props, without build metadata ("0.1.0").</summary>
    public static string Version { get; } =
        (typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    private static readonly string? CustomDataDirectory =
        Environment.GetEnvironmentVariable("PRICERUNNERPANEL_DATA") is { Length: > 0 } custom ? Path.GetFullPath(custom) : null;

    /// <summary>
    /// The watchlist, settings and logs: %LOCALAPPDATA%\PriceRunnerPanel on Windows, $XDG_DATA_HOME/PriceRunnerPanel
    /// (~/.local/share, or ~/.var/app/&lt;app id&gt;/data inside the Flatpak) on Linux, or PRICERUNNERPANEL_DATA when set (testing).
    /// </summary>
    public static string DataDirectory { get; } =
        CustomDataDirectory ?? Path.Combine(
            // DoNotVerify: on Linux the XDG folder may not exist yet, and .NET would return "" for it (a relative path).
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            "PriceRunnerPanel");

    /// <summary>
    /// Downloaded product images, which can be fetched again: inside the data folder on Windows (and when testing),
    /// $XDG_CACHE_HOME/PriceRunnerPanel (~/.cache, or ~/.var/app/&lt;app id&gt;/cache inside the Flatpak) on Linux.
    /// </summary>
    public static string ImageCacheDirectory { get; } =
        CustomDataDirectory is null && !OperatingSystem.IsWindows()
            ? Path.Combine(XdgCacheHome(), "PriceRunnerPanel", "images")
            : Path.Combine(DataDirectory, "images");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    private static string XdgCacheHome() =>
        Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } cache && Path.IsPathRooted(cache)
            ? cache
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify), ".cache");

    /// <summary>Appends a line to logs\&lt;file&gt;. Never throws: logging must not break the app or an update.</summary>
    public static void Log(string message, string file = "app.log")
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(Path.Combine(LogDirectory, file),
                string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {message}{Environment.NewLine}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
