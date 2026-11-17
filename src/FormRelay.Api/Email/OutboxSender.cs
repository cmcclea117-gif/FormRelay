using Dapper;
using Microsoft.Data.SqlClient;

namespace FormRelay.Api.Email;

/// <summary>
/// Background worker that drains dbo.Notifications. Submitting a form never waits on, or fails because of, email:
/// the API only writes an outbox row, and this sends it, retrying with a growing delay before giving up.
/// </summary>
public class OutboxSender(IConfiguration config, IEmailSender mail, ILogger<OutboxSender> log) : BackgroundService
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connStr = config.GetConnectionString("Default")!;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DrainAsync(connStr, stoppingToken); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Outbox pass failed; will retry");
            }
            try { await Task.Delay(PollEvery, stoppingToken); } catch (OperationCanceledException) { }
        }
    }

    private async Task DrainAsync(string connStr, CancellationToken ct)
    {
        await using var db = new SqlConnection(connStr);
        var due = await db.QueryAsync<Row>("""
            SELECT TOP 20 Id, ToAddresses, Subject, Body, Attempts
            FROM dbo.Notifications
            WHERE Status = 'pending' AND NextAttemptUtc <= SYSUTCDATETIME()
            ORDER BY Id
            """);

        foreach (var n in due)
        {
            try
            {
                await mail.SendAsync(n.ToAddresses, n.Subject, n.Body, ct);
                await db.ExecuteAsync(
                    "UPDATE dbo.Notifications SET Status = 'sent', SentUtc = SYSUTCDATETIME(), Attempts = Attempts + 1, LastError = NULL WHERE Id = @Id",
                    new { n.Id });
                log.LogInformation("Sent notification {Id} to {To}", n.Id, n.ToAddresses);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                var attempts = n.Attempts + 1;
                var gaveUp = attempts >= MaxAttempts;
                await db.ExecuteAsync("""
                    UPDATE dbo.Notifications
                    SET Attempts = @attempts,
                        Status = @status,
                        LastError = LEFT(@error, 2000),
                        NextAttemptUtc = DATEADD(MINUTE, @delay, SYSUTCDATETIME())
                    WHERE Id = @Id
                    """,
                    new { n.Id, attempts, status = gaveUp ? "failed" : "pending", error = e.Message, delay = attempts * 2 });
                log.LogWarning("Notification {Id} attempt {Attempt} failed: {Error}", n.Id, attempts, e.Message);
            }
        }
    }

    private record Row(long Id, string ToAddresses, string Subject, string Body, int Attempts);
}
