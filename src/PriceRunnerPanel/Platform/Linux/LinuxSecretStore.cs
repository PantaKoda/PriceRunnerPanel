using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Tmds.DBus.Protocol;

namespace PriceRunnerPanel.Platform;

/// <summary>
/// Encrypts passwords and tokens with AES-GCM under a key derived from a per-app master secret. Inside a Flatpak the
/// master secret comes from the Secret portal (org.freedesktop.portal.Secret), which keeps it in the user's keyring
/// (KWallet, GNOME Keyring), so a copy of the app's data folder is no use without the user's login. Outside a Flatpak
/// it is a random key file next to the state, readable only by the user: that protects against a copied state file,
/// not against someone who can read the whole data folder.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxSecretStore : ISecretStore
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <summary>The keyring may have to be unlocked first, so give the user time.</summary>
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromMinutes(2);

    private static readonly byte[] KeyInfo = "PriceRunnerPanel secrets v1"u8.ToArray();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private byte[]? _key;

    public string UnreadableMessage => SessionBus.InFlatpak
        ? "the saved secret can't be read (the desktop's keyring didn't hand over the app's key); type it again"
        : "the saved secret can't be read with this user's key; type it again";

    /// <summary>The key file used outside a Flatpak.</summary>
    public static string KeyFile => Path.Combine(AppInfo.DataDirectory, "secret.key");

    public async Task<string> ProtectAsync(string secret)
    {
        var key = await GetKeyAsync().ConfigureAwait(false);
        var plain = Encoding.UTF8.GetBytes(secret);
        var sealedBytes = new byte[NonceSize + TagSize + plain.Length];
        var nonce = sealedBytes.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, sealedBytes.AsSpan(NonceSize + TagSize), sealedBytes.AsSpan(NonceSize, TagSize));
        return Convert.ToBase64String(sealedBytes);
    }

    public async Task<string> UnprotectAsync(string protectedSecret)
    {
        var sealedBytes = Convert.FromBase64String(protectedSecret);
        if (sealedBytes.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("The saved secret is too short.");
        }

        var key = await GetKeyAsync().ConfigureAwait(false);
        var plain = new byte[sealedBytes.Length - NonceSize - TagSize];
        using var aes = new AesGcm(key, TagSize);
        // Throws AuthenticationTagMismatchException (a CryptographicException) for another key, e.g. a state file
        // copied from Windows or from another user.
        aes.Decrypt(sealedBytes.AsSpan(0, NonceSize), sealedBytes.AsSpan(NonceSize + TagSize), sealedBytes.AsSpan(NonceSize, TagSize), plain);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>Fetches the master secret once per run; a failure is retried next time.</summary>
    private async Task<byte[]> GetKeyAsync()
    {
        if (_key is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_key is null)
            {
                var master = SessionBus.InFlatpak ? await RetrieveFromPortalAsync().ConfigureAwait(false) : ReadOrCreateKeyFile();
                _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, salt: null, info: KeyInfo);
                CryptographicOperations.ZeroMemory(master);
            }

            return _key;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The portal writes the app's secret into a pipe we hand it, then answers through a Request.</summary>
    private static async Task<byte[]> RetrieveFromPortalAsync()
    {
        try
        {
            if (await SessionBus.GetAsync().ConfigureAwait(false) is not { } bus)
            {
                throw new CryptographicException("there is no D-Bus session bus to reach the Secret portal");
            }

            using var pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
            using var secret = new MemoryStream();
            var token = Portal.NewToken();
            var call = RetrieveSecret(bus, token, pipe.ClientSafePipeHandle);
            using var timeout = new CancellationTokenSource(AnswerTimeout);
            var read = pipe.CopyToAsync(secret, timeout.Token);
            // Once the call is out, the portal holds the write end; EOF comes when it has written the secret and closed it.
            var (response, _) = await Portal.RequestAsync(bus, token, call, AnswerTimeout, pipe.DisposeLocalCopyOfClientHandle).ConfigureAwait(false);
            if (response != 0)
            {
                throw new CryptographicException($"the Secret portal refused (response {response})");
            }

            await read.ConfigureAwait(false);
            if (secret.Length == 0)
            {
                throw new CryptographicException("the Secret portal sent an empty secret");
            }

            return secret.ToArray();
        }
        catch (Exception ex) when (SessionBus.IsFailure(ex))
        {
            AppInfo.Log($"The Secret portal failed: {ex.Message}");
            throw new CryptographicException($"the Secret portal failed: {ex.Message}", ex);
        }
    }

    private static MessageBuffer RetrieveSecret(DBusConnection bus, string token, SafePipeHandle writeEnd)
    {
        var writer = bus.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(Portal.Service, Portal.ObjectPath, "org.freedesktop.portal.Secret", "RetrieveSecret", "ha{sv}");
            writer.WriteHandle(writeEnd);
            var options = writer.WriteDictionaryStart();
            Portal.Entry(ref writer, "handle_token", token);
            writer.WriteDictionaryEnd(options);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static byte[] ReadOrCreateKeyFile()
    {
        try
        {
            if (File.Exists(KeyFile))
            {
                return File.ReadAllBytes(KeyFile);
            }

            Directory.CreateDirectory(AppInfo.DataDirectory);
            var key = RandomNumberGenerator.GetBytes(32);
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            };
            using (var file = new FileStream(KeyFile, options))
            {
                file.Write(key);
            }

            return key;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CryptographicException($"the key file couldn't be used ({ex.Message})", ex);
        }
    }
}
