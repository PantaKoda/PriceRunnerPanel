using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using PriceRunnerPanel.Models;

namespace PriceRunnerPanel.Services;

/// <summary>
/// Sends price alerts through the user's own mail account: no server of ours and no paid service. The password is kept
/// encrypted for the Windows user (DPAPI), so a copy of the state file is no use elsewhere. Always over TLS. Nothing
/// is loaded or connected until a mail is actually sent.
/// </summary>
public static class EmailSender
{
    /// <summary>The submission port, with STARTTLS. <see cref="SmtpClient"/> can't do TLS from the first byte (port 465).</summary>
    public const int SubmissionPort = 587;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static async Task SendAsync(EmailSettings settings, string subject, string body)
    {
        using var message = Compose(settings, subject, body);
        using var client = new SmtpClient(settings.Server, settings.Port)
        {
            EnableSsl = true,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(settings.Address, Unprotect(settings.ProtectedPassword)),
        };

        // SmtpClient's own Timeout doesn't apply to async sends.
        using var cts = new CancellationTokenSource(Timeout);
        await client.SendMailAsync(message, cts.Token).ConfigureAwait(false);
    }

    /// <summary>Plain text only; a subject is one line, whatever a product name contains.</summary>
    public static MailMessage Compose(EmailSettings settings, string subject, string body)
    {
        var message = new MailMessage(settings.Address, string.IsNullOrWhiteSpace(settings.SendTo) ? settings.Address : settings.SendTo)
        {
            Subject = subject.ReplaceLineEndings(" "),
            Body = body,
            IsBodyHtml = false,
            SubjectEncoding = Encoding.UTF8,
            BodyEncoding = Encoding.UTF8,
        };
        return message;
    }

    /// <summary>The mail server of the common providers, else the usual name for the address's domain.</summary>
    public static string ServerFor(string address)
    {
        var domain = address[(address.LastIndexOf('@') + 1)..].Trim().ToLowerInvariant();
        return domain switch
        {
            "gmail.com" or "googlemail.com" => "smtp.gmail.com",
            "outlook.com" or "hotmail.com" or "live.com" or "msn.com" => "smtp-mail.outlook.com",
            "icloud.com" or "me.com" or "mac.com" => "smtp.mail.me.com",
            "yahoo.com" or "yahoo.se" => "smtp.mail.yahoo.com",
            _ => $"smtp.{domain}",
        };
    }

    public static string Protect(string password) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser));

    private static string Unprotect(string protectedPassword) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedPassword), null, DataProtectionScope.CurrentUser));
}
