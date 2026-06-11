using NewsCentral.Security;
using Xunit;

namespace NewsCentral.Shared.Tests;

public sealed class SignatureGateTests
{
    // Full matrix: VerifyResult × requireSignedIndex {false, true}
    // Valid    → never rejects
    // Invalid  → always rejects
    // Unsigned → rejects iff requireSigned
    // Disabled → rejects iff requireSigned

    [Theory]
    [InlineData(VerifyResult.Valid,    false, false)]
    [InlineData(VerifyResult.Valid,    true,  false)]
    [InlineData(VerifyResult.Invalid,  false, true)]
    [InlineData(VerifyResult.Invalid,  true,  true)]
    [InlineData(VerifyResult.Unsigned, false, false)]
    [InlineData(VerifyResult.Unsigned, true,  true)]
    [InlineData(VerifyResult.Disabled, false, false)]
    [InlineData(VerifyResult.Disabled, true,  true)]
    public void ShouldReject_Matrix(VerifyResult result, bool requireSigned, bool expectReject)
    {
        var rejected = SignatureGate.ShouldReject(result, requireSigned, out var reason);

        Assert.Equal(expectReject, rejected);
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public void Valid_ReasonContainsValid()
    {
        SignatureGate.ShouldReject(VerifyResult.Valid, true, out var reason);
        Assert.Contains("valid", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Invalid_ReasonContainsInvalid()
    {
        SignatureGate.ShouldReject(VerifyResult.Invalid, false, out var reason);
        Assert.Contains("invalid", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unsigned_AcceptedReason_WhenNotRequired()
    {
        SignatureGate.ShouldReject(VerifyResult.Unsigned, false, out var reason);
        Assert.Contains("accepted", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unsigned_RequiredReason_WhenRequired()
    {
        SignatureGate.ShouldReject(VerifyResult.Unsigned, true, out var reason);
        Assert.Contains("required", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Disabled_AcceptedReason_WhenNotRequired()
    {
        SignatureGate.ShouldReject(VerifyResult.Disabled, false, out var reason);
        Assert.Contains("accepted", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Disabled_RequiredReason_WhenRequired()
    {
        SignatureGate.ShouldReject(VerifyResult.Disabled, true, out var reason);
        Assert.Contains("required", reason, StringComparison.OrdinalIgnoreCase);
    }
}
