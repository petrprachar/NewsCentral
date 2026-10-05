using NewsCentral.Configuration;
using Xunit;

namespace NewsCentral.Shared.Tests;

public sealed class EnvironmentInitializationValidatorTests
{
    // ── ValidateAdmin ────────────────────────────────────────────────────────

    [Fact]
    public void ValidateAdmin_EmptyUsername_ReportsRequired()
    {
        var errors = EnvironmentInitializationValidator.ValidateAdmin(
            "", "Display Name", null, "Passw0rd!", "Passw0rd!");

        Assert.Contains("Username is required.", errors);
    }

    [Fact]
    public void ValidateAdmin_UsernameContainingWhitespace_IsRejected()
    {
        var errors = EnvironmentInitializationValidator.ValidateAdmin(
            "fresh admin", "Display Name", null, "Passw0rd!", "Passw0rd!");

        Assert.Contains("Username must not contain whitespace.", errors);
    }

    [Fact]
    public void ValidateAdmin_EmptyDisplayName_ReportsRequired()
    {
        var errors = EnvironmentInitializationValidator.ValidateAdmin(
            "fresh-admin", "", null, "Passw0rd!", "Passw0rd!");

        Assert.Contains("Display name is required.", errors);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("admin@newscentral.local")]
    public void ValidateAdmin_NullEmptyOrValidUpn_ProducesNoUpnError(string? upn)
    {
        var errors = EnvironmentInitializationValidator.ValidateAdmin(
            "fresh-admin", "Display Name", upn, "Passw0rd!", "Passw0rd!");

        Assert.DoesNotContain(errors, e => e.Contains("UPN"));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("a@")]
    [InlineData("@b")]
    [InlineData("a@b@c")]
    public void ValidateAdmin_MalformedUpn_IsRejected(string upn)
    {
        var errors = EnvironmentInitializationValidator.ValidateAdmin(
            "fresh-admin", "Display Name", upn, "Passw0rd!", "Passw0rd!");

        Assert.Contains("UPN must contain exactly one \"@\" with text on both sides.", errors);
    }

    [Fact]
    public void ValidateAdmin_PasswordSevenChars_IsRejected()
    {
        var errors = EnvironmentInitializationValidator.ValidateAdmin(
            "fresh-admin", "Display Name", null, "Short1!", "Short1!");

        Assert.Contains("Password must be at least 8 characters.", errors);
    }

    [Fact]
    public void ValidateAdmin_PasswordEightChars_IsAccepted()
    {
        var errors = EnvironmentInitializationValidator.ValidateAdmin(
            "fresh-admin", "Display Name", null, "Passw0rd", "Passw0rd");

        Assert.DoesNotContain(errors, e => e.Contains("Password must be at least"));
    }

    [Fact]
    public void ValidateAdmin_MismatchedConfirmation_IsRejected()
    {
        var errors = EnvironmentInitializationValidator.ValidateAdmin(
            "fresh-admin", "Display Name", null, "Passw0rd!", "Different1!");

        Assert.Contains("Password and confirmation do not match.", errors);
    }

    [Fact]
    public void ValidateAdmin_TooShortAndMismatched_ReportsOnlyLengthError()
    {
        // A too-short password never ALSO reports as "does not match" — the mismatch check is
        // gated behind the length check passing first (see the implementation's comment).
        var errors = EnvironmentInitializationValidator.ValidateAdmin(
            "fresh-admin", "Display Name", null, "Short1!", "Different1!");

        Assert.Contains("Password must be at least 8 characters.", errors);
        Assert.DoesNotContain(errors, e => e.Contains("does not match"));
    }

    [Fact]
    public void ValidateAdmin_AllValid_ReturnsNoErrors()
    {
        var errors = EnvironmentInitializationValidator.ValidateAdmin(
            "fresh-admin", "Fresh Admin", "admin@newscentral.local", "Passw0rd!", "Passw0rd!");

        Assert.Empty(errors);
    }

    // ── ValidateTargetPath ───────────────────────────────────────────────────

    [Fact]
    public void ValidateTargetPath_Empty_ReportsRequired()
    {
        var errors = EnvironmentInitializationValidator.ValidateTargetPath("");

        Assert.Contains("Path is required.", errors);
    }

    [Fact]
    public void ValidateTargetPath_Relative_IsRejected()
    {
        var errors = EnvironmentInitializationValidator.ValidateTargetPath(@"NewsCentralFresh");

        Assert.Contains("Path must be a fully qualified (absolute) path.", errors);
    }

    [Fact]
    public void ValidateTargetPath_AbsoluteLocal_IsValid()
    {
        var errors = EnvironmentInitializationValidator.ValidateTargetPath(@"C:\Download\NewsCentralFresh");

        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateTargetPath_AbsoluteUnc_IsValid()
    {
        var errors = EnvironmentInitializationValidator.ValidateTargetPath(@"\\server\share\newscentral");

        Assert.Empty(errors);
    }
}
