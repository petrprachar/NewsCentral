using System.Security.Cryptography;

namespace NewsCentral.Security;

/// <summary>
/// Static helpers for the key management UI and external tooling parity.
/// </summary>
public static class SigningKeyTool
{
    /// <summary>
    /// Generates a fresh ECDSA nistP256 key pair.
    /// Returns Base64 of the PKCS#8 private key and SubjectPublicKeyInfo.
    /// </summary>
    public static (string PrivateKeyBase64, string PublicKeyBase64) GenerateKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (
            Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey()),
            Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo())
        );
    }

    /// <summary>
    /// Derives the Base64 SubjectPublicKeyInfo from a private key.
    /// Accepts a PEM block (<c>-----BEGIN PRIVATE KEY-----</c> or
    /// <c>-----BEGIN EC PRIVATE KEY-----</c>) or a single-line Base64 PKCS#8 value.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when the input is malformed or is not a P-256 key.
    /// </exception>
    public static string DerivePublicKey(string privateKeyInput)
    {
        ArgumentNullException.ThrowIfNull(privateKeyInput);

        using var ecdsa = ECDsa.Create();
        try
        {
            if (privateKeyInput.TrimStart().StartsWith("-----", StringComparison.Ordinal))
                ecdsa.ImportFromPem(privateKeyInput);
            else
                ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyInput), out _);
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            throw new ArgumentException(
                "Unable to import the private key. Supply a PEM block " +
                "(-----BEGIN PRIVATE KEY----- or -----BEGIN EC PRIVATE KEY-----) " +
                "or a single-line Base64 PKCS#8 value.",
                nameof(privateKeyInput), ex);
        }

        if (ecdsa.KeySize != 256)
            throw new ArgumentException(
                $"Expected a P-256 key (256-bit) but got a {ecdsa.KeySize}-bit key. " +
                "Re-generate the key pair with SigningKeyTool.GenerateKeyPair().",
                nameof(privateKeyInput));

        return Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
    }

    /// <summary>
    /// Returns a truncated display string for read-only UI presentation of the private key.
    /// </summary>
    public static string Truncate(string? key, int head = 12, int tail = 6)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;
        if (key.Length <= head + tail) return key;
        return $"{key[..head]}…{key[^tail..]}";
    }
}
