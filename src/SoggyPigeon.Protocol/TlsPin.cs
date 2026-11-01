using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SoggyPigeon.Protocol;

/// <summary>
/// Certificate pinning shared by the server (prints its pin) and the client
/// (verifies it): trust the pin, not a CA chain — the self-signed pattern
/// from the study pack. SHA-256 over the certificate DER.
/// </summary>
public static class TlsPin
{
    public static string Sha256Hex(X509Certificate2 cert)
        => Convert.ToHexString(SHA256.HashData(cert.Export(X509ContentType.Cert))).ToLowerInvariant();

    /// <summary>True when hex matches the cert's pin (spaces/colons tolerated).</summary>
    public static bool Matches(X509Certificate2 cert, string pin)
    {
        var clean = new string(pin.Where(char.IsLetterOrDigit).ToArray());
        return string.Equals(clean, Sha256Hex(cert), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Client-side SslStream validation: accept ONLY the pinned cert,
    /// regardless of chain errors (self-signed by design).</summary>
    public static RemoteCertificateValidationCallback Validator(string pin)
        => (_, cert, _, _) => cert is X509Certificate2 c && Matches(c, pin);
}
