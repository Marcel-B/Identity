using System.ComponentModel.DataAnnotations;
using Identity.Server.Data;
using Identity.Server.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace Identity.Server.Pages.Admin.Users;

public sealed class EditModel(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IOpenIddictAuthorizationManager authorizationManager,
    IOpenIddictTokenManager tokenManager,
    AccountEmails emails) : PageModel
{
    public ApplicationUser? Account { get; private set; }

    public IReadOnlyList<string> AllRoles { get; private set; } = [];

    public IList<UserLoginInfo> Logins { get; private set; } = [];

    public int PasskeyCount { get; private set; }

    public bool HasPassword { get; private set; }

    public bool IsSelf { get; private set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib eine E-Mail-Adresse ein.")]
        [EmailAddress(ErrorMessage = "Das ist keine gültige E-Mail-Adresse.")]
        [Display(Name = "E-Mail")]
        public string Email { get; set; } = "";

        [StringLength(100)]
        [Display(Name = "Anzeigename")]
        public string? DisplayName { get; set; }

        [Display(Name = "E-Mail-Adresse bestätigt")]
        public bool EmailConfirmed { get; set; }

        public List<string> Roles { get; set; } = [];
    }

    public async Task<IActionResult> OnGetAsync(string id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        Input = new InputModel
        {
            Email = Account!.Email ?? "",
            DisplayName = Account.DisplayName,
            EmailConfirmed = Account.EmailConfirmed,
            Roles = [.. await userManager.GetRolesAsync(Account)],
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var user = Account!;
        var errors = new List<IdentityError>();

        var oldEmail = user.Email;
        if (!string.Equals(oldEmail, Input.Email, StringComparison.OrdinalIgnoreCase))
        {
            errors.AddRange((await userManager.SetEmailAsync(user, Input.Email)).Errors);

            // Accounts use the e-mail address as user name, so keep both in step.
            if (string.Equals(user.UserName, oldEmail, StringComparison.OrdinalIgnoreCase))
            {
                errors.AddRange((await userManager.SetUserNameAsync(user, Input.Email)).Errors);
            }
        }

        user.DisplayName = string.IsNullOrWhiteSpace(Input.DisplayName) ? null : Input.DisplayName.Trim();
        user.EmailConfirmed = Input.EmailConfirmed;
        errors.AddRange((await userManager.UpdateAsync(user)).Errors);

        var wanted = Input.Roles.Intersect(AllRoles).ToHashSet(StringComparer.Ordinal);
        if (IsSelf)
        {
            // Nobody can take away their own admin rights by accident.
            wanted.Add(DatabaseInitializer.AdminRole);
        }

        var current = await userManager.GetRolesAsync(user);
        errors.AddRange((await userManager.RemoveFromRolesAsync(user, current.Except(wanted))).Errors);
        errors.AddRange((await userManager.AddToRolesAsync(user, wanted.Except(current))).Errors);

        if (errors.Count > 0)
        {
            errors.ForEach(error => ModelState.AddModelError(string.Empty, error.Description));
            return Page();
        }

        TempData["StatusMessage"] = "Gespeichert. Neue Rollen gelten in Anwendungen ab der nächsten Token-Erneuerung.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostLockAsync(string id, bool locked)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        if (IsSelf)
        {
            TempData["ErrorMessage"] = "Du kannst dich nicht selbst sperren.";
            return RedirectToPage(new { id });
        }

        await userManager.SetLockoutEnabledAsync(Account!, true);
        await userManager.SetLockoutEndDateAsync(Account!, locked ? DateTimeOffset.MaxValue : null);
        if (locked)
        {
            await RevokeAsync(Account!);
        }

        TempData["StatusMessage"] = locked ? "Benutzer gesperrt und abgemeldet." : "Sperre aufgehoben.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostResetTwoFactorAsync(string id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        await userManager.SetTwoFactorEnabledAsync(Account!, false);
        await userManager.ResetAuthenticatorKeyAsync(Account!);
        TempData["StatusMessage"] = "Zwei-Faktor-Anmeldung zurückgesetzt.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostSendPasswordResetAsync(string id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        await emails.SendPasswordResetAsync(Account!);
        TempData["StatusMessage"] = $"Link zum Zurücksetzen an {Account!.Email} gesendet.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostSignOutEverywhereAsync(string id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        await RevokeAsync(Account!);
        TempData["StatusMessage"] = "Alle Anmeldungen bei Anwendungen wurden widerrufen.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(string id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        if (IsSelf)
        {
            TempData["ErrorMessage"] = "Du kannst dich nicht selbst löschen.";
            return RedirectToPage(new { id });
        }

        await RevokeAsync(Account!);
        await userManager.DeleteAsync(Account!);
        TempData["StatusMessage"] = $"{Account!.Email} gelöscht.";
        return RedirectToPage("./Index");
    }

    /// <summary>Invalidates the session cookie and every authorization and token issued to applications.</summary>
    private async Task RevokeAsync(ApplicationUser user)
    {
        await userManager.UpdateSecurityStampAsync(user);
        await authorizationManager.RevokeBySubjectAsync(user.Id);
        await tokenManager.RevokeBySubjectAsync(user.Id);
    }

    private async Task<bool> LoadAsync(string id)
    {
        Account = await userManager.FindByIdAsync(id);
        if (Account is null)
        {
            return false;
        }

        AllRoles = await roleManager.Roles.OrderBy(role => role.Name).Select(role => role.Name!).ToListAsync();
        Logins = await userManager.GetLoginsAsync(Account);
        PasskeyCount = (await userManager.GetPasskeysAsync(Account)).Count;
        HasPassword = await userManager.HasPasswordAsync(Account);
        IsSelf = userManager.GetUserId(User) == Account.Id;
        return true;
    }
}
