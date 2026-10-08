using System.ComponentModel.DataAnnotations;
using Identity.Server.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Identity.Server.Pages.Account;

public sealed class LoginModel(
    SignInManager<IdentityUser> signInManager,
    IOptions<IdentitySeedOptions> seedOptions,
    IWebHostEnvironment environment) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty, Required]
    public string UserName { get; set; } = string.Empty;

    [BindProperty, Required]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    public bool RememberMe { get; set; }

    public string? ErrorMessage { get; private set; }

    public IReadOnlyList<SeedUser> DemoUsers =>
        environment.IsDevelopment() ? seedOptions.Value.Users : [];

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            ErrorMessage = "Bitte Benutzername und Passwort eingeben.";
            return Page();
        }

        var result = await signInManager.PasswordSignInAsync(UserName, Password, RememberMe, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/");
        }

        ErrorMessage = result.IsLockedOut
            ? "Das Konto ist vorübergehend gesperrt."
            : "Benutzername oder Passwort ist falsch.";
        return Page();
    }
}
