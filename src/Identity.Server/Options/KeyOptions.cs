namespace Identity.Server.Options;

/// <summary>Signing and encryption keys for tokens (section <c>Keys</c>).</summary>
public sealed class KeyOptions
{
    public const string SectionName = "Keys";

    /// <summary>
    /// Signing certificates. All of them are published for validation; OpenIddict signs with the
    /// newest one that is currently valid, so a new certificate can be added before the old one expires.
    /// </summary>
    public List<CertificateOptions> SigningCertificates { get; set; } = [];

    /// <summary>Encryption certificates for authorization codes, refresh tokens and the external login state.</summary>
    public List<CertificateOptions> EncryptionCertificates { get; set; } = [];

    /// <summary>Generate throw-away keys on every start. Only meant for automated tests.</summary>
    public bool UseEphemeralKeys { get; set; }
}

public sealed class CertificateOptions
{
    /// <summary>Path to a PKCS#12 (.pfx) file.</summary>
    public string Path { get; set; } = "";

    public string? Password { get; set; }
}
