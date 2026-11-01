using System.Security;
using Microsoft.Win32;

namespace PriceRunnerPanel.Services;

/// <summary>
/// Starts the panel when the user signs in, through the per-user Run key (HKCU\Software\Microsoft\Windows\CurrentVersion\Run).
/// Needs no admin rights and shows in Task Manager's Startup apps, where it can also be turned off. The entry starts
/// this executable with <see cref="AtLogin"/>, which keeps the window hidden in the tray.
/// </summary>
public sealed class StartupRegistration(string executablePath, string valueName = "PriceRunnerPanel")
{
    public const string AtLogin = "--startup";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string Command => $"\"{executablePath}\" {AtLogin}";

    /// <summary>True when Windows will start this executable at sign-in.</summary>
    public bool IsRegistered
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return string.Equals(key?.GetValue(valueName) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <returns>False if Windows refused the change.</returns>
    public bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                key.SetValue(valueName, Command); // also repairs an entry pointing at an old location
            }
            else
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return false;
        }
    }
}
