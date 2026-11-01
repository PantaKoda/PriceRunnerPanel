using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace PriceRunnerPanel.Services;

/// <summary>
/// The app's shortcut in the per-user Start menu (no admin rights), which also makes it findable in Windows search.
/// It carries the app's AppUserModelID, so a shortcut pinned to the taskbar and the running app share one button.
/// </summary>
public sealed class StartMenuShortcut(string executablePath)
{
    private const string FileName = "PriceRunner Panel.lnk";
    private const ushort VtLpwstr = 31;

    // PKEY_AppUserModel_ID
    private static readonly PropertyKey AppUserModelIdKey = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    private static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), FileName);

    public bool Exists => File.Exists(ShortcutPath);

    /// <summary>True when the shortcut exists and opens this executable.</summary>
    public bool IsCurrent
    {
        get
        {
            if (!Exists)
            {
                return false;
            }

            try
            {
                var link = (IShellLinkW)new ShellLink();
                ((IPersistFile)link).Load(ShortcutPath, 0);
                var target = new StringBuilder(1024);
                link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
                return string.Equals(target.ToString(), executablePath, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (IsShortcutFailure(ex))
            {
                return false;
            }
        }
    }

    /// <summary>Creates the shortcut, or points an existing one at this executable.</summary>
    /// <returns>False if Windows refused.</returns>
    public bool Save()
    {
        var value = IntPtr.Zero;
        try
        {
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(executablePath);
            link.SetWorkingDirectory(Path.GetDirectoryName(executablePath) ?? "");
            link.SetIconLocation(executablePath, 0);
            link.SetDescription("Track prices on PriceRunner");

            value = Marshal.StringToCoTaskMemUni(ToastNotifier.AppUserModelId);
            var key = AppUserModelIdKey;
            var variant = new PropVariant { Type = VtLpwstr, Value = value };
            var properties = (IPropertyStore)link;
            properties.SetValue(ref key, ref variant);
            properties.Commit();

            Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath)!);
            ((IPersistFile)link).Save(ShortcutPath, true);
            return true;
        }
        catch (Exception ex) when (IsShortcutFailure(ex))
        {
            return false;
        }
        finally
        {
            Marshal.FreeCoTaskMem(value);
        }
    }

    private static bool IsShortcutFailure(Exception ex) =>
        ex is COMException or UnauthorizedAccessException or IOException or InvalidCastException;

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PropertyKey(Guid FormatId, uint PropertyId);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Type;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr Value;
        public IntPtr Padding;
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    // The methods are declared in vtable order, including the ones this class doesn't call.
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr findData, uint flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }
}
