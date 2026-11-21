namespace PriceRunnerPanel.Platform;

/// <summary>Desktop notifications. Plain text only: product names are never read as markup.</summary>
public interface INotifier
{
    /// <summary>Shows a notification; one with the same tag replaces the earlier one. Clicking it runs <paramref name="onActivated"/>.</summary>
    /// <returns>False when the desktop has no notifications for this app.</returns>
    bool Show(string title, string body, string tag, Action? onActivated);

    /// <summary>Clears this tag's notification (e.g. for a product that was unpinned).</summary>
    void Remove(string tag);
}

/// <summary>The app's own menu entry. Only Windows needs one made; a Flatpak brings its .desktop file.</summary>
public interface IShortcutService
{
    bool Exists { get; }

    /// <summary>True when the entry exists and opens this executable.</summary>
    bool IsCurrent { get; }

    /// <summary>Creates the entry, or points an existing one at this executable. False if the system refused.</summary>
    bool Save();
}

/// <summary>Starting the panel, hidden in the tray, when the user signs in.</summary>
public interface IAutostartService
{
    /// <summary>The tray menu item ("Start with Windows").</summary>
    string Label { get; }

    string EnabledMessage { get; }

    string DisabledMessage { get; }

    string RefusedMessage { get; }

    /// <summary>Turns it on or off. False if the system refused (or the user said no).</summary>
    Task<bool> SetAsync(bool enabled);

    /// <summary>At every start: keeps the system's entry in line with the setting (e.g. after the app moved).</summary>
    Task SyncAsync(bool enabled);
}

/// <summary>Keeps passwords and tokens encrypted for this user, so a copy of the state file is no use elsewhere.</summary>
public interface ISecretStore
{
    Task<string> ProtectAsync(string secret);

    /// <exception cref="System.Security.Cryptography.CryptographicException">The secret can't be read here.</exception>
    Task<string> UnprotectAsync(string protectedSecret);

    /// <summary>Shown when a saved secret can't be read.</summary>
    string UnreadableMessage { get; }
}

/// <summary>One running panel per user: a second launch shows the running one and exits.</summary>
public interface ISingleInstance : IDisposable
{
    /// <summary>True when this is the first instance. It then raises <see cref="ShowRequested"/> when another launch asks.</summary>
    bool TryClaim();

    /// <summary>Asks the running instance to show its window.</summary>
    void RequestShow();

    /// <summary>May be raised on any thread.</summary>
    event EventHandler? ShowRequested;
}
