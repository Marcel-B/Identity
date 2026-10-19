using System.Security.Cryptography.X509Certificates;
using Identity.Server.Options;

namespace Identity.Server.Services;

/// <summary>Picks the token keys for the OpenIddict server and client from <see cref="KeyOptions"/>.</summary>
public static class TokenKeys
{
    public static void Configure(
        KeyOptions options,
        IHostEnvironment environment,
        Action<X509Certificate2> addSigningCertificate,
        Action<X509Certificate2> addEncryptionCertificate,
        Action useDevelopmentCertificates,
        Action useEphemeralKeys)
    {
        if (options.SigningCertificates.Count > 0 || options.EncryptionCertificates.Count > 0)
        {
            if (options.SigningCertificates.Count == 0 || options.EncryptionCertificates.Count == 0)
            {
                throw new InvalidOperationException("Configure both Keys:SigningCertificates and Keys:EncryptionCertificates.");
            }

            options.SigningCertificates.ForEach(certificate => addSigningCertificate(Load(certificate)));
            options.EncryptionCertificates.ForEach(certificate => addEncryptionCertificate(Load(certificate)));
        }
        else if (options.UseEphemeralKeys)
        {
            useEphemeralKeys();
        }
        else if (environment.IsDevelopment())
        {
            // Self-signed certificates kept in the current user's certificate store, so tokens survive a restart.
            useDevelopmentCertificates();
        }
        else
        {
            throw new InvalidOperationException(
                "No token keys configured. Set Keys:SigningCertificates and Keys:EncryptionCertificates (see README).");
        }
    }

    private static X509Certificate2 Load(CertificateOptions certificate) =>
        X509CertificateLoader.LoadPkcs12FromFile(certificate.Path, certificate.Password,
            X509KeyStorageFlags.EphemeralKeySet);
}
