using NewsCentral.Security;
using System.Runtime.Intrinsics.Arm;
using System.Security.Cryptography;
using Xunit;

namespace NewsCentral.Shared.Tests;

public sealed class EcdsaRoundTripTests
{
    private static readonly EcdsaSignatureService Svc = new();

    private sealed class SampleEntity : ISignable
    {
        public string Name { get; set; } = "hello";
        public int Value { get; set; } = 42;
        public string? Signature { get; set; }
    }

    [Fact]
    public void GenerateAndSign_VerifyValid()
    {
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();
        var e = new SampleEntity();
        e.Signature = Svc.Sign(e, priv);

        Assert.Equal(VerifyResult.Valid, Svc.Verify(e, pub));
    }

    [Fact]
    public void Verify_InvalidWithWrongKey()
    {
        var (priv, _) = SigningKeyTool.GenerateKeyPair();
        var (_, wrongPub) = SigningKeyTool.GenerateKeyPair();
        var e = new SampleEntity();
        e.Signature = Svc.Sign(e, priv);

        Assert.Equal(VerifyResult.Invalid, Svc.Verify(e, wrongPub));
    }

    [Fact]
    public void Verify_UnsignedWhenSignatureNull()
    {
        var (_, pub) = SigningKeyTool.GenerateKeyPair();

        Assert.Equal(VerifyResult.Unsigned, Svc.Verify(new SampleEntity { Signature = null }, pub));
    }

    [Fact]
    public void Verify_DisabledWhenNoKeySupplied()
    {
        var e = new SampleEntity { Signature = "anything" };

        Assert.Equal(VerifyResult.Disabled, Svc.Verify(e));
        Assert.Equal(VerifyResult.Disabled, Svc.Verify(e, null, string.Empty));
    }

    [Fact]
    public void DerivePublicKey_MatchesGenerated()
    {
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();

        Assert.Equal(pub, SigningKeyTool.DerivePublicKey(priv));
    }

    [Fact]
    public void Verify_ValidWithSecondKeyInRotationList()
    {
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();
        var (_, wrongPub) = SigningKeyTool.GenerateKeyPair();
        var e = new SampleEntity();
        e.Signature = Svc.Sign(e, priv);

        // Correct key is second — simulates rotation window
        Assert.Equal(VerifyResult.Valid, Svc.Verify(e, wrongPub, pub));
    }

    [Fact]
    public void Verify_InvalidWhenPayloadTamperedAfterSigning()
    {
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();
        var e = new SampleEntity();
        e.Signature = Svc.Sign(e, priv);

        e.Value = 99;                       // mutate a covered field after signing
        Assert.Equal(VerifyResult.Invalid, Svc.Verify(e, pub));
    }

    [Fact]
    public void DerivePublicKey_AcceptsPemInput()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = ec.ExportPkcs8PrivateKeyPem();    // -----BEGIN PRIVATE KEY-----
        var expectedPub = Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo());

        Assert.Equal(expectedPub, SigningKeyTool.DerivePublicKey(pem));
    }

    [Fact]
    public void DerivePublicKey_ThrowsArgumentExceptionOnGarbage()
    {
        Assert.Throws<ArgumentException>(() => SigningKeyTool.DerivePublicKey("not-a-key"));
    }
}
