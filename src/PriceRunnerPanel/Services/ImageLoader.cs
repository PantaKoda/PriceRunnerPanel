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

    /// <summary>Forgets one picture, in memory and on disk (an unpinned product leaves nothing behind).</summary>
    public void Delete(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        _memory.TryRemove(url, out _);
        TryDelete(FileFor(url));
    }

    /// <summary>Lets go of a picture in memory (the file stays); rows that still show it keep their own reference.</summary>
    public void Release(string? url)
    {
        if (!string.IsNullOrEmpty(url))
        {
            _memory.TryRemove(url, out _);
        }
    }

    /// <summary>Deletes every cached picture except <paramref name="keepUrls"/>, e.g. search thumbnails from earlier runs.</summary>
    public void Prune(IEnumerable<string?> keepUrls)
    {
        var keep = keepUrls.OfType<string>().Select(FileFor).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(cacheDirectory))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(cacheDirectory, "*.img").Where(f => !keep.Contains(f)).ToList())
            {
                TryDelete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string FileFor(string url) =>
        Path.Combine(cacheDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32] + ".img");

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task<Bitmap?> LoadCoreAsync(string url)
    {
        var file = FileFor(url);
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
