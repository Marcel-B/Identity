namespace Identity.Server.Options;

/// <summary>Sign-in with external accounts (section <c>ExternalProviders</c>). A provider is active once it has a client ID.</summary>
public sealed class ExternalProviderOptions
{
    public const string SectionName = "ExternalProviders";

    public ExternalProvider GitHub { get; set; } = new();

    public ExternalProvider Google { get; set; } = new();

    public ExternalProvider Microsoft { get; set; } = new();

    public IEnumerable<string> EnabledProviders
    {
        get
        {
            if (GitHub.IsEnabled) yield return nameof(GitHub);
            if (Google.IsEnabled) yield return nameof(Google);
            if (Microsoft.IsEnabled) yield return nameof(Microsoft);
        }
    }
}

public sealed class ExternalProvider
{
    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>Microsoft only: <c>common</c>, <c>organizations</c>, <c>consumers</c> or a tenant ID.</summary>
    public string? Tenant { get; set; }

    public bool IsEnabled => !string.IsNullOrWhiteSpace(ClientId);
}
