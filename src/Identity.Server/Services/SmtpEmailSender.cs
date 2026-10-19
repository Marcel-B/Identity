using Identity.Server.Options;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Identity.Server.Services;

public sealed class SmtpEmailSender(IOptions<EmailOptions> options, IOptions<ServerOptions> serverOptions) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string htmlBody, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(serverOptions.Value.DisplayName, settings.From));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = htmlBody }.ToMessageBody();

        using var client = new SmtpClient();
        await client.ConnectAsync(settings.Host!, settings.Port,
            settings.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto, cancellationToken);

        if (!string.IsNullOrEmpty(settings.UserName))
        {
            await client.AuthenticateAsync(settings.UserName, settings.Password ?? "", cancellationToken);
        }

        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }
}
