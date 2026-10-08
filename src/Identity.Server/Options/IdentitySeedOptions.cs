namespace Identity.Server.Options;

/// <summary>
/// Roles, users and OIDC clients that are created or updated on startup.
/// Bound from the "Seed" configuration section.
/// </summary>
public sealed class IdentitySeedOptions
{
    public const string SectionName = "Seed";

    public List<string> Roles { get; set; } = [];

    public List<SeedUser> Users { get; set; } = [];

    public List<SeedClient> Clients { get; set; } = [];
}

public sealed class SeedUser
{
    public required string UserName { get; set; }

    public string? Email { get; set; }

    public required string Password { get; set; }

    public List<string> Roles { get; set; } = [];
}

public sealed class SeedClient
{
    public required string ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public string? DisplayName { get; set; }

    public List<string> RedirectUris { get; set; } = [];

    public List<string> PostLogoutRedirectUris { get; set; } = [];

    /// <summary>Additional (API) scopes the client may request besides openid, profile, email, roles and offline_access.</summary>
    public List<string> Scopes { get; set; } = [];

    /// <summary>
    /// OAuth grant types: "authorization_code" (with refresh tokens) for BFFs that sign users in, "client_credentials"
    /// for services that call APIs on their own behalf, e.g. a remote BFF registering itself at its host BFF.
    /// </summary>
    public List<string> GrantTypes { get; set; } = [AuthorizationCode];

    public const string AuthorizationCode = "authorization_code";

    public const string ClientCredentials = "client_credentials";
}
