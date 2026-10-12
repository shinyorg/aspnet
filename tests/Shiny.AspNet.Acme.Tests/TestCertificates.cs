using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Shiny.AspNet.Acme.Tests;

/// <summary>Self-signed server certificates "obtained elsewhere", for import and fallback tests.</summary>
static class TestCertificates
{
    /// <summary>RSA 2048, the first name as CN, every name as a SAN, server-auth EKU; key exportable.</summary>
    public static X509Certificate2 Create(params string[] dnsNames)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={dnsNames[0]}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in dnsNames)
            san.AddDnsName(name);

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));

        var now = DateTimeOffset.UtcNow;
        using var generated = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(30));

        // Round-tripped so the key is usable as a server credential (macOS, Windows) and exportable.
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }
}
