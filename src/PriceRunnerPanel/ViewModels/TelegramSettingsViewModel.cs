using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PriceRunnerPanel.Models;
using PriceRunnerPanel.Services;

namespace PriceRunnerPanel.ViewModels;

/// <summary>The Telegram bot that price alerts are sent through, set up from the watchlist.</summary>
public sealed partial class TelegramSettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly TelegramSender _sender;
    private readonly Func<Task> _save;

    public TelegramSettingsViewModel(AppSettings settings, TelegramSender sender, Func<Task> save)
    {
        _settings = settings;
        _sender = sender;
        _save = save;
        ChatId = settings.Telegram?.ChatId.ToString(CultureInfo.InvariantCulture) ?? "";
    }

    /// <summary>A newly typed token; empty keeps the saved one. Never read back from the saved settings.</summary>
    [ObservableProperty]
    public partial string Token { get; set; } = "";

    /// <summary>Filled in from the bot's messages when left empty.</summary>
    [ObservableProperty]
    public partial string ChatId { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsSending { get; set; }

    public bool IsIdle => !IsSending;

    public bool IsConfigured => _settings.Telegram is not null;

    public string TokenHint => IsConfigured ? "Bot token saved (type to replace)" : "Bot token from @BotFather";

    public async Task SendAsync(string text)
    {
        if (_settings.Telegram is { } telegram)
        {
            await _sender.SendAsync(SecretStore.Unprotect(telegram.ProtectedToken), telegram.ChatId, text);
        }
    }

    [RelayCommand]
    private async Task SaveAndTestAsync()
    {
        var typed = Token.Trim();
        if (typed.Length == 0 && !IsConfigured)
        {
            Status = "Paste your bot's token first.";
            return;
        }

        if (typed.Length > 0 && !TelegramSender.IsValidToken(typed))
        {
            Status = "That bot token doesn't look right. It looks like 123456:ABC-…";
            return;
        }

        var chatText = ChatId.Trim();
        long chatId = 0;
        if (chatText.Length > 0 && !long.TryParse(chatText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out chatId))
        {
            Status = "A chat ID is a number. Leave it empty to find yours.";
            return;
        }

        IsSending = true;
        Status = "Sending a test message…";
        try
        {
            var token = typed.Length > 0 ? typed : SecretStore.Unprotect(_settings.Telegram!.ProtectedToken);
            if (chatText.Length == 0)
            {
                var chats = await _sender.FindChatsAsync(token);
                if (chats.Count != 1)
                {
                    // Several chats: guessing could send alerts to someone else.
                    Status = chats.Count == 0
                        ? "Send your bot any message in Telegram, then click again."
                        : "Several people message this bot. Type your chat ID, or use a bot of your own.";
                    return;
                }

                chatId = chats[0].Id;
                ChatId = chatId.ToString(CultureInfo.InvariantCulture);
            }

            await _sender.SendAsync(token, chatId, "PriceRunner Panel: Telegram alerts work. Turn them on with the bell on each watchlist card.");
            _settings.Telegram = new TelegramSettings { ProtectedToken = SecretStore.Protect(token), ChatId = chatId };
            Token = "";
            OnPropertyChanged(nameof(IsConfigured));
            OnPropertyChanged(nameof(TokenHint));
            await _save();
            Status = "Test message sent. Check Telegram, then click the bell on the cards you want alerts for.";
        }
        catch (Exception ex) when (AlertFailure.Is(ex))
        {
            Status = $"Not saved: {AlertFailure.Describe(ex)}";
        }
        finally
        {
            IsSending = false;
        }
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        _settings.Telegram = null;
        Token = ChatId = "";
        Status = "Telegram removed. No alerts will be sent there.";
        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(TokenHint));
        await _save();
    }
}
