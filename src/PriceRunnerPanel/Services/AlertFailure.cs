using System.Net.Mail;
using System.Security.Cryptography;

namespace PriceRunnerPanel.Services;

/// <summary>What can go wrong when sending an alert by email or Telegram, in words for the user.</summary>
public static class AlertFailure
{
    public static bool Is(Exception ex) =>
        ex is SmtpException or InvalidOperationException or FormatException or CryptographicException or OperationCanceledException or IOException or System.Net.Http.HttpRequestException;

    public static string Describe(Exception ex) => ex switch
    {
        OperationCanceledException => "no answer in time",
        System.Net.Http.HttpRequestException => "couldn't connect",
        CryptographicException => "the saved secret can't be read on this Windows account; type it again",
        // "Failure sending mail." says nothing; the reason is inside.
        SmtpException { InnerException: { } inner } => inner.Message,
        _ => ex.Message,
    };
}
