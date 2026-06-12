using NewsCentral.Models.IndexFile;

namespace NewsCentral.Security;

/// <summary>
/// Centralizes the accept/reject decision for ECDSA index verification results.
/// Default behavior (requireSignedIndex = false) mirrors the existing phased-rollout
/// pass-through: only Invalid is rejected. Set requireSignedIndex = true to go
/// fail-closed: Unsigned and Disabled are also rejected.
/// </summary>
public static class SignatureGate
{
    private static readonly EcdsaSignatureService Ecdsa = new();

    /// <summary>
    /// Verifies a team's <paramref name="index"/> with key precedence:
    /// <list type="number">
    ///   <item>A configured registry public key always WINS (dual-key rotation supported);
    ///         a delivered <see cref="TeamIndexFile.SigningPublicKey"/> is ignored when a
    ///         registry key is present — this is the anti-downgrade guarantee.</item>
    ///   <item>Otherwise, for a dynamic (Entra-resolved) team only, the delivered key is used:
    ///         present → verified against it; absent → <see cref="VerifyResult.Unsigned"/>.</item>
    ///   <item>Otherwise (static team, no registry key) → <see cref="VerifyResult.Disabled"/>,
    ///         preserving existing semantics. The delivered key is NEVER consulted for a static
    ///         team, so it cannot be used to downgrade a statically-trusted team.</item>
    /// </list>
    /// </summary>
    public static VerifyResult VerifyWithPrecedence(
        TeamIndexFile index, IReadOnlyList<string?> registryPublicKeys, bool isDynamicTeam)
    {
        if (registryPublicKeys is not null &&
            registryPublicKeys.Any(k => !string.IsNullOrWhiteSpace(k)))
        {
            // Registry key wins — delivered key never consulted (anti-downgrade).
            return Ecdsa.Verify(index, registryPublicKeys.ToArray());
        }

        if (isDynamicTeam)
        {
            return string.IsNullOrWhiteSpace(index.SigningPublicKey)
                ? VerifyResult.Unsigned
                : Ecdsa.Verify(index, index.SigningPublicKey);
        }

        // Static team with no registry key — delivered key is intentionally NOT consulted.
        return VerifyResult.Disabled;
    }

    /// <summary>
    /// Returns true when the team's content must be rejected.
    /// <paramref name="reason"/> is always set to a human-readable explanation suitable
    /// for logging in both accept and reject paths.
    /// </summary>
    public static bool ShouldReject(VerifyResult result, bool requireSignedIndex, out string reason)
    {
        switch (result)
        {
            case VerifyResult.Valid:
                reason = "signature valid";
                return false;

            case VerifyResult.Invalid:
                reason = "signature invalid";
                return true;

            case VerifyResult.Unsigned:
                if (requireSignedIndex)
                {
                    reason = "unsigned, signing required";
                    return true;
                }
                reason = "unsigned (accepted)";
                return false;

            case VerifyResult.Disabled:
                if (requireSignedIndex)
                {
                    reason = "no public key, signing required";
                    return true;
                }
                reason = "no public key configured (accepted)";
                return false;

            default:
                reason = $"unknown verification result {result}";
                return true;
        }
    }
}
