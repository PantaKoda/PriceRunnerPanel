namespace PriceRunnerPanel.Platform;

/// <summary>
/// Picks this system's implementation of each platform service. The Windows build only contains the Windows ones; the
/// cross-platform build contains the Linux ones and falls back to doing nothing on other systems.
/// </summary>
public static class PlatformServices
{
#if WINDOWS
    public static ISecretStore Secrets { get; } = new WindowsSecretStore();

    public static INotifier CreateNotifier() => new WindowsToastNotifier();

    public static IShortcutService CreateShortcut(string executablePath) => new WindowsStartMenuShortcut(executablePath);

    public static IAutostartService CreateAutostart(string executablePath) => new WindowsAutostart(executablePath);

    public static ISingleInstance CreateSingleInstance() => new WindowsSingleInstance();
#else
    public static ISecretStore Secrets { get; } = OperatingSystem.IsLinux() ? new LinuxSecretStore() : new UnsupportedSecretStore();

    public static INotifier CreateNotifier() => OperatingSystem.IsLinux() ? new LinuxNotifier() : new NoNotifier();

    /// <summary>The Flatpak's .desktop file is the menu entry, so there is nothing to create.</summary>
    public static IShortcutService CreateShortcut(string executablePath) => new NoShortcut();

    public static IAutostartService CreateAutostart(string executablePath) =>
        OperatingSystem.IsLinux() ? new LinuxAutostart(executablePath) : new NoAutostart();

    public static ISingleInstance CreateSingleInstance() => OperatingSystem.IsLinux() ? new LinuxSingleInstance() : new NoSingleInstance();
#endif
}

internal sealed class NoNotifier : INotifier
{
    public bool Show(string title, string body, string tag, Action? onActivated) => false;

    public void Remove(string tag)
    {
    }
}

internal sealed class NoShortcut : IShortcutService
{
    public bool Exists => true;

    public bool IsCurrent => true;

    public bool Save() => true;
}

internal sealed class NoAutostart : IAutostartService
{
    public string Label => "Start at login";

    public string EnabledMessage => "";

    public string DisabledMessage => "";

    public string RefusedMessage => "Starting at login isn't supported on this system";

    public Task<bool> SetAsync(bool enabled) => Task.FromResult(false);

    public Task SyncAsync(bool enabled) => Task.CompletedTask;
}

internal sealed class UnsupportedSecretStore : ISecretStore
{
    public string UnreadableMessage => "saved secrets aren't supported on this system";

    public Task<string> ProtectAsync(string secret) =>
        Task.FromException<string>(new System.Security.Cryptography.CryptographicException(UnreadableMessage));

    public Task<string> UnprotectAsync(string protectedSecret) => ProtectAsync(protectedSecret);
}

internal sealed class NoSingleInstance : ISingleInstance
{
    public event EventHandler? ShowRequested
    {
        add { }
        remove { }
    }

    public bool TryClaim() => true;

    public void RequestShow()
    {
    }

    public void Dispose()
    {
    }
}
