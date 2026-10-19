using System.ComponentModel.DataAnnotations;
using System.Text;
using Identity.Server.Data;
using Identity.Server.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using QRCoder;

namespace Identity.Server.Pages.Account.Manage;

public sealed class EnableAuthenticatorModel(
    UserManager<ApplicationUser> userManager,
    IOptions<ServerOptions> serverOptions) : PageModel
{
    public string SharedKey { get; private set; } = "";

    public string QrCodeSvg { get; private set; } = "";

    public IReadOnlyList<string>? RecoveryCodes { get; private set; }

    [BindProperty]
    [Required(ErrorMessage = "Bitte gib den Code aus der App ein.")]
    [StringLength(7, MinimumLength = 6, ErrorMessage = "Der Code hat 6 Ziffern.")]
    [Display(Name = "Code aus der App")]
    public string Code { get; set; } = "";

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        // While two-factor sign-in is off, start with a fresh key so an old, possibly leaked one cannot be confirmed.
        // Once it is on, the key in use stays valid, so opening this page does not lock out the current app.
        if (!await userManager.GetTwoFactorEnabledAsync(user) || await userManager.GetAuthenticatorKeyAsync(user) is null)
        {
            await userManager.ResetAuthenticatorKeyAsync(user);
        }

        await LoadKeyAsync(user);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        if (!ModelState.IsValid)
        {
            await LoadKeyAsync(user);
            return Page();
        }

        var code = Code.Replace(" ", string.Empty).Replace("-", string.Empty);
        var valid = await userManager.VerifyTwoFactorTokenAsync(
            user, userManager.Options.Tokens.AuthenticatorTokenProvider, code);

        if (!valid)
        {
            ModelState.AddModelError(nameof(Code), "Der Code ist ungültig. Stimmt die Uhrzeit auf dem Handy?");
            await LoadKeyAsync(user);
            return Page();
        }

        var wasEnabled = await userManager.GetTwoFactorEnabledAsync(user);
        await userManager.SetTwoFactorEnabledAsync(user, true);

        if (!wasEnabled || await userManager.CountRecoveryCodesAsync(user) == 0)
        {
            RecoveryCodes = [.. await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10) ?? []];
            return Page();
        }

        TempData["StatusMessage"] = "Die neue Authenticator-App ist eingerichtet.";
        return RedirectToPage("./TwoFactor");
    }

    private async Task LoadKeyAsync(ApplicationUser user)
    {
        var key = await userManager.GetAuthenticatorKeyAsync(user)
            ?? throw new InvalidOperationException("The authenticator key could not be loaded.");
        SharedKey = FormatKey(key);

        var issuer = Uri.EscapeDataString(serverOptions.Value.DisplayName);
        var account = Uri.EscapeDataString(user.Email ?? user.UserName ?? user.Id);
        var uri = $"otpauth://totp/{issuer}:{account}?secret={key}&issuer={issuer}&digits=6";

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.Q);
        QrCodeSvg = new SvgQRCode(data).GetGraphic(4);
    }

    private static string FormatKey(string key)
    {
        var result = new StringBuilder();
        for (var i = 0; i < key.Length; i += 4)
        {
            result.Append(key.AsSpan(i, Math.Min(4, key.Length - i))).Append(' ');
        }

        return result.ToString().TrimEnd().ToLowerInvariant();
    }
}
