namespace Identity.Server.Services;

/// <summary>Writes mails to the log instead of sending them. Used when no SMTP host is configured.</summary>
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string to, string subject, string htmlBody, CancellationToken cancellationToken = default)
    {
        logger.LogWarning("No SMTP host configured. Mail to {To} with subject {Subject}:\n{Body}", to, subject, htmlBody);
        return Task.CompletedTask;
    }
}
