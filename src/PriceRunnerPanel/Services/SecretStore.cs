using System.Security.Cryptography;
using System.Text;

namespace PriceRunnerPanel.Services;

/// <summary>Keeps passwords and tokens encrypted for the Windows user (DPAPI), so a copy of the state file is no use elsewhere.</summary>
public static class SecretStore
{
    public static string Protect(string secret) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser));

    public static string Unprotect(string protectedSecret) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedSecret), null, DataProtectionScope.CurrentUser));
}
