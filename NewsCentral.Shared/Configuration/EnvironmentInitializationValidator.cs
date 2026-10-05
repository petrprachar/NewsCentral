namespace NewsCentral.Configuration;

/// <summary>
/// Pure, no I/O — validation for the M5b environment setup wizard's two inputs: the first
/// administrator's credentials and the target path to initialize. Mirrors the shape of
/// <see cref="EnvironmentSettingsResolver.Validate"/> (a flat error-message list; empty means
/// valid) so the wizard can render both the same way the Environment Management editor already
/// does. Never touches disk — <see cref="EnvironmentInitializer"/> is the I/O-performing
/// counterpart.
/// </summary>
public static class EnvironmentInitializationValidator
{
    /// <summary>Matches BCrypt's own practical limit — not enforced here, this is a MINIMUM.</summary>
    public const int MinPasswordLength = 8;

    /// <summary>
    /// Validates the first administrator's fields. <paramref name="upn"/> is optional — null or
    /// empty is valid (the wizard still prefills it from <c>WindowsIdentityService.GetCurrentUserUPN()</c>,
    /// but a blank UPN is a legitimate choice, exactly like <see cref="Models.User.UPN"/> itself
    /// being nullable). When given, it must contain exactly one <c>"@"</c> with non-empty text on
    /// both sides — the same shape <c>AuthenticationService.LoginWithUpnAsync</c> later matches
    /// against, not full RFC 5321 validation. The confirmation mismatch is reported only once the
    /// password itself already passes the length check, so a too-short password never also reports
    /// as "does not match" when it happens to differ from the confirmation too.
    /// </summary>
    public static IReadOnlyList<string> ValidateAdmin(
        string? username, string? displayName, string? upn, string? password, string? confirmPassword)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(username))
            errors.Add("Username is required.");
        else if (username.Any(char.IsWhiteSpace))
            errors.Add("Username must not contain whitespace.");

        if (string.IsNullOrWhiteSpace(displayName))
            errors.Add("Display name is required.");

        if (!string.IsNullOrEmpty(upn))
        {
            var parts = upn.Split('@');
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
                errors.Add("UPN must contain exactly one \"@\" with text on both sides.");
        }

        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
            errors.Add($"Password must be at least {MinPasswordLength} characters.");
        else if (!string.Equals(password, confirmPassword, StringComparison.Ordinal))
            errors.Add("Password and confirmation do not match.");

        return errors;
    }

    /// <summary>
    /// Validates a candidate environment root to initialize. Only shape is checked here — it must
    /// be a fully-qualified (absolute, local or UNC) path; existence and initialization state are
    /// checked separately by <c>EnvironmentProbe</c>, which touches disk and this type deliberately
    /// does not.
    /// </summary>
    public static IReadOnlyList<string> ValidateTargetPath(string? path)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(path))
            errors.Add("Path is required.");
        else if (!EnvironmentPaths.IsAbsolute(path))
            errors.Add("Path must be a fully qualified (absolute) path.");

        return errors;
    }
}
