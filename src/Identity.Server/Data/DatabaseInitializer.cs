using Identity.Server.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Identity.Server.Data;

/// <summary>Migrates the database and creates the seed data from <see cref="SeedOptions"/> before the server accepts requests.</summary>
public sealed class DatabaseInitializer(
    IServiceProvider services,
    IOptions<ServerOptions> serverOptions,
    IOptions<SeedOptions> seedOptions,
    ILogger<DatabaseInitializer> logger) : IHostedService
{
    public const string AdminRole = "admin";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;

        if (serverOptions.Value.MigrateDatabaseOnStartup)
        {
            var database = provider.GetRequiredService<ApplicationDbContext>().Database;
            await database.MigrateAsync(cancellationToken);

            if (database.IsSqlite())
            {
                // Lets readers continue while a write is in progress. Stored in the database file.
                await database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
            }
        }

        var seed = seedOptions.Value;
        await SeedRolesAsync(provider.GetRequiredService<RoleManager<IdentityRole>>(), seed);
        await SeedAdminAsync(provider.GetRequiredService<UserManager<ApplicationUser>>(), seed);
        await SeedScopesAsync(provider.GetRequiredService<IOpenIddictScopeManager>(), seed, cancellationToken);
        await SeedClientsAsync(provider.GetRequiredService<IOpenIddictApplicationManager>(), seed, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task SeedRolesAsync(RoleManager<IdentityRole> roles, SeedOptions seed)
    {
        foreach (var name in seed.Roles.Append(AdminRole).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!await roles.RoleExistsAsync(name))
            {
                EnsureSucceeded(await roles.CreateAsync(new IdentityRole(name)), $"role '{name}'");
            }
        }
    }

    private async Task SeedAdminAsync(UserManager<ApplicationUser> users, SeedOptions seed)
    {
        if (seed.Admin is not { Email.Length: > 0, Password.Length: > 0 } admin ||
            (await users.GetUsersInRoleAsync(AdminRole)).Count > 0)
        {
            return;
        }

        var user = await users.FindByEmailAsync(admin.Email);
        if (user is null)
        {
            user = new ApplicationUser { UserName = admin.Email, Email = admin.Email, EmailConfirmed = true };
            EnsureSucceeded(await users.CreateAsync(user, admin.Password), $"admin '{admin.Email}'");
        }

        EnsureSucceeded(await users.AddToRoleAsync(user, AdminRole), $"admin role for '{admin.Email}'");
        logger.LogInformation("Created administrator {Email}", admin.Email);
    }

    private static async Task SeedScopesAsync(IOpenIddictScopeManager scopes, SeedOptions seed, CancellationToken cancellationToken)
    {
        foreach (var scope in seed.Scopes)
        {
            if (await scopes.FindByNameAsync(scope.Name, cancellationToken) is not null)
            {
                continue;
            }

            var descriptor = new OpenIddictScopeDescriptor { Name = scope.Name, DisplayName = scope.DisplayName ?? scope.Name };
            descriptor.Resources.UnionWith(scope.Resources);
            await scopes.CreateAsync(descriptor, cancellationToken);
        }
    }

    private static async Task SeedClientsAsync(IOpenIddictApplicationManager applications, SeedOptions seed, CancellationToken cancellationToken)
    {
        foreach (var client in seed.Clients)
        {
            if (await applications.FindByClientIdAsync(client.ClientId, cancellationToken) is not null)
            {
                continue;
            }

            await applications.CreateAsync(ClientDescriptors.Create(
                client.ClientId, client.ClientSecret, client.DisplayName, client.ConsentType, client.GrantTypes,
                client.RedirectUris, client.PostLogoutRedirectUris, client.Scopes), cancellationToken);
        }
    }

    private static void EnsureSucceeded(IdentityResult result, string what)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not create {what}: {string.Join(" ", result.Errors.Select(error => error.Description))}");
        }
    }
}

