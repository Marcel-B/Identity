using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace Identity.Server.Services;

/// <summary>
/// Resource indicators (RFC 8707, the <c>resource</c> parameter) as MCP clients send them: a resource is accepted when it
/// is listed under "Ressourcen / Audiences" of an API scope the client may request. That replaces OpenIddict's
/// static resource registration and its <c>rsrc:</c> client permissions, so a new MCP server only needs its URL added to
/// its scope in the admin pages. The access token's audiences still come from the scope (AuthorizationController).
/// </summary>
public sealed class ResourceIndicators(IOpenIddictApplicationManager applications, IOpenIddictScopeManager scopes) :
    IOpenIddictServerHandler<ValidateAuthorizationRequestContext>,
    IOpenIddictServerHandler<ValidateTokenRequestContext>
{
    public static OpenIddictServerHandlerDescriptor AuthorizationDescriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateAuthorizationRequestContext>()
            .UseScopedHandler<ResourceIndicators>()
            .SetOrder(int.MaxValue - 100_000)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    public static OpenIddictServerHandlerDescriptor TokenDescriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateTokenRequestContext>()
            .UseScopedHandler<ResourceIndicators>()
            .SetOrder(int.MaxValue - 100_000)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    public ValueTask HandleAsync(ValidateAuthorizationRequestContext context) =>
        ValidateAsync(context, context.ClientId, context.Request.GetResources());

    public ValueTask HandleAsync(ValidateTokenRequestContext context) =>
        ValidateAsync(context, context.ClientId, context.Request.GetResources());

    private async ValueTask ValidateAsync(BaseValidatingContext context, string? clientId, IEnumerable<string> requested)
    {
        var resources = requested.ToList();
        if (resources.Count == 0)
        {
            return;
        }

        var allowed = await AllowedResourcesAsync(clientId, context.CancellationToken);
        var unknown = resources.FirstOrDefault(resource => !allowed.Contains(resource));
        if (unknown is not null)
        {
            context.Reject(
                error: Errors.InvalidTarget,
                description: $"The resource '{unknown}' is not listed under a scope this client may request.");
        }
    }

    /// <summary>The resources of all scopes the client has a permission for.</summary>
    private async Task<HashSet<string>> AllowedResourcesAsync(string? clientId, CancellationToken cancellationToken)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(clientId) ||
            await applications.FindByClientIdAsync(clientId, cancellationToken) is not { } application)
        {
            return allowed;
        }

        await foreach (var scope in scopes.ListAsync(count: null, offset: null, cancellationToken))
        {
            var name = await scopes.GetNameAsync(scope, cancellationToken);
            if (name is null ||
                !await applications.HasPermissionAsync(application, Permissions.Prefixes.Scope + name, cancellationToken))
            {
                continue;
            }

            foreach (var resource in await scopes.GetResourcesAsync(scope, cancellationToken))
            {
                // Only absolute URIs can be resource indicators; names like "demo-api" stay audiences only.
                if (Uri.TryCreate(resource, UriKind.Absolute, out _))
                {
                    allowed.Add(resource);
                }
            }
        }

        return allowed;
    }
}
