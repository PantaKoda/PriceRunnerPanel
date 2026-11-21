using System.Security.Cryptography;
using System.Text;

namespace PriceRunnerPanel.Platform;

/// <summary>Keeps passwords and tokens encrypted for the Windows user (DPAPI), so a copy of the state file is no use elsewhere.</summary>
public sealed class WindowsSecretStore : ISecretStore
{
    public string UnreadableMessage => "the saved secret can't be read on this Windows account; type it again";

    public Task<string> ProtectAsync(string secret) => Run(() =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser)));

    public Task<string> UnprotectAsync(string protectedSecret) => Run(() =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedSecret), null, DataProtectionScope.CurrentUser)));

    // DPAPI is synchronous and fast: run it in place, so callers see its exceptions exactly as before.
    private static Task<string> Run(Func<string> work)
    {
        try
        {
            return Task.FromResult(work());
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return Task.FromException<string>(ex);
        }
    }
}
