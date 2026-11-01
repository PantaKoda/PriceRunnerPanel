using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;

namespace PriceRunnerPanel.Services;

/// <summary>Product thumbnails: fetched once, kept on disk and in memory. Failures just mean no picture.</summary>
public sealed class ImageLoader(HttpClient http, string cacheDirectory)
{
    private const int DecodeWidth = 112;

    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _memory = new();

    public Task<Bitmap?> LoadAsync(string? url) =>
        string.IsNullOrEmpty(url) ? Task.FromResult<Bitmap?>(null) : _memory.GetOrAdd(url, LoadCoreAsync);

    private async Task<Bitmap?> LoadCoreAsync(string url)
    {
        var file = Path.Combine(cacheDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32] + ".img");
        try
        {
            byte[] bytes;
            if (File.Exists(file))
            {
                bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
            }
            else
            {
                bytes = await http.GetByteArrayAsync(new Uri(url)).ConfigureAwait(false);
                Directory.CreateDirectory(cacheDirectory);
                await File.WriteAllBytesAsync(file, bytes).ConfigureAwait(false);
            }

            using var stream = new MemoryStream(bytes);
            return Bitmap.DecodeToWidth(stream, DecodeWidth);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            _memory.TryRemove(url, out _);
            return null;
        }
    }
}
