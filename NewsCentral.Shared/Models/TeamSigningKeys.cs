namespace NewsCentral.Models;

/// <summary>
/// ECDSA P-256 signing key material for a team.
/// <para>
/// The full object (including <see cref="PrivateKey"/>) lives ONLY on the authoring
/// tier at <c>{teamFolderName}/team-signing.json</c> and must NEVER be written through
/// <c>IBlobDistributionService</c> or synced to client machines.
/// </para>
/// <para>
/// Trust assumption: team content authors are trusted, and their processes are granted
/// access to the team private key by design.
/// </para>
/// </summary>
public sealed class TeamSigningKeys
{
    /// <summary>Base64 PKCS#8 private key — authoring side only.</summary>
    public string? PrivateKey { get; set; }

    /// <summary>Base64 SubjectPublicKeyInfo — distributed to verifying components.</summary>
    public string? PublicKey { get; set; }

    /// <summary>
    /// Base64 SPKI of the previous key; optional; retained for the rotation window
    /// so content signed before a key rotation can still be verified.
    /// </summary>
    public string? PublicKeyPrevious { get; set; }
}