/// <summary>Turns the client settings used by the seed and the admin UI into an OpenIddict descriptor.</summary>
public static class ClientDescriptors
{
    public static readonly string[] SupportedGrantTypes = [GrantTypes.AuthorizationCode, GrantTypes.RefreshToken, GrantTypes.ClientCredentials];

    public static OpenIddictApplicationDescriptor Create(
        string clientId,
        string? clientSecret,
        string? displayName,
        string consentType,
        IEnumerable<string> grantTypes,
        IEnumerable<string> redirectUris,
        IEnumerable<string> postLogoutRedirectUris,
        IEnumerable<string> scopes)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? clientId : displayName,
        };

        Apply(descriptor, clientSecret, consentType, grantTypes, redirectUris, postLogoutRedirectUris, scopes);
        return descriptor;
    }

    /// <summary>Replaces type, consent, permissions and URIs of <paramref name="descriptor"/>. A null secret keeps the current one.</summary>
    public static void Apply(
        OpenIddictApplicationDescriptor descriptor,
        string? clientSecret,
        string consentType,
        IEnumerable<string> grantTypes,
        IEnumerable<string> redirectUris,
        IEnumerable<string> postLogoutRedirectUris,
        IEnumerable<string> scopes)
    {
        var grants = grantTypes.Select(grant => grant.Trim()).Where(SupportedGrantTypes.Contains).ToHashSet(StringComparer.Ordinal);
        if (grants.Count == 0)
        {
            throw new ArgumentException("A client needs at least one supported grant type.", nameof(grantTypes));
        }

        if (!string.IsNullOrEmpty(clientSecret))
        {
            descriptor.ClientSecret = clientSecret;
        }

        descriptor.ClientType = string.IsNullOrEmpty(descriptor.ClientSecret) ? ClientTypes.Public : ClientTypes.Confidential;
        if (descriptor.ClientType == ClientTypes.Public && grants.Contains(GrantTypes.ClientCredentials))
        {
            throw new ArgumentException("Client credentials need a client secret.", nameof(grantTypes));
        }

        descriptor.ConsentType = consentType == ConsentTypes.Explicit ? ConsentTypes.Explicit : ConsentTypes.Implicit;

        descriptor.Permissions.Clear();
        descriptor.Permissions.Add(Permissions.Endpoints.Token);
        descriptor.Permissions.Add(Permissions.Endpoints.Revocation);
        descriptor.Permissions.Add(Permissions.Endpoints.Introspection);

        if (grants.Contains(GrantTypes.AuthorizationCode))
        {
            descriptor.Permissions.UnionWith(
            [
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.EndSession,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.ResponseTypes.Code,
                Permissions.Scopes.Profile,
                Permissions.Scopes.Email,
                Permissions.Scopes.Roles,
            ]);
            descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);
        }
        else
        {
            descriptor.Requirements.Remove(Requirements.Features.ProofKeyForCodeExchange);
        }

        if (grants.Contains(GrantTypes.RefreshToken))
        {
            descriptor.Permissions.Add(Permissions.GrantTypes.RefreshToken);
        }

        if (grants.Contains(GrantTypes.ClientCredentials))
        {
            descriptor.Permissions.Add(Permissions.GrantTypes.ClientCredentials);
        }

        foreach (var scope in scopes.Select(scope => scope.Trim()).Where(scope => scope.Length > 0))
        {
            descriptor.Permissions.Add(Permissions.Prefixes.Scope + scope);
        }

        descriptor.RedirectUris.Clear();
        descriptor.RedirectUris.UnionWith(ParseUris(redirectUris));
        descriptor.PostLogoutRedirectUris.Clear();
        descriptor.PostLogoutRedirectUris.UnionWith(ParseUris(postLogoutRedirectUris));
    }

    private static IEnumerable<Uri> ParseUris(IEnumerable<string> values) =>
        values.Select(value => value.Trim()).Where(value => value.Length > 0).Select(value =>
            Uri.TryCreate(value, UriKind.Absolute, out var uri)
                ? uri
                : throw new ArgumentException($"'{value}' is not an absolute URI."));
}
