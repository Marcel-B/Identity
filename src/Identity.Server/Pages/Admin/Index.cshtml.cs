using Identity.Server.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace Identity.Server.Pages.Admin;

public sealed class IndexModel(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IOpenIddictApplicationManager applicationManager,
    IOpenIddictScopeManager scopeManager) : PageModel
{
    public int Users { get; private set; }

    public int Roles { get; private set; }

    public long Clients { get; private set; }

    public long Scopes { get; private set; }

    public async Task OnGetAsync()
    {
        Users = await userManager.Users.CountAsync();
        Roles = await roleManager.Roles.CountAsync();
        Clients = await applicationManager.CountAsync();
        Scopes = await scopeManager.CountAsync();
    }
}
