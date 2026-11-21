using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security;
using Tmds.DBus.Protocol;

namespace PriceRunnerPanel.Platform;

/// <summary>
/// Desktop notifications over D-Bus. Inside a Flatpak through the Notification portal
/// (org.freedesktop.portal.Notification, no extra permission); otherwise straight to the notification server
/// (org.freedesktop.Notifications). Both respect the desktop's do-not-disturb setting. Clicking one opens its link.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxNotifier : INotifier
{
    private const string ServerName = "org.freedesktop.Notifications";
    private const string ServerPath = "/org/freedesktop/Notifications";
    private const string PortalInterface = "org.freedesktop.portal.Notification";
    private const string OpenAction = "open";

    private readonly bool _portal = SessionBus.InFlatpak;
    private readonly ConcurrentDictionary<string, Action> _onClick = new();

    // The notification server numbers its notifications; the portal uses our tags as ids.
    private readonly ConcurrentDictionary<string, uint> _serverIds = new();
    private readonly ConcurrentDictionary<uint, string> _serverTags = new();
    private readonly SemaphoreSlim _setup = new(1, 1);
    private bool _ready;
    private bool _bodyMarkup;

    public bool Show(string title, string body, string tag, Action? onActivated)
    {
        tag = TagFor(tag);
        if (onActivated is not null)
        {
            _onClick[tag] = onActivated;
        }
        else
        {
            _onClick.TryRemove(tag, out _);
        }

        _ = ShowAsync(title, body, tag, onActivated is not null);
        return true;
    }

    public void Remove(string tag) => _ = RemoveAsync(TagFor(tag));

    private async Task ShowAsync(string title, string body, string tag, bool clickable)
    {
        try
        {
            if (await ConnectAsync().ConfigureAwait(false) is not { } bus)
            {
                return;
            }

            if (_portal)
            {
                await bus.CallMethodAsync(AddNotification(bus, tag, title, body, clickable)).ConfigureAwait(false);
                return;
            }

            var replaces = _serverIds.TryGetValue(tag, out var previous) ? previous : 0u;
            var id = await bus.CallMethodAsync(
                Notify(bus, replaces, title, _bodyMarkup ? SecurityElement.Escape(body) : body, clickable),
                static (message, _) => message.GetBodyReader().ReadUInt32(),
                null).ConfigureAwait(false);
            _serverIds[tag] = id;
            _serverTags[id] = tag;
        }
        catch (Exception ex) when (SessionBus.IsFailure(ex))
        {
            AppInfo.Log($"Couldn't show a notification: {ex.Message}");
        }
    }

    private async Task RemoveAsync(string tag)
    {
        _onClick.TryRemove(tag, out _);
        try
        {
            if (await ConnectAsync().ConfigureAwait(false) is not { } bus)
            {
                return;
            }

            if (_portal)
            {
                await bus.CallMethodAsync(RemoveNotification(bus, tag)).ConfigureAwait(false);
            }
            else if (_serverIds.TryRemove(tag, out var id))
            {
                _serverTags.TryRemove(id, out _);
                await bus.CallMethodAsync(CloseNotification(bus, id)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (SessionBus.IsFailure(ex))
        {
            // Already gone, or the server forgot it: nothing to clear.
        }
    }

    /// <summary>Connects once and listens for clicks; null without a session bus.</summary>
    private async Task<DBusConnection?> ConnectAsync()
    {
        var bus = await SessionBus.GetAsync().ConfigureAwait(false);
        if (bus is null || _ready)
        {
            return bus;
        }

        await _setup.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_ready)
            {
                return bus;
            }

            if (_portal)
            {
                await bus.AddMatchAsync(
                    new MatchRule { Type = MessageType.Signal, Interface = PortalInterface, Member = "ActionInvoked", Path = Portal.ObjectPath },
                    static (message, _) =>
                    {
                        var reader = message.GetBodyReader();
                        return (Id: reader.ReadString(), Action: reader.ReadString());
                    },
                    notification =>
                    {
                        if (notification.HasValue && notification.Value.Action == OpenAction)
                        {
                            Clicked(notification.Value.Id);
                        }
                    },
                    emitOnCapturedContext: false).ConfigureAwait(false);
            }
            else
            {
                await bus.AddMatchAsync(
                    new MatchRule { Type = MessageType.Signal, Interface = ServerName, Member = "ActionInvoked", Path = ServerPath },
                    static (message, _) =>
                    {
                        var reader = message.GetBodyReader();
                        return (Id: reader.ReadUInt32(), Action: reader.ReadString());
                    },
                    notification =>
                    {
                        if (notification.HasValue && notification.Value.Action == "default" && _serverTags.TryGetValue(notification.Value.Id, out var tag))
                        {
                            Clicked(tag);
                        }
                    },
                    emitOnCapturedContext: false).ConfigureAwait(false);

                // Servers that understand markup in the body (KDE, GNOME) would read "<" and "&" in a product name.
                var capabilities = await bus.CallMethodAsync(
                    GetCapabilities(bus), static (message, _) => message.GetBodyReader().ReadArrayOfString(), null).ConfigureAwait(false);
                _bodyMarkup = capabilities.Contains("body-markup");
            }

            _ready = true;
            return bus;
        }
        finally
        {
            _setup.Release();
        }
    }

    private void Clicked(string tag)
    {
        if (_onClick.TryGetValue(tag, out var action))
        {
            action();
        }
    }

    // Same limit as the Windows toasts, so both identify a product's notification the same way.
    private static string TagFor(string tag) => tag.Length <= 64 ? tag : tag[..64];

    private static MessageBuffer AddNotification(DBusConnection bus, string id, string title, string body, bool clickable)
    {
        var writer = bus.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(Portal.Service, Portal.ObjectPath, PortalInterface, "AddNotification", "sa{sv}");
            writer.WriteString(id);
            var options = writer.WriteDictionaryStart();
            Portal.Entry(ref writer, "title", title);
            Portal.Entry(ref writer, "body", body); // plain text: the portal only reads markup from "markup-body"
            Portal.Entry(ref writer, "priority", "normal");
            if (clickable)
            {
                Portal.Entry(ref writer, "default-action", OpenAction);
            }

            writer.WriteDictionaryEnd(options);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static MessageBuffer RemoveNotification(DBusConnection bus, string id)
    {
        using var writer = bus.GetMessageWriter();
        writer.WriteMethodCallHeader(Portal.Service, Portal.ObjectPath, PortalInterface, "RemoveNotification", "s");
        writer.WriteString(id);
        return writer.CreateMessage();
    }

    private static MessageBuffer Notify(DBusConnection bus, uint replaces, string title, string body, bool clickable)
    {
        var writer = bus.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(ServerName, ServerPath, ServerName, "Notify", "susssasa{sv}i");
            writer.WriteString("PriceRunner Panel");
            writer.WriteUInt32(replaces);
            writer.WriteString(AppInfo.AppId); // icon name; shows the app's icon when it is installed
            writer.WriteString(title);
            writer.WriteString(body);
            writer.WriteArray(clickable ? ["default", "Open"] : Array.Empty<string>());
            var hints = writer.WriteDictionaryStart();
            Portal.Entry(ref writer, "desktop-entry", AppInfo.AppId);
            Portal.Entry(ref writer, "urgency", VariantValue.Byte(1));
            writer.WriteDictionaryEnd(hints);
            writer.WriteInt32(-1); // the server's default display time
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static MessageBuffer CloseNotification(DBusConnection bus, uint id)
    {
        using var writer = bus.GetMessageWriter();
        writer.WriteMethodCallHeader(ServerName, ServerPath, ServerName, "CloseNotification", "u");
        writer.WriteUInt32(id);
        return writer.CreateMessage();
    }

    private static MessageBuffer GetCapabilities(DBusConnection bus)
    {
        using var writer = bus.GetMessageWriter();
        writer.WriteMethodCallHeader(ServerName, ServerPath, ServerName, "GetCapabilities");
        return writer.CreateMessage();
    }
}
