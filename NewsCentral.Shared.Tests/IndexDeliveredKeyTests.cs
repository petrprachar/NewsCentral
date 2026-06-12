using NewsCentral.Models.IndexFile;
using NewsCentral.Security;
using Xunit;

namespace NewsCentral.Shared.Tests;

/// <summary>
/// C1 (regression gate) + C2 (delivered-key round-trip) for the Phase 3a SigningPublicKey field.
/// </summary>
public sealed class IndexDeliveredKeyTests
{
    private static readonly EcdsaSignatureService Svc = new();

    private static TeamIndexFile MakeIndex() => new()
    {
        TeamFolderName = "cz-its",
        TeamName       = "CZ ITS",
        Version        = "1.0.0",
        IndexHash      = "deadbeef",
        PublishedAssignments =
        {
            new PublishedAssignmentIndex { AssignmentId = "a1", PresentationName = "Hello" }
        }
    };

    // ── C1 — regression: the added field does not perturb the canonical input ──

    [Fact]
    public void Sign_WithSigningPublicKeyNull_VerifiesValid()
    {
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();
        var index = MakeIndex();
        Assert.Null(index.SigningPublicKey);

        index.Signature = Svc.Sign(index, priv);

        Assert.Equal(VerifyResult.Valid, Svc.Verify(index, pub));
    }

    [Fact]
    public void Sign_ThenTamperContent_IsInvalid()
    {
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();
        var index = MakeIndex();
        index.Signature = Svc.Sign(index, priv);

        index.PublishedAssignments[0].PresentationName = "Tampered";

        Assert.Equal(VerifyResult.Invalid, Svc.Verify(index, pub));
    }

    [Fact]
    public void SigningPublicKey_IsExcludedFromSignedPayload()
    {
        // Signed with the key field null; setting it afterwards must NOT break verification,
        // proving the field is excluded from the canonical signed bytes.
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();
        var index = MakeIndex();
        index.Signature = Svc.Sign(index, priv);

        index.SigningPublicKey = "any-base64-value-here";

        Assert.Equal(VerifyResult.Valid, Svc.Verify(index, pub));
    }

    // ── C2 — delivered-key round-trip ────────────────────────────────────────

    [Fact]
    public void Verify_AgainstEmbeddedKey_IsValid()
    {
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();
        var index = MakeIndex();
        index.Signature = Svc.Sign(index, priv);
        index.SigningPublicKey = pub;

        Assert.Equal(VerifyResult.Valid, Svc.Verify(index, index.SigningPublicKey!));
    }

    [Fact]
    public void Verify_AfterReplacingOnlySigningPublicKey_IsInvalid()
    {
        var (priv, _) = SigningKeyTool.GenerateKeyPair();
        var (_, otherPub) = SigningKeyTool.GenerateKeyPair();
        var index = MakeIndex();
        index.Signature = Svc.Sign(index, priv);   // signed by the original key

        index.SigningPublicKey = otherPub;          // swap ONLY the delivered key

        Assert.Equal(VerifyResult.Invalid, Svc.Verify(index, index.SigningPublicKey!));
    }
}
