using System.Runtime.Versioning;
using Tmds.DBus.Protocol;

namespace PriceRunnerPanel.Platform;

/// <summary>
/// One instance per user session: the running panel owns its app ID as a D-Bus name (a Flatpak may always own its
/// own ID), and a second launch calls its Show method. This works across Flatpak sandboxes, where named mutexes and
/// events don't reach. Without a session bus every launch simply runs.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxSingleInstance : ISingleInstance, IPathMethodHandler
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    private DBusConnection? _bus;

    public event EventHandler? ShowRequested;

    /// <summary>"/io/github/PantaKoda/PriceRunnerPanel"</summary>
    public string Path { get; } = "/" + AppInfo.AppId.Replace('.', '/');

    public bool HandlesChildPaths => false;

    // Runs before Avalonia starts, on the main thread with no synchronization context, so waiting here is safe.
    public bool TryClaim()
    {
        try
        {
            _bus = SessionBus.GetAsync().GetAwaiter().GetResult();
            if (_bus is null)
            {
                return true;
            }

            _bus.AddMethodHandler(this);
            return _bus.TryRequestNameAsync(AppInfo.AppId, RequestNameOptions.None).WaitAsync(CallTimeout).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (SessionBus.IsFailure(ex))
        {
            AppInfo.Log($"Couldn't check for a running instance over D-Bus: {ex.Message}");
            return true;
        }
    }

    public void RequestShow()
    {
        if (_bus is null)
        {
            return;
        }

        try
        {
            _bus.CallMethodAsync(ShowCall(_bus)).WaitAsync(CallTimeout).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (SessionBus.IsFailure(ex))
        {
            AppInfo.Log($"Couldn't ask the running instance to show its window: {ex.Message}");
        }
    }

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        var request = context.Request;
        if (request.InterfaceAsString != AppInfo.AppId || request.MemberAsString != "Show")
        {
            context.ReplyUnknownMethodError();
            return default;
        }

        ShowRequested?.Invoke(this, EventArgs.Empty);
        if (!context.NoReplyExpected)
        {
            using var writer = context.CreateReplyWriter(null!);
            context.Reply(writer.CreateMessage());
        }

        return default;
    }

    public void Dispose()
    {
        // The shared connection lives as long as the process; the name goes with it.
    }

    private MessageBuffer ShowCall(DBusConnection bus)
    {
        using var writer = bus.GetMessageWriter();
        writer.WriteMethodCallHeader(AppInfo.AppId, Path, AppInfo.AppId, "Show");
        return writer.CreateMessage();
    }
}
