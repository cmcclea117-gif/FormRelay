using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace FormRelay.Api.Email;

/// <summary>Anything that can send a plain-text email. Swap implementations without touching callers.</summary>
public interface IEmailSender
{
    Task SendAsync(string toAddresses, string subject, string body, CancellationToken ct);
}

/// <summary>Sends through any SMTP server (smtp4dev locally, a relay or Microsoft 365 later). Configured in the "Smtp" section.</summary>
public class SmtpEmailSender(IConfiguration config) : IEmailSender
{
    public async Task SendAsync(string toAddresses, string subject, string body, CancellationToken ct)
    {
        var s = config.GetSection("Smtp");
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(s["From"] ?? "forms@localhost"));
        foreach (var addr in toAddresses.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            message.To.Add(MailboxAddress.Parse(addr));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        using var client = new SmtpClient();
        var tls = s.GetValue("UseTls", false) ? SecureSocketOptions.StartTls : SecureSocketOptions.None;
        await client.ConnectAsync(s["Host"] ?? "localhost", s.GetValue("Port", 25), tls, ct);
        if (!string.IsNullOrEmpty(s["User"]))
            await client.AuthenticateAsync(s["User"], s["Password"], ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }
}
