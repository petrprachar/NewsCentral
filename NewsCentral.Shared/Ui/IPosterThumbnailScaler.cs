namespace NewsCentral.Ui;

/// <summary>
/// UI-2.2: platform seam for producing a small JPEG thumbnail from a full-size source image.
/// Implementations never throw for a decode failure — they return null — so
/// <see cref="PosterThumbnailCache"/> can treat "can't decode" and "can't scale" identically.
/// </summary>
public interface IPosterThumbnailScaler
{
    /// <summary>
    /// Returns a JPEG-encoded thumbnail fitted inside <paramref name="maxWidth"/> ×
    /// <paramref name="maxHeight"/> (aspect preserved, never upscaled), or null if
    /// <paramref name="source"/> could not be decoded.
    /// </summary>
    Task<byte[]?> ScaleToJpegAsync(byte[] source, int maxWidth, int maxHeight, int quality, CancellationToken ct);
}
