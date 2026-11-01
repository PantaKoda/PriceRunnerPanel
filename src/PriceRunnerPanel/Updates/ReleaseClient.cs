using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PriceRunnerPanel.Updates;

/// <summary>Releases, or why they couldn't be read (shown to the user as-is).</summary>
public sealed record ReleaseListResult(IReadOnlyList<ReleaseInfo>? Releases, string? Error)
{
    public bool IsSuccess => Releases is not null;
}

/// <summary>
/// Reads the app's public GitHub releases anonymously and downloads release files. Downloads are accepted only from
/// this repository's release download URLs on github.com over HTTPS; GitHub then redirects to its file storage.
/// </summary>
public sealed class ReleaseClient : IDisposable
{
    private const int MaxNotesLength = 20_000;
    private static readonly Uri ApiBase = new("https://api.github.com/");
    private const string WebHost = "github.com";

    private readonly HttpClient _http;
    private readonly string _repository;

    public ReleaseClient(string repository)
    {
        _repository = repository;
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            // Redirects (to GitHub's file storage) must stay on HTTPS; .NET never follows HTTPS → HTTP anyway.
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"PriceRunnerPanel/{AppInfo.Version} (+https://github.com/{repository})");
    }

    /// <summary>A download that receives nothing for this long is abandoned (a socket can stall silently).</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public Uri ReleasesPage => new($"https://{WebHost}/{_repository}/releases");

    public async Task<ReleaseListResult> GetReleasesAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiBase, $"repos/{_repository}/releases?per_page=30"));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new(null, $"GitHub has no public releases for {_repository}.");
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                return new(null, "GitHub's limit for anonymous checks was reached. Try again in an hour.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new(null, $"GitHub answered {(int)response.StatusCode} when checking for updates.");
            }

            var dtos = await response.Content.ReadFromJsonAsync(ReleaseJsonContext.Default.ListReleaseDto, cancellationToken).ConfigureAwait(false) ?? [];
            return new([.. dtos.Select(d => Map(d, ReleasesPage)).OfType<ReleaseInfo>()], null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or (TaskCanceledException and not OperationCanceledException { CancellationToken.IsCancellationRequested: true }))
        {
            return new(null, "Couldn't reach GitHub to check for updates.");
        }
    }

    /// <summary>True when <paramref name="url"/> is a release download of this repository.</summary>
    public bool IsReleaseDownload(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(url.UserInfo) && url.IsDefaultPort
        && string.Equals(url.IdnHost, WebHost, StringComparison.OrdinalIgnoreCase)
        && url.AbsolutePath.StartsWith($"/{_repository}/releases/download/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Downloads a release file into <paramref name="destination"/>, refusing anything larger than <paramref name="maxBytes"/>.</summary>
    public async Task DownloadAsync(Uri url, Stream destination, long maxBytes, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!IsReleaseDownload(url))
        {
            throw new InvalidOperationException($"Refusing to download '{url}': it isn't a release file of {_repository}.");
        }

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("The download was redirected away from HTTPS.");
        }

        var total = response.Content.Headers.ContentLength;
        if (total > maxBytes)
        {
            throw new InvalidOperationException($"The download is larger ({total} bytes) than expected.");
        }

        var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            var buffer = new byte[81920];
            long received = 0;
            while (true)
            {
                int read;
                using (var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    stall.CancelAfter(StallTimeout);
                    try
                    {
                        read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException($"the download stalled (nothing received for {StallTimeout.TotalSeconds:0} seconds)");
                    }
                }

                if (read == 0)
                {
                    break;
                }

                received += read;
                if (received > maxBytes)
                {
                    throw new InvalidOperationException("The download is larger than expected.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                if (total is > 0)
                {
                    progress?.Report((double)received / total.Value);
                }
            }
        }
    }

    public void Dispose() => _http.Dispose();

    private static ReleaseInfo? Map(ReleaseDto dto, Uri releasesPage)
    {
        if (!AppVersion.TryParse(dto.TagName, out var version))
        {
            return null; // not a version tag: not an update
        }

        // "View on GitHub" opens this link: it must be one of this repository's release pages, else the list page.
        var html = Uri.TryCreate(dto.HtmlUrl, UriKind.Absolute, out var page) && page.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(page.UserInfo) && page.IsDefaultPort
            && string.Equals(page.IdnHost, releasesPage.IdnHost, StringComparison.OrdinalIgnoreCase)
            && page.AbsolutePath.StartsWith(releasesPage.AbsolutePath + "/", StringComparison.OrdinalIgnoreCase)
            ? page
            : releasesPage;

        var notes = dto.Body ?? "";
        return new ReleaseInfo
        {
            Version = version,
            Tag = dto.TagName!,
            Title = string.IsNullOrWhiteSpace(dto.Name) ? $"PriceRunner Panel {version}" : dto.Name.Trim(),
            Notes = notes.Length > MaxNotesLength ? notes[..MaxNotesLength] + "\n…" : notes,
            HtmlUrl = html,
            PublishedAt = dto.PublishedAt,
            IsDraft = dto.Draft,
            IsPreRelease = dto.Prerelease,
            Assets = [.. (dto.Assets ?? [])
                .Where(a => !string.IsNullOrEmpty(a.Name) && Uri.TryCreate(a.BrowserDownloadUrl, UriKind.Absolute, out _))
                .Select(a => new ReleaseAsset(a.Name!, new Uri(a.BrowserDownloadUrl!), a.Size))],
        };
    }
}

internal sealed record ReleaseDto
{
    [JsonPropertyName("tag_name")] public string? TagName { get; init; }

    [JsonPropertyName("name")] public string? Name { get; init; }

    [JsonPropertyName("body")] public string? Body { get; init; }

    [JsonPropertyName("html_url")] public string? HtmlUrl { get; init; }

    [JsonPropertyName("draft")] public bool Draft { get; init; }

    [JsonPropertyName("prerelease")] public bool Prerelease { get; init; }

    [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; init; }

    [JsonPropertyName("assets")] public List<ReleaseAssetDto>? Assets { get; init; }
}

internal sealed record ReleaseAssetDto
{
    [JsonPropertyName("name")] public string? Name { get; init; }

    [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; init; }

    [JsonPropertyName("size")] public long Size { get; init; }
}

[JsonSerializable(typeof(List<ReleaseDto>))]
internal sealed partial class ReleaseJsonContext : JsonSerializerContext;
