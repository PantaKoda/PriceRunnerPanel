using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PriceRunnerPanel.Services;

/// <summary>A private chat with the bot.</summary>
public sealed record TelegramChat(long Id, string Name);

/// <summary>
/// Sends price alerts through the user's own Telegram bot. Plain text only, so product names are never read as markup.
/// The bot token is part of Telegram's URLs, so failures are reported from Telegram's answer, never from the request.
/// </summary>
public sealed partial class TelegramSender(HttpClient http)
{
    private const string Api = "https://api.telegram.org";

    /// <summary>Telegram refuses longer messages.</summary>
    private const int MaxLength = 4096;

    private const int Attempts = 3;
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BetweenAttempts = TimeSpan.FromSeconds(3);

    public static bool IsValidToken(string token) => TokenFormat().IsMatch(token);

    public async Task SendAsync(string token, long chatId, string text)
    {
        var payload = new Dictionary<string, object>
        {
            ["chat_id"] = chatId,
            ["text"] = text.Length <= MaxLength ? text : text[..MaxLength],
            ["link_preview_options"] = new Dictionary<string, bool> { ["is_disabled"] = true },
        };
        using var answer = await CallAsync(token, "sendMessage", payload).ConfigureAwait(false);
    }

    /// <summary>
    /// The private chats among the bot's recent messages. Reading them doesn't mark them as read, so another program
    /// that uses the same bot still gets them. Telegram refuses this while the bot runs on a webhook or another poller.
    /// </summary>
    public async Task<IReadOnlyList<TelegramChat>> FindChatsAsync(string token)
    {
        using var answer = await CallAsync(token, "getUpdates?limit=100&allowed_updates=%5B%22message%22%5D", null).ConfigureAwait(false);
        var chats = new Dictionary<long, TelegramChat>();
        foreach (var update in answer.RootElement.GetProperty("result").EnumerateArray())
        {
            if (update.TryGetProperty("message", out var message) && message.TryGetProperty("chat", out var chat)
                && chat.TryGetProperty("id", out var id) && id.TryGetInt64(out var chatId)
                && chat.TryGetProperty("type", out var type) && type.GetString() == "private")
            {
                var name = chat.TryGetProperty("username", out var username) ? $"@{username.GetString()}"
                    : chat.TryGetProperty("first_name", out var first) ? first.GetString() ?? "you"
                    : "you";
                chats[chatId] = new TelegramChat(chatId, name);
            }
        }

        return [.. chats.Values];
    }

    /// <summary>
    /// A request now and then gets no answer, so one that times out or can't connect is tried again. A retry can at
    /// worst repeat an alert, which beats losing it. An answer from Telegram, even a refusal, is final.
    /// </summary>
    private async Task<JsonDocument> CallAsync(string token, string method, Dictionary<string, object>? payload)
    {
        if (!IsValidToken(token))
        {
            throw new InvalidOperationException("That bot token doesn't look right.");
        }

        var url = $"{Api}/bot{token}/{method}";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var content = payload is null ? null : JsonContent.Create(payload);
                return await AttemptAsync(url, content).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException && attempt < Attempts)
            {
                await Task.Delay(BetweenAttempts).ConfigureAwait(false);
            }
        }
    }

    private async Task<JsonDocument> AttemptAsync(string url, HttpContent? content)
    {
        try
        {
            using var attemptTimeout = new CancellationTokenSource(AttemptTimeout);
            using var response = content is null
                ? await http.GetAsync(url, attemptTimeout.Token).ConfigureAwait(false)
                : await http.PostAsync(url, content, attemptTimeout.Token).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            if (document.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                return document;
            }

            var reason = document.RootElement.TryGetProperty("description", out var description) ? description.GetString() : null;
            document.Dispose();
            throw new InvalidOperationException($"Telegram says: {reason ?? $"error {(int)response.StatusCode}"}");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Telegram sent an answer that couldn't be read.");
        }
    }

    [GeneratedRegex(@"^\d+:[A-Za-z0-9_-]{20,}$")]
    private static partial Regex TokenFormat();
}
