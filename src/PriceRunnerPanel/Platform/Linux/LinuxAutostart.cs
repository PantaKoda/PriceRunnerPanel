using System.Runtime.Versioning;
using System.Text;
using Tmds.DBus.Protocol;

namespace PriceRunnerPanel.Platform;

/// <summary>
/// Starts the panel, hidden in the tray, when the user logs in. Inside a Flatpak the Background portal
/// (org.freedesktop.portal.Background, RequestBackground with autostart) writes the autostart entry; the desktop may
/// ask the user once. Outside a Flatpak the entry is an XDG autostart file (~/.config/autostart).
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxAutostart(string executablePath) : IAutostartService
{
    /// <summary>The user may be asked, so give them time to answer.</summary>
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromMinutes(2);

    public string Label => "Start at login";

    public string EnabledMessage => "Starts at login (hidden in the tray)";

    public string DisabledMessage => "No longer starts at login";

    public string RefusedMessage => "The desktop didn't allow starting at login";

    /// <summary>~/.config/autostart/&lt;app id&gt;.desktop (outside a Flatpak).</summary>
    public static string AutostartFile =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            "autostart",
            AppInfo.AppId + ".desktop");

    public Task<bool> SetAsync(bool enabled) => SessionBus.InFlatpak ? RequestAsync(enabled) : Task.FromResult(WriteFile(enabled));

    public async Task SyncAsync(bool enabled)
    {
        if (!SessionBus.InFlatpak)
        {
            WriteFile(enabled); // also repairs an entry pointing at an old location
        }
        else if (enabled)
        {
            // The portal remembers the user's answer, so this only asks on the very first start.
            await RequestAsync(true).ConfigureAwait(false);
        }
    }

    private async Task<bool> RequestAsync(bool enabled)
    {
        try
        {
            if (await SessionBus.GetAsync().ConfigureAwait(false) is not { } bus)
            {
                return false;
            }

            var token = Portal.NewToken();
            var (response, results) = await Portal.RequestAsync(bus, token, RequestBackground(bus, token, enabled), AnswerTimeout).ConfigureAwait(false);
            var autostart = results.TryGetValue("autostart", out var value) && value.GetBool();
            if (response != 0 || autostart != enabled)
            {
                AppInfo.Log($"The Background portal answered {response} (autostart {autostart}) to autostart {enabled}.");
                return false;
            }

            return true;
        }
        catch (Exception ex) when (SessionBus.IsFailure(ex))
        {
            AppInfo.Log($"Couldn't change autostart through the Background portal: {ex.Message}");
            return false;
        }
    }

    private static MessageBuffer RequestBackground(DBusConnection bus, string token, bool autostart)
    {
        var writer = bus.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(Portal.Service, Portal.ObjectPath, "org.freedesktop.portal.Background", "RequestBackground", "sa{sv}");
            writer.WriteString(""); // no parent window: the panel may be hidden in the tray
            var options = writer.WriteDictionaryStart();
            Portal.Entry(ref writer, "handle_token", token);
            Portal.Entry(ref writer, "reason", "Start PriceRunner Panel when you log in, hidden in the tray, so it keeps tracking prices.");
            Portal.Entry(ref writer, "autostart", autostart);
            // Run inside the sandbox as "flatpak run --command=PriceRunnerPanel <app id> --startup".
            Portal.Entry(ref writer, "commandline", VariantValue.Array(new[] { AppInfo.ExecutableName, AppInfo.AtLoginArgument }));
            Portal.Entry(ref writer, "dbus-activatable", false);
            writer.WriteDictionaryEnd(options);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private bool WriteFile(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                File.Delete(AutostartFile);
                return true;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(AutostartFile)!);
            var entry = new StringBuilder()
                .Append("[Desktop Entry]\n")
                .Append("Type=Application\n")
                .Append("Name=PriceRunner Panel\n")
                .Append("Comment=Track prices on PriceRunner\n")
                .Append($"Exec={ExecArgument(executablePath)} {AppInfo.AtLoginArgument}\n")
                .Append("Terminal=false\n")
                .Append("X-GNOME-Autostart-enabled=true\n")
                .ToString();
            File.WriteAllText(AutostartFile, entry);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppInfo.Log($"Couldn't change {AutostartFile}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// A path as one Exec argument: quoted, with the characters the Desktop Entry spec reserves escaped, then escaped
    /// once more because Exec is itself a string value.
    /// </summary>
    internal static string ExecArgument(string path)
    {
        var quoted = new StringBuilder("\"");
        foreach (var c in path)
        {
            if (c is '"' or '`' or '$' or '\\')
            {
                quoted.Append('\\');
            }

            quoted.Append(c);
        }

        return quoted.Append('"').ToString().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "%%", StringComparison.Ordinal);
    }
}
