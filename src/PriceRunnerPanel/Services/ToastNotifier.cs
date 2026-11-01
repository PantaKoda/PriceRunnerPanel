using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace PriceRunnerPanel.Services;

/// <summary>
/// Windows toast notifications for the unpackaged app. Windows needs an app identity for toasts: the app registers
/// its AppUserModelID with a display name under HKCU (per user, no admin rights) and uses it for this process.
/// Toasts respect the OS settings (Do not disturb, per-app switches). Clicking one opens its link.
/// </summary>
public sealed class ToastNotifier
{
    public const string AppUserModelId = "PriceRunnerPanel.Desktop";
    private const string Group = "prices";

    private readonly Windows.UI.Notifications.ToastNotifier? _notifier;

    public ToastNotifier()
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppUserModelId}"))
            {
                key.SetValue("DisplayName", "PriceRunner Panel");
            }

            _ = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            _notifier = ToastNotificationManager.CreateToastNotifier(AppUserModelId);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or SecurityException or IOException)
        {
            _notifier = null;
        }
    }

    public bool Show(string title, string body, string tag, Action? onActivated)
    {
        if (_notifier is null)
        {
            return false;
        }

        try
        {
            var xml = new XmlDocument();
            var toast = xml.CreateElement("toast");
            xml.AppendChild(toast);
            var visual = xml.CreateElement("visual");
            toast.AppendChild(visual);
            var binding = xml.CreateElement("binding");
            binding.SetAttribute("template", "ToastGeneric");
            visual.AppendChild(binding);
            foreach (var line in new[] { title, body })
            {
                var text = xml.CreateElement("text");
                text.InnerText = line; // plain text: product names are never interpreted as markup
                binding.AppendChild(text);
            }

            var shown = new ToastNotification(xml) { Tag = TagFor(tag), Group = Group };
            if (onActivated is not null)
            {
                shown.Activated += (_, _) => onActivated();
            }

            _notifier.Show(shown);
            return true;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Clears this tag's notifications from the Action Center (e.g. for a product that was unpinned).</summary>
    public void Remove(string tag)
    {
        if (_notifier is null)
        {
            return;
        }

        try
        {
            ToastNotificationManager.History.Remove(TagFor(tag), Group, AppUserModelId);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
        }
    }

    private static string TagFor(string tag) => tag.Length <= 64 ? tag : tag[..64];

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
