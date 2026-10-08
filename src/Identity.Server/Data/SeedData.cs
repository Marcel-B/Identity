using Identity.Server.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Identity.Server.Data;

/// <summary>
/// Creates the database and seeds roles, users, scopes and clients from configuration.
/// Existing clients are updated so changes in appsettings take effect on restart.
/// </summary>
public sealed class SeedData(IServiceProvider services, ILogger<SeedData> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var options = provider.GetRequiredService<IOptions<IdentitySeedOptions>>().Value;

        // A real deployment should use EF Core migrations instead.
        var db = provider.GetRequiredService<ApplicationDbContext>();
        await db.Database.EnsureCreatedAsync(cancellationToken);

        await SeedRolesAsync(provider.GetRequiredService<RoleManager<IdentityRole>>(), options);
        await SeedUsersAsync(provider.GetRequiredService<UserManager<IdentityUser>>(), options);
        await SeedScopesAsync(provider.GetRequiredService<IOpenIddictScopeManager>(), options, cancellationToken);
        await SeedClientsAsync(provider.GetRequiredService<IOpenIddictApplicationManager>(), options, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task SeedRolesAsync(RoleManager<IdentityRole> roleManager, IdentitySeedOptions options)
    {
        var roles = options.Roles.Concat(options.Users.SelectMany(u => u.Roles)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var role in roles)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(new IdentityRole(role));
            }
        }
    }

    private async Task SeedUsersAsync(UserManager<IdentityUser> userManager, IdentitySeedOptions options)
    {
        foreach (var seed in options.Users)
        {
            var user = await userManager.FindByNameAsync(seed.UserName);
            if (user is null)
            {
                user = new IdentityUser { UserName = seed.UserName, Email = seed.Email, EmailConfirmed = true };
                var result = await userManager.CreateAsync(user, seed.Password);
                if (!result.Succeeded)
                {
                    logger.LogError("Could not create user {User}: {Errors}", seed.UserName,
                        string.Join(", ", result.Errors.Select(e => e.Description)));
                    continue;
                }
            }

            var missing = seed.Roles.Except(await userManager.GetRolesAsync(user), StringComparer.OrdinalIgnoreCase).ToList();
            if (missing.Count > 0)
            {
                await userManager.AddToRolesAsync(user, missing);
            }
        }
    }

    private static async Task SeedScopesAsync(IOpenIddictScopeManager manager, IdentitySeedOptions options, CancellationToken ct)
    {
        foreach (var scope in options.Clients.SelectMany(c => c.Scopes).Distinct())
        {
            if (await manager.FindByNameAsync(scope, ct) is null)
            {
                await manager.CreateAsync(new OpenIddictScopeDescriptor { Name = scope, Resources = { scope } }, ct);
            }
        }
    }

    private static async Task SeedClientsAsync(IOpenIddictApplicationManager manager, IdentitySeedOptions options, CancellationToken ct)
    {
        foreach (var client in options.Clients)
        {
            var descriptor = new OpenIddictApplicationDescriptor
            {
                ClientId = client.ClientId,
                ClientSecret = client.ClientSecret,
                DisplayName = client.DisplayName ?? client.ClientId,
                ClientType = string.IsNullOrEmpty(client.ClientSecret) ? ClientTypes.Public : ClientTypes.Confidential,
                ApplicationType = ApplicationTypes.Web,
                ConsentType = ConsentTypes.Implicit,
                Permissions =
                {
                    Permissions.Endpoints.Authorization,
                    Permissions.Endpoints.Token,
                    Permissions.Endpoints.EndSession,
                    Permissions.GrantTypes.AuthorizationCode,
                    Permissions.GrantTypes.RefreshToken,
                    Permissions.ResponseTypes.Code,
                    Permissions.Scopes.Email,
                    Permissions.Scopes.Profile,
                    Permissions.Scopes.Roles,
                    Permissions.Prefixes.Scope + Scopes.OfflineAccess,
                },
                Requirements = { Requirements.Features.ProofKeyForCodeExchange },
            };

            foreach (var scope in client.Scopes)
            {
                descriptor.Permissions.Add(Permissions.Prefixes.Scope + scope);
            }

            foreach (var uri in client.RedirectUris)
            {
                descriptor.RedirectUris.Add(new Uri(uri));
            }

            foreach (var uri in client.PostLogoutRedirectUris)
            {
                descriptor.PostLogoutRedirectUris.Add(new Uri(uri));
            }

            var existing = await manager.FindByClientIdAsync(client.ClientId, ct);
            if (existing is null)
            {
                await manager.CreateAsync(descriptor, ct);
            }
            else
            {
                await manager.UpdateAsync(existing, descriptor, ct);
            }
        }
    }
}
