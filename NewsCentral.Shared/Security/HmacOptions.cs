namespace NewsCentral.Security;

public class HmacOptions
{
    /// <summary>
    /// Base64-encoded 32-byte HMAC-SHA256 key shared across all components.
    /// Empty string disables HMAC verification (unsigned content passes through with a warning).
    /// Generate a key: [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    /// </summary>
    public string SecretKey { get; set; } = string.Empty;
}
