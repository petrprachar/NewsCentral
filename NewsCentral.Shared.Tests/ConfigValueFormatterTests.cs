using NewsCentral.Configuration;

namespace NewsCentral.Shared.Tests;

public sealed class ConfigValueFormatterTests
{
    private static ConfigKeyDescriptor Desc(bool secret) => new()
    {
        CanonicalKey = "X",
        DisplayName = "X",
        Default = "",
        RegistryType = RegistryValueType.RegSz,
        ControlKind = secret ? ControlKind.Redacted : ControlKind.Text,
        IsSecret = secret,
        OverridableState = OverridableState.Overridable,
        ValueHint = ""
    };

    [Fact]
    public void NullValue_RendersNotSet() =>
        Assert.Equal("(not set)", ConfigValueFormatter.ForDisplay(Desc(secret: false), null));

    [Fact]
    public void EmptyValue_RendersEmpty() =>
        Assert.Equal("(empty)", ConfigValueFormatter.ForDisplay(Desc(secret: false), ""));

    [Fact]
    public void SecretWithValue_IsRedacted() =>
        Assert.Equal("••• (set)", ConfigValueFormatter.ForDisplay(Desc(secret: true), "super-secret"));

    [Fact]
    public void SecretButNull_StillNotSet() =>
        Assert.Equal("(not set)", ConfigValueFormatter.ForDisplay(Desc(secret: true), null));

    [Fact]
    public void PublicKey_NotSecret_ShownInFull()
    {
        // A Signing public key is IsSecret = false → shown, not redacted (spec §5.1/§6).
        const string spki = "MFkwEwYHKoZIzj0CAQ...base64-public-key";
        Assert.Equal(spki, ConfigValueFormatter.ForDisplay(Desc(secret: false), spki));
    }

    [Fact]
    public void PlainValue_ShownVerbatim() =>
        Assert.Equal("Share", ConfigValueFormatter.ForDisplay(Desc(secret: false), "Share"));
}
