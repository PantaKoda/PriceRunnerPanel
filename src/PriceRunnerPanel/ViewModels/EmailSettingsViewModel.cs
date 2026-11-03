using System.Net.Mail;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PriceRunnerPanel.Models;
using PriceRunnerPanel.Services;

namespace PriceRunnerPanel.ViewModels;

/// <summary>The mail account that price alerts are sent through, set up from the watchlist.</summary>
public sealed partial class EmailSettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Func<Task> _save;

    public EmailSettingsViewModel(AppSettings settings, Func<Task> save)
    {
        _settings = settings;
        _save = save;
        Address = settings.Email?.Address ?? "";
        SendTo = settings.Email?.SendTo ?? "";
        Server = settings.Email?.Server ?? "";
    }

    [ObservableProperty]
    public partial string Address { get; set; }

    /// <summary>Where alerts go, when not to the same address.</summary>
    [ObservableProperty]
    public partial string SendTo { get; set; }

    [ObservableProperty]
    public partial string Server { get; set; }

    /// <summary>A newly typed password; empty keeps the saved one. Never read back from the saved settings.</summary>
    [ObservableProperty]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsSending { get; set; }

    public bool IsIdle => !IsSending;

    public bool IsConfigured => _settings.Email is not null;

    public string PasswordHint => IsConfigured ? "Password saved (type to replace)" : "App password";

    /// <summary>Sends a mail with the configured account; throws what <see cref="Describe"/> can explain.</summary>
    public Task SendAsync(string subject, string body) =>
        _settings.Email is { } email ? EmailSender.SendAsync(email, subject, body) : Task.CompletedTask;

    public static bool IsSendFailure(Exception ex) =>
        ex is SmtpException or InvalidOperationException or FormatException or CryptographicException or OperationCanceledException or IOException;

    public static string Describe(Exception ex) => ex switch
    {
        OperationCanceledException => "the mail server did not answer in time",
        CryptographicException => "the saved password can't be read on this Windows account; type it again",
        // "Failure sending mail." says nothing; the reason is inside.
        SmtpException { InnerException: { } inner } => inner.Message,
        _ => ex.Message,
    };

    [RelayCommand]
    private async Task SaveAndTestAsync()
    {
        var address = Address.Trim();
        var sendTo = SendTo.Trim();
        if (!MailAddress.TryCreate(address, out _))
        {
            Status = "That email address doesn't look right.";
            return;
        }

        if (sendTo.Length > 0 && !MailAddress.TryCreate(sendTo, out _))
        {
            Status = "The address to send to doesn't look right.";
            return;
        }

        if (Password.Length == 0 && !IsConfigured)
        {
            Status = "Type the app password of this account.";
            return;
        }

        var server = Server.Trim();
        if (server.Length == 0)
        {
            server = EmailSender.ServerFor(address);
            Server = server;
        }

        IsSending = true;
        Status = "Sending a test email…";
        try
        {
            _settings.Email = new EmailSettings
            {
                Address = address,
                SendTo = sendTo.Length > 0 ? sendTo : null,
                Server = server,
                Port = EmailSender.SubmissionPort,
                ProtectedPassword = Password.Length > 0 ? EmailSender.Protect(Password) : _settings.Email!.ProtectedPassword,
            };
            Password = "";
            OnPropertyChanged(nameof(IsConfigured));
            OnPropertyChanged(nameof(PasswordHint));
            await _save();
            await EmailSender.SendAsync(_settings.Email, "PriceRunner Panel: test email", "Email alerts work. Turn them on with the envelope on each watchlist card.");
            Status = $"Test email sent to {(sendTo.Length > 0 ? sendTo : address)}. Check your inbox.";
        }
        catch (Exception ex) when (IsSendFailure(ex))
        {
            Status = $"Saved, but the test failed: {Describe(ex)}";
        }
        finally
        {
            IsSending = false;
        }
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        _settings.Email = null;
        Address = SendTo = Server = Password = "";
        Status = "Email account removed. No alerts will be emailed.";
        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(PasswordHint));
        await _save();
    }
}
