using System.Text;
using System.Text.Encodings.Web;
using Identity.Server.Data;
using Identity.Server.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Identity.Server.Services;

/// <summary>Builds and sends the account mails (confirm address, reset password).</summary>
public sealed class AccountEmails(
    UserManager<ApplicationUser> userManager,
    IEmailSender sender,
    IOptions<ServerOptions> serverOptions,
    LinkGenerator links,
    IHttpContextAccessor httpContextAccessor)
{
    public async Task SendConfirmationAsync(ApplicationUser user, string? returnUrl = null)
    {
        var code = Encode(await userManager.GenerateEmailConfirmationTokenAsync(user));
        var url = PageUrl("/Account/ConfirmEmail", new { userId = user.Id, code, returnUrl });

        await sender.SendAsync(user.Email!, $"{serverOptions.Value.DisplayName}: E-Mail-Adresse bestätigen",
            $"<p>Bitte bestätige deine E-Mail-Adresse: <a href=\"{HtmlEncoder.Default.Encode(url)}\">Adresse bestätigen</a></p>");
    }

    public async Task SendPasswordResetAsync(ApplicationUser user)
    {
        var code = Encode(await userManager.GeneratePasswordResetTokenAsync(user));
        var url = PageUrl("/Account/ResetPassword", new { code });

        await sender.SendAsync(user.Email!, $"{serverOptions.Value.DisplayName}: Passwort zurücksetzen",
            $"<p>Über diesen Link kannst du ein neues Passwort setzen: <a href=\"{HtmlEncoder.Default.Encode(url)}\">Passwort zurücksetzen</a></p>" +
            "<p>Wenn du das nicht angefordert hast, kannst du diese Mail ignorieren.</p>");
    }

    public static string Encode(string token) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

    public static string? Decode(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private string PageUrl(string page, object values)
    {
        var context = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("Account mails can only be sent during a request.");

        return links.GetUriByPage(context, page, handler: null, values)
            ?? throw new InvalidOperationException($"No route for page {page}.");
    }
}
