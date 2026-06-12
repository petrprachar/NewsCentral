namespace NewsCentral.Security;

/// <summary>
/// Marks a signable type that carries its own verification public key alongside the content
/// (the "key-with-content" model for dynamic teams). The carried key is excluded from the
/// canonical signing input using the exact same null-and-restore mechanism that excludes
/// <see cref="ISignable.Signature"/>, so the signature does not cover the key field.
/// </summary>
public interface IDeliveredKeyCarrier
{
    string? SigningPublicKey { get; set; }
}
