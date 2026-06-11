namespace NewsCentral.Security;

/// <summary>
/// Centralizes the accept/reject decision for ECDSA index verification results.
/// Default behavior (requireSignedIndex = false) mirrors the existing phased-rollout
/// pass-through: only Invalid is rejected. Set requireSignedIndex = true to go
/// fail-closed: Unsigned and Disabled are also rejected.
/// </summary>
public static class SignatureGate
{
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
