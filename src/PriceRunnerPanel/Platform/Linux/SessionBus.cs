using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Tmds.DBus.Protocol;

namespace PriceRunnerPanel.Platform;

/// <summary>
/// The app's own connection to the D-Bus session bus, shared by the Linux services (Avalonia keeps its own for the
/// tray icon). Null when there is no session bus, e.g. in a bare SSH login: the services then do nothing.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class SessionBus
{
    private static readonly Lazy<Task<DBusConnection?>> Shared = new(ConnectAsync);

    /// <summary>
    /// Running inside a Flatpak sandbox: talk to the desktop through portals. PRICERUNNERPANEL_PORTALS=1 forces the
    /// portal code paths outside a sandbox (testing).
    /// </summary>
    public static bool InFlatpak { get; } =
        File.Exists("/.flatpak-info") || Environment.GetEnvironmentVariable("PRICERUNNERPANEL_PORTALS") == "1";

    public static Task<DBusConnection?> GetAsync() => Shared.Value;

    /// <summary>Everything a D-Bus call can fail with when the bus or the other side misbehaves.</summary>
    public static bool IsFailure(Exception ex) =>
        ex is DBusConnectionException or DBusMessageException or IOException or SocketException or ObjectDisposedException
            or InvalidOperationException or TimeoutException or OperationCanceledException or UnauthorizedAccessException;

    private static async Task<DBusConnection?> ConnectAsync()
    {
        if (DBusAddress.Session is not { Length: > 0 } address)
        {
            AppInfo.Log("No D-Bus session bus: notifications, autostart and single-instance are off.");
            return null;
        }

        var connection = new DBusConnection(address);
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
            return connection;
        }
        catch (Exception ex) when (IsFailure(ex))
        {
            AppInfo.Log($"Couldn't connect to the D-Bus session bus: {ex.Message}");
            connection.Dispose();
            return null;
        }
    }
}

/// <summary>
/// Calls to xdg-desktop-portal that answer through a Request object (Background, Secret): the answer arrives as a
/// Response signal on a path made from this connection's name and a token we choose, so we listen before calling.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class Portal
{
    public const string Service = "org.freedesktop.portal.Desktop";
    public const string ObjectPath = "/org/freedesktop/portal/desktop";

    /// <summary>A handle_token: letters, digits and underscores only.</summary>
    public static string NewToken() => "prp_" + RandomNumberGenerator.GetHexString(16, lowercase: true);

    /// <summary>
    /// Waits for the Response of the request made by <paramref name="call"/> (which must carry
    /// <paramref name="token"/> as its handle_token). Response 0 is success, 1 cancelled by the user, 2 other.
    /// <paramref name="sent"/> runs once the portal has accepted the call.
    /// </summary>
    public static async Task<(uint Response, Dictionary<string, VariantValue> Results)> RequestAsync(
        DBusConnection connection, string token, MessageBuffer call, TimeSpan timeout, Action? sent = null)
    {
        var sender = connection.UniqueName?.TrimStart(':').Replace('.', '_')
            ?? throw new InvalidOperationException("The D-Bus connection has no name.");
        var answer = new TaskCompletionSource<(uint, Dictionary<string, VariantValue>)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rule = new MatchRule
        {
            Type = MessageType.Signal,
            Interface = "org.freedesktop.portal.Request",
            Member = "Response",
            Path = $"{ObjectPath}/request/{sender}/{token}",
        };

        using var subscription = await connection.AddMatchAsync(
            rule,
            static (message, _) =>
            {
                var reader = message.GetBodyReader();
                return (reader.ReadUInt32(), reader.ReadDictionaryOfStringToVariantValue());
            },
            notification =>
            {
                if (notification.HasValue)
                {
                    answer.TrySetResult(notification.Value);
                }
                else
                {
                    answer.TrySetException(notification.Exception ?? new DBusConnectionClosedException("The portal request ended without an answer."));
                }
            },
            emitOnCapturedContext: false,
            flags: ObserverFlags.EmitOnConnectionClosed).ConfigureAwait(false);

        await connection.CallMethodAsync(call).ConfigureAwait(false);
        sent?.Invoke();
        return await answer.Task.WaitAsync(timeout).ConfigureAwait(false);
    }

    /// <summary>Writes one "key → variant" entry of an a{sv} dictionary.</summary>
    public static void Entry(ref MessageWriter writer, string key, string value)
    {
        writer.WriteDictionaryEntryStart();
        writer.WriteString(key);
        writer.WriteVariantString(value);
    }

    public static void Entry(ref MessageWriter writer, string key, bool value)
    {
        writer.WriteDictionaryEntryStart();
        writer.WriteString(key);
        writer.WriteVariantBool(value);
    }

    public static void Entry(ref MessageWriter writer, string key, VariantValue value)
    {
        writer.WriteDictionaryEntryStart();
        writer.WriteString(key);
        writer.WriteVariant(value);
    }
}
