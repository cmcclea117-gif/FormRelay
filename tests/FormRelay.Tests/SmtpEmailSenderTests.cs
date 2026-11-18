using System.Net;
using System.Net.Sockets;
using System.Text;
using FormRelay.Api.Email;
using Microsoft.Extensions.Configuration;

namespace FormRelay.Tests;

/// <summary>Sends through the real MailKit client to a tiny in-process SMTP server, so a MailKit upgrade is caught by CI.</summary>
public class SmtpEmailSenderTests
{
    private sealed class FakeSmtp : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public List<string> Recipients { get; } = [];
        public string? MailFrom { get; private set; }
        public string Data { get; private set; } = "";
        public Task Done { get; }

        public FakeSmtp() { _listener.Start(); Done = Task.Run(ServeOnce); }

        private async Task ServeOnce()
        {
            using var client = await _listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            async Task Say(string s) => await stream.WriteAsync(Encoding.ASCII.GetBytes(s + "\r\n"));

            await Say("220 fake ESMTP");
            while (await reader.ReadLineAsync() is { } line)
            {
                var cmd = line.ToUpperInvariant();
                if (cmd.StartsWith("EHLO") || cmd.StartsWith("HELO")) await Say("250 fake");
                else if (cmd.StartsWith("MAIL FROM")) { MailFrom = line; await Say("250 ok"); }
                else if (cmd.StartsWith("RCPT TO")) { Recipients.Add(line); await Say("250 ok"); }
                else if (cmd == "DATA")
                {
                    await Say("354 go ahead");
                    var sb = new StringBuilder();
                    while (await reader.ReadLineAsync() is { } d && d != ".") sb.AppendLine(d);
                    Data = sb.ToString();
                    await Say("250 queued");
                }
                else if (cmd == "QUIT") { await Say("221 bye"); break; }
                else await Say("250 ok");
            }
        }

        public void Dispose() => _listener.Stop();
    }

    private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => p.Value)).Build();

    [Theory]
    [InlineData(null)]     // no Smtp section at all (what the installer writes when email is skipped)
    [InlineData("")]       // an empty host (what 0.1.1 wrote when email was skipped)
    [InlineData("   ")]
    public async Task Without_a_mail_server_it_reports_not_configured_and_refuses_to_send(string? host)
    {
        var sender = new SmtpEmailSender(Config(("Smtp:Host", host), ("Smtp:Port", "25")));
        Assert.False(sender.IsConfigured);
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync("a@example.com", "s", "b", default));
        Assert.Contains("mail server", e.Message);
    }

    [Fact]
    public void With_a_host_it_is_configured() =>
        Assert.True(new SmtpEmailSender(Config(("Smtp:Host", "mail.example.com"))).IsConfigured);

    [Fact]
    public async Task Sends_one_message_to_every_recipient()
    {
        using var smtp = new FakeSmtp();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Smtp:Host"] = "127.0.0.1",
            ["Smtp:Port"] = smtp.Port.ToString(),
            ["Smtp:UseTls"] = "false",
            ["Smtp:From"] = "formrelay@example.com",
        }).Build();

        await new SmtpEmailSender(config).SendAsync("a@example.com; b@example.com", "New request: lunch", "Hello from FormRelay", default);
        await smtp.Done.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("formrelay@example.com", smtp.MailFrom);
        Assert.Equal(2, smtp.Recipients.Count);
        Assert.Contains(smtp.Recipients, r => r.Contains("a@example.com"));
        Assert.Contains(smtp.Recipients, r => r.Contains("b@example.com"));
        Assert.Contains("Subject: New request: lunch", smtp.Data);
        Assert.Contains("Hello from FormRelay", smtp.Data);
    }
}
