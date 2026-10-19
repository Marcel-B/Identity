namespace Identity.Server.Options;

/// <summary>
/// Data created on start when it does not exist yet (section <c>Seed</c>).
/// Existing entries are never overwritten, so changes made in the admin UI survive a restart.
/// </summary>
public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    /// <summary>First administrator; only created while nobody has the admin role.</summary>
    public SeedAdminOptions? Admin { get; set; }

    public List<string> Roles { get; set; } = [];

    public List<SeedScopeOptions> Scopes { get; set; } = [];

    public List<SeedClientOptions> Clients { get; set; } = [];
}

public sealed class SeedAdminOptions
{
    public string Email { get; set; } = "";

    public string Password { get; set; } = "";
}

public sealed class SeedScopeOptions
{
    public string Name { get; set; } = "";

    public string? DisplayName { get; set; }

    /// <summary>Audiences (<c>aud</c>) added to access tokens that carry this scope.</summary>
    public List<string> Resources { get; set; } = [];
}

public sealed class SeedClientOptions
{
    public string ClientId { get; set; } = "";

    /// <summary>Empty means public client (e.g. a SPA or native app using PKCE).</summary>
    public string? ClientSecret { get; set; }

    public string? DisplayName { get; set; }

    /// <summary><c>implicit</c> (no consent screen, for own apps) or <c>explicit</c>.</summary>
    public string ConsentType { get; set; } = "implicit";

    /// <summary>Any of <c>authorization_code</c>, <c>refresh_token</c>, <c>client_credentials</c>.</summary>
    public List<string> GrantTypes { get; set; } = ["authorization_code", "refresh_token"];

    public List<string> RedirectUris { get; set; } = [];

    public List<string> PostLogoutRedirectUris { get; set; } = [];

    /// <summary>Scopes the client may request in addition to the OpenID Connect ones.</summary>
    public List<string> Scopes { get; set; } = [];
}
