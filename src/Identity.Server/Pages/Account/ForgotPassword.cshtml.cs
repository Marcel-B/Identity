using System.ComponentModel.DataAnnotations;
using Identity.Server.Data;
using Identity.Server.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Identity.Server.Pages.Account;

public sealed class ForgotPasswordModel(UserManager<ApplicationUser> userManager, AccountEmails emails) : PageModel
{
    [BindProperty]
    [Required(ErrorMessage = "Bitte gib deine E-Mail-Adresse ein.")]
    [EmailAddress(ErrorMessage = "Das ist keine gültige E-Mail-Adresse.")]
    [Display(Name = "E-Mail")]
    public string Email { get; set; } = "";

    public bool Sent { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        // Same answer for unknown addresses, so nobody can probe for accounts.
        if (await userManager.FindByEmailAsync(Email) is { } user && await userManager.IsEmailConfirmedAsync(user))
        {
            await emails.SendPasswordResetAsync(user);
        }

        Sent = true;
        return Page();
    }
}
