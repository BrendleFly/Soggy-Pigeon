using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SoggyPigeon.Protocol;

namespace SoggyPigeon.Server;

/// <summary>
/// The server's TLS identity: a self-signed certificate persisted as PFX in
/// the data dir. Clients pin its SHA-256 (copied from the server console /
/// tls-pin.txt) via TlsPin. Losing this file forces every player to re-pin,
/// so back it up.
/// </summary>
public static class TlsSupport
{
    public const string PfxPassword = "soggy-pigeon-v1";   // key never leaves the box

    public static string PfxPath(string dataDir) => Path.Combine(dataDir, "server.pfx");
    public static string PinPath(string dataDir) => Path.Combine(dataDir, "tls-pin.txt");

    /// <summary>Load the persisted identity, creating it on first run.</summary>
    public static X509Certificate2 EnsureCertificate(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var pfx = PfxPath(dataDir);

        if (File.Exists(pfx))
            return new X509Certificate2(pfx, PfxPassword);

        var request = new CertificateRequest(
            "CN=Soggy Pigeon Server",
            RSA.Create(2048),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        var exported = created.Export(X509ContentType.Pkcs12, PfxPassword);
        File.WriteAllBytes(pfx, exported);
        var cert = new X509Certificate2(pfx, PfxPassword);

        File.WriteAllText(PinPath(dataDir), TlsPin.Sha256Hex(cert) + "\n");
        return cert;
    }

    public static string Sha256Hex(X509Certificate2 cert) => TlsPin.Sha256Hex(cert);
}
