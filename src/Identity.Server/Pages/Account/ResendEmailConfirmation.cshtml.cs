using System.ComponentModel.DataAnnotations;
using Identity.Server.Data;
using Identity.Server.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Identity.Server.Pages.Account;

public sealed class ResendEmailConfirmationModel(UserManager<ApplicationUser> userManager, AccountEmails emails) : PageModel
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

        // The answer is the same whether the address exists or not, so nobody can probe for accounts.
        if (await userManager.FindByEmailAsync(Email) is { EmailConfirmed: false } user)
        {
            await emails.SendConfirmationAsync(user);
        }

        Sent = true;
        return Page();
    }
}
