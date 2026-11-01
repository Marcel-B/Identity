using System.ComponentModel.DataAnnotations;
using Identity.Server.Data;
using Identity.Server.Monitoring;
using Identity.Server.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Identity.Server.Pages.Account;

public sealed class LoginModel(
    SignInManager<ApplicationUser> signInManager,
    UserManager<ApplicationUser> userManager,
    IOptions<ServerOptions> serverOptions,
    IOptions<ExternalProviderOptions> providerOptions,
    ILogger<LoginModel> logger) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public string? PasskeyCredential { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public IReadOnlyList<string> ExternalProviders { get; private set; } = [];

    public bool AllowRegistration => serverOptions.Value.AllowRegistration;

    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib deinen Benutzernamen oder deine E-Mail-Adresse ein.")]
        [Display(Name = "E-Mail oder Benutzername")]
        public string Login { get; set; } = "";

        [Required(ErrorMessage = "Bitte gib dein Passwort ein.")]
        [DataType(DataType.Password)]
        [Display(Name = "Passwort")]
        public string Password { get; set; } = "";

        [Display(Name = "Angemeldet bleiben")]
        public bool RememberMe { get; set; }
    }

    public void OnGet() => Load();

    public async Task<IActionResult> OnPostAsync()
    {
        Load();
        ModelState.Remove(nameof(PasskeyCredential));
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var user = await userManager.FindByNameAsync(Input.Login) ?? await userManager.FindByEmailAsync(Input.Login);
        if (user is null)
        {
            IdentityMetrics.SignIn(IdentityMetrics.Methods.Password, Microsoft.AspNetCore.Identity.SignInResult.Failed);
            ModelState.AddModelError(string.Empty, "Benutzername oder Passwort ist falsch.");
            return Page();
        }

        var wasLockedOut = await userManager.IsLockedOutAsync(user);
        var result = await signInManager.PasswordSignInAsync(user, Input.Password, Input.RememberMe, lockoutOnFailure: true);

        // Identity refuses unconfirmed accounts before it checks the password. Only tell people with the
        // right password why, so the hint does not reveal which addresses have an account.
        if (result.IsNotAllowed && !await userManager.CheckPasswordAsync(user, Input.Password))
        {
            result = Microsoft.AspNetCore.Identity.SignInResult.Failed;
        }

        IdentityMetrics.SignIn(IdentityMetrics.Methods.Password, result, wasLockedOut);
        return await HandleResultAsync(result, user);
    }

    public async Task<IActionResult> OnPostPasskeyAsync()
    {
        Load();
        if (string.IsNullOrEmpty(PasskeyCredential))
        {
            ModelState.AddModelError(string.Empty, "Die Anmeldung mit Passkey wurde abgebrochen.");
            return Page();
        }

        var result = await signInManager.PasskeySignInAsync(PasskeyCredential);
        IdentityMetrics.SignIn(IdentityMetrics.Methods.Passkey, result);
        return await HandleResultAsync(result, user: null);
    }

    private async Task<IActionResult> HandleResultAsync(Microsoft.AspNetCore.Identity.SignInResult result, ApplicationUser? user)
    {
        var returnUrl = SafeReturnUrl();

        if (result.Succeeded)
        {
            return LocalRedirect(returnUrl);
        }

        if (result.RequiresTwoFactor)
        {
            return RedirectToPage("./LoginWith2fa", new { returnUrl, rememberMe = Input.RememberMe });
        }

        if (result.IsLockedOut)
        {
            logger.LogWarning("Account {UserId} locked out.", user?.Id);
            return RedirectToPage("./Lockout");
        }

        if (result.IsNotAllowed && user is not null && !await userManager.IsEmailConfirmedAsync(user))
        {
            ModelState.AddModelError(string.Empty, "Bitte bestätige zuerst deine E-Mail-Adresse.");
            return Page();
        }

        ModelState.AddModelError(string.Empty, user is null
            ? "Die Anmeldung mit Passkey ist fehlgeschlagen."
            : "Benutzername oder Passwort ist falsch.");
        return Page();
    }

    private void Load()
    {
        ExternalProviders = [.. providerOptions.Value.EnabledProviders];
    }

    private string SafeReturnUrl() => ReturnUrl is not null && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/";
}
