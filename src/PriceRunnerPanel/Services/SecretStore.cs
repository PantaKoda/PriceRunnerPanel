using PriceRunnerPanel.Platform;

namespace PriceRunnerPanel.Services;

/// <summary>Passwords and tokens in the state file, encrypted for this user by the system's own store (see <see cref="ISecretStore"/>).</summary>
public static class SecretStore
{
    public static Task<string> ProtectAsync(string secret) => PlatformServices.Secrets.ProtectAsync(secret);

    public static Task<string> UnprotectAsync(string protectedSecret) => PlatformServices.Secrets.UnprotectAsync(protectedSecret);
}
