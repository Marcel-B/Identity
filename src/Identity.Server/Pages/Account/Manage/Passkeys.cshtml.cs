using System.ComponentModel.DataAnnotations;
using Identity.Server.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;

namespace Identity.Server.Pages.Account.Manage;

public sealed class PasskeysModel(UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager) : PageModel
{
    public const int MaxPasskeys = 20;

    public IList<UserPasskeyInfo> Passkeys { get; private set; } = [];

    [BindProperty]
    public string? Credential { get; set; }

    [BindProperty]
    [StringLength(100)]
    [Display(Name = "Name")]
    public string? Name { get; set; }

    public static string IdOf(UserPasskeyInfo passkey) => WebEncoders.Base64UrlEncode(passkey.CredentialId);

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        Passkeys = await userManager.GetPasskeysAsync(user);
        return Page();
    }

    public async Task<IActionResult> OnPostAddAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        if (string.IsNullOrEmpty(Credential))
        {
            TempData["ErrorMessage"] = "Der Passkey wurde nicht angelegt.";
            return RedirectToPage();
        }

        if ((await userManager.GetPasskeysAsync(user)).Count >= MaxPasskeys)
        {
            TempData["ErrorMessage"] = $"Mehr als {MaxPasskeys} Passkeys sind nicht möglich.";
            return RedirectToPage();
        }

        var attestation = await signInManager.PerformPasskeyAttestationAsync(Credential);
        if (!attestation.Succeeded)
        {
            TempData["ErrorMessage"] = $"Der Passkey konnte nicht geprüft werden: {attestation.Failure.Message}";
            return RedirectToPage();
        }

        var passkey = attestation.Passkey;
        passkey.Name = string.IsNullOrWhiteSpace(Name) ? $"Passkey vom {DateTime.Now:dd.MM.yyyy}" : Name.Trim();

        var result = await userManager.AddOrUpdatePasskeyAsync(user, passkey);
        TempData[result.Succeeded ? "StatusMessage" : "ErrorMessage"] = result.Succeeded
            ? "Passkey hinzugefügt. Du kannst dich jetzt ohne Passwort anmelden."
            : "Der Passkey konnte nicht gespeichert werden.";

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveAsync(string id)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        byte[] credentialId;
        try
        {
            credentialId = WebEncoders.Base64UrlDecode(id);
        }
        catch (FormatException)
        {
            return BadRequest();
        }

        var result = await userManager.RemovePasskeyAsync(user, credentialId);
        TempData[result.Succeeded ? "StatusMessage" : "ErrorMessage"] = result.Succeeded
            ? "Passkey entfernt."
            : "Der Passkey konnte nicht entfernt werden.";

        return RedirectToPage();
    }
}
