using Microsoft.Extensions.Configuration;

namespace NewsCentral.Configuration;

public static class SigningKeyConfigurationReader
{
    /// <summary>
    /// Returns the ECDSA public keys for <paramref name="teamFolderName"/> in
    /// priority order — current key first, previous key second — suitable for
    /// passing directly to <see cref="NewsCentral.Security.EcdsaSignatureService.Verify{T}"/>.
    /// Null/empty entries are omitted; an empty array is returned when neither key
    /// is configured, which causes Verify to return <c>Disabled</c>.
    /// </summary>
    /// <remarks>
    /// Registry source (NewsService):
    ///   HKLM\Software\[Company]\NewsCentral\NewsService\Signing\{teamFolderName}\PublicKey
    ///   HKLM\Software\[Company]\NewsCentral\NewsService\Signing\{teamFolderName}\PublicKeyPrevious
    /// Both are surfaced as configuration keys by <see cref="RegistryConfigurationProvider"/>
    /// via its recursive WalkKey traversal.
    /// </remarks>
    public static string?[] GetPublicKeys(IConfiguration config, string teamFolderName)
    {
        var current  = config[$"Signing:{teamFolderName}:PublicKey"];
        var previous = config[$"Signing:{teamFolderName}:PublicKeyPrevious"];

        return new[] { current, previous }
            .Where(k => !string.IsNullOrEmpty(k))
            .ToArray();
    }
}
