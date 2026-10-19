using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Identity.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Identity.Server.Pages.Admin.Clients;

public sealed class EditModel(
    IOpenIddictApplicationManager applicationManager,
    IOpenIddictScopeManager scopeManager,
    IOpenIddictAuthorizationManager authorizationManager,
    IOpenIddictTokenManager tokenManager) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public IReadOnlyList<string> AvailableScopes { get; private set; } = [];

    public bool IsNew => Id is null;

    public bool IsConfidential { get; private set; }

    /// <summary>Shown once after it was generated; only its hash is stored.</summary>
    [TempData]
    public string? NewSecret { get; set; }

    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib eine Client-ID ein.")]
        [RegularExpression("^[A-Za-z0-9][A-Za-z0-9._-]*$", ErrorMessage = "Keine Leerzeichen oder Sonderzeichen.")]
        [Display(Name = "Client-ID")]
        public string ClientId { get; set; } = "";

        [Display(Name = "Anzeigename")]
        public string? DisplayName { get; set; }

        [Display(Name = "Vertraulicher Client mit Secret (Server-Anwendung, BFF, Dienst)")]
        public bool Confidential { get; set; } = true;

        [Display(Name = "Zustimmung")]
        public string ConsentType { get; set; } = ConsentTypes.Implicit;

        public List<string> GrantTypes { get; set; } = [OpenIddictConstants.GrantTypes.AuthorizationCode, OpenIddictConstants.GrantTypes.RefreshToken];

        [Display(Name = "Redirect-URIs (eine pro Zeile)")]
        public string? RedirectUris { get; set; }

        [Display(Name = "Post-Logout-Redirect-URIs (eine pro Zeile)")]
        public string? PostLogoutRedirectUris { get; set; }

        public List<string> Scopes { get; set; } = [];
    }

    public async Task<IActionResult> OnGetAsync()
    {
        AvailableScopes = await LoadScopesAsync();
        if (Id is null)
        {
            return Page();
        }

        if (await applicationManager.FindByIdAsync(Id) is not { } application)
        {
            return NotFound();
        }

        var descriptor = new OpenIddictApplicationDescriptor();
        await applicationManager.PopulateAsync(descriptor, application);

        IsConfidential = descriptor.ClientType == ClientTypes.Confidential;
        Input = new InputModel
        {
            ClientId = descriptor.ClientId!,
            DisplayName = descriptor.DisplayName,
            Confidential = IsConfidential,
            ConsentType = descriptor.ConsentType ?? ConsentTypes.Implicit,
            GrantTypes = [.. descriptor.Permissions
                .Where(permission => permission.StartsWith(Permissions.Prefixes.GrantType, StringComparison.Ordinal))
                .Select(permission => permission[Permissions.Prefixes.GrantType.Length..])],
            RedirectUris = string.Join('\n', descriptor.RedirectUris),
            PostLogoutRedirectUris = string.Join('\n', descriptor.PostLogoutRedirectUris),
            Scopes = [.. descriptor.Permissions
                .Where(permission => permission.StartsWith(Permissions.Prefixes.Scope, StringComparison.Ordinal))
                .Select(permission => permission[Permissions.Prefixes.Scope.Length..])
                .Where(AvailableScopes.Contains)],
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        AvailableScopes = await LoadScopesAsync();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var application = Id is null ? null : await applicationManager.FindByIdAsync(Id);
        if (Id is not null && application is null)
        {
            return NotFound();
        }

        var descriptor = new OpenIddictApplicationDescriptor();
        string? secret = null;

        if (application is null)
        {
            if (await applicationManager.FindByClientIdAsync(Input.ClientId) is not null)
            {
                ModelState.AddModelError("Input.ClientId", "Diese Client-ID gibt es schon.");
                return Page();
            }

            descriptor.ClientId = Input.ClientId;
            secret = Input.Confidential ? GenerateSecret() : null;
        }
        else
        {
            await applicationManager.PopulateAsync(descriptor, application);
        }

        IsConfidential = application is null ? Input.Confidential : descriptor.ClientType == ClientTypes.Confidential;
        descriptor.DisplayName = string.IsNullOrWhiteSpace(Input.DisplayName) ? descriptor.ClientId : Input.DisplayName.Trim();

        try
        {
            ClientDescriptors.Apply(descriptor, secret, Input.ConsentType, Input.GrantTypes,
                Scopes.EditModel.Lines(Input.RedirectUris), Scopes.EditModel.Lines(Input.PostLogoutRedirectUris),
                Input.Scopes.Intersect(AvailableScopes));

            if (application is null)
            {
                application = await applicationManager.CreateAsync(descriptor);
            }
            else
            {
                await applicationManager.UpdateAsync(application, descriptor);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or OpenIddictExceptions.ValidationException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return Page();
        }

        NewSecret = secret;
        TempData["StatusMessage"] = $"Client {descriptor.ClientId} gespeichert.";
        return RedirectToPage(new { id = await applicationManager.GetIdAsync(application) });
    }

    public async Task<IActionResult> OnPostNewSecretAsync()
    {
        if (Id is null || await applicationManager.FindByIdAsync(Id) is not { } application ||
            !await applicationManager.HasClientTypeAsync(application, ClientTypes.Confidential))
        {
            return NotFound();
        }

        var secret = GenerateSecret();
        var descriptor = new OpenIddictApplicationDescriptor();
        await applicationManager.PopulateAsync(descriptor, application);
        descriptor.ClientSecret = secret;
        await applicationManager.UpdateAsync(application, descriptor);

        NewSecret = secret;
        TempData["StatusMessage"] = "Neues Secret erzeugt. Das alte gilt ab sofort nicht mehr.";
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (Id is not null && await applicationManager.FindByIdAsync(Id) is { } application)
        {
            await tokenManager.RevokeByApplicationIdAsync(Id);
            await authorizationManager.RevokeByApplicationIdAsync(Id);
            await applicationManager.DeleteAsync(application);
            TempData["StatusMessage"] = "Client gelöscht.";
        }

        return RedirectToPage("./Index");
    }

    private async Task<IReadOnlyList<string>> LoadScopesAsync()
    {
        var scopes = new List<string>();
        await foreach (var scope in scopeManager.ListAsync())
        {
            scopes.Add((await scopeManager.GetNameAsync(scope))!);
        }

        return [.. scopes.Order(StringComparer.Ordinal)];
    }

    private static string GenerateSecret() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
}
