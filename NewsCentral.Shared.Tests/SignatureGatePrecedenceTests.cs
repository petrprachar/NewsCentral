using NewsCentral.Models.IndexFile;
using NewsCentral.Security;
using Xunit;

namespace NewsCentral.Shared.Tests;

/// <summary>
/// C3 — SignatureGate.VerifyWithPrecedence matrix, including the anti-downgrade guarantees:
/// the delivered key is never used for a static team and never overrides a registry key.
/// </summary>
public sealed class SignatureGatePrecedenceTests
{
    private static readonly EcdsaSignatureService Svc = new();

    private static TeamIndexFile MakeIndex() => new()
    {
        TeamFolderName = "cz-its",
        TeamName       = "CZ ITS",
        IndexHash      = "abc123",
        PublishedAssignments =
        {
            new PublishedAssignmentIndex { AssignmentId = "a1", PresentationName = "Hello" }
        }
    };

    [Fact]
    public void RegistryKey_ValidSig_ForgedDeliveredKeyIgnored_Valid()
    {
        var (priv, regPub) = SigningKeyTool.GenerateKeyPair();
        var (_, forgedPub) = SigningKeyTool.GenerateKeyPair();
        var index = MakeIndex();
        index.Signature = Svc.Sign(index, priv);
        index.SigningPublicKey = forgedPub;   // attacker-supplied delivered key

        // isDynamicTeam true or false — registry key must win either way.
        Assert.Equal(VerifyResult.Valid,
            SignatureGate.VerifyWithPrecedence(index, new[] { regPub }, isDynamicTeam: false));
    }

    [Fact]
    public void RegistryKey_TamperedContent_Invalid()
    {
        var (priv, regPub) = SigningKeyTool.GenerateKeyPair();
        var index = MakeIndex();
        index.Signature = Svc.Sign(index, priv);
        index.PublishedAssignments[0].PresentationName = "Tampered";

        Assert.Equal(VerifyResult.Invalid,
            SignatureGate.VerifyWithPrecedence(index, new[] { regPub }, isDynamicTeam: false));
    }

    [Fact]
    public void NoRegistryKey_Dynamic_ValidDeliveredKey_Valid()
    {
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();
        var index = MakeIndex();
        index.Signature = Svc.Sign(index, priv);
        index.SigningPublicKey = pub;

        Assert.Equal(VerifyResult.Valid,
            SignatureGate.VerifyWithPrecedence(index, Array.Empty<string?>(), isDynamicTeam: true));
    }

    [Fact]
    public void NoRegistryKey_Dynamic_TamperedContent_Invalid()
    {
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();
        var index = MakeIndex();
        index.Signature = Svc.Sign(index, priv);
        index.SigningPublicKey = pub;
        index.PublishedAssignments[0].PresentationName = "Tampered";

        Assert.Equal(VerifyResult.Invalid,
            SignatureGate.VerifyWithPrecedence(index, Array.Empty<string?>(), isDynamicTeam: true));
    }

    [Fact]
    public void NoRegistryKey_Dynamic_SigningPublicKeyAbsent_Unsigned()
    {
        var (priv, _) = SigningKeyTool.GenerateKeyPair();
        var index = MakeIndex();
        index.Signature = Svc.Sign(index, priv);   // signed, but no delivered key present
        Assert.Null(index.SigningPublicKey);

        Assert.Equal(VerifyResult.Unsigned,
            SignatureGate.VerifyWithPrecedence(index, Array.Empty<string?>(), isDynamicTeam: true));
    }

    [Fact]
    public void NoRegistryKey_NotDynamic_DeliveredKeyPresent_Disabled()
    {
        // Anti-downgrade: a static team never uses the delivered key.
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();
        var index = MakeIndex();
        index.Signature = Svc.Sign(index, priv);
        index.SigningPublicKey = pub;

        Assert.Equal(VerifyResult.Disabled,
            SignatureGate.VerifyWithPrecedence(index, Array.Empty<string?>(), isDynamicTeam: false));
    }

    [Fact]
    public void RegistryKeyPresent_AndDynamic_RegistryWins_DeliveredIgnored()
    {
        // Anti-downgrade: even for a dynamic team, a present registry key wins and the
        // delivered key is ignored. Sign with the registry key; deliver a forged key.
        var (priv, regPub) = SigningKeyTool.GenerateKeyPair();
        var (_, forgedPub) = SigningKeyTool.GenerateKeyPair();
        var index = MakeIndex();
        index.Signature = Svc.Sign(index, priv);
        index.SigningPublicKey = forgedPub;

        Assert.Equal(VerifyResult.Valid,
            SignatureGate.VerifyWithPrecedence(index, new[] { regPub }, isDynamicTeam: true));
    }
}
