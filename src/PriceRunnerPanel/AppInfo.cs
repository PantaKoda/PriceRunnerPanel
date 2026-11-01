using System.Globalization;
using System.Reflection;

namespace PriceRunnerPanel;

/// <summary>What this build is, where its data lives, and a small append-only log in the data folder.</summary>
public static class AppInfo
{
    /// <summary>The public GitHub repository whose releases are this app's updates.</summary>
    public const string Repository = "PantaKoda/PriceRunnerPanel";

    public const string ExecutableName = "PriceRunnerPanel.exe";

    /// <summary>The version from Directory.Build.props, without build metadata ("0.1.0").</summary>
    public static string Version { get; } =
        (typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    /// <summary>%LOCALAPPDATA%\PriceRunnerPanel, or PRICERUNNERPANEL_DATA when set (testing).</summary>
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("PRICERUNNERPANEL_DATA") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PriceRunnerPanel");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

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
