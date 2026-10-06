using NewsCentral.Ui;

namespace NewsCentral.Services;

#if WINDOWS
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

/// <summary>
/// UI-2.2: decodes and downsizes a poster image using the Windows.Graphics.Imaging WinRT APIs —
/// already available on this project's net9.0-windows10.0.19041.0 target without any additional
/// NuGet package. Never throws for a bad image; returns null, which PosterThumbnailCache treats
/// as "no thumbnail," never as a reason to fall back to the full-size image.
/// </summary>
public sealed class WindowsPosterThumbnailScaler : IPosterThumbnailScaler
{
    public async Task<byte[]?> ScaleToJpegAsync(byte[] source, int maxWidth, int maxHeight, int quality, CancellationToken ct)
    {
        try
        {
            using var inputStream = new InMemoryRandomAccessStream();
            await inputStream.WriteAsync(source.AsBuffer());
            inputStream.Seek(0);

            BitmapDecoder decoder;
            try
            {
                decoder = await BitmapDecoder.CreateAsync(inputStream);
            }
            catch (Exception)
            {
                return null; // undecodable image — not this scaler's job to diagnose further
            }

            ct.ThrowIfCancellationRequested();

            var (scaledWidth, scaledHeight) = Fit(decoder.PixelWidth, decoder.PixelHeight, maxWidth, maxHeight);

            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)scaledWidth,
                ScaledHeight = (uint)scaledHeight,
                InterpolationMode = BitmapInterpolationMode.Fant
            };

            var pixelProvider = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);

            var pixels = pixelProvider.DetachPixelData();

            using var outputStream = new InMemoryRandomAccessStream();
            var propertySet = new BitmapPropertySet
            {
                ["ImageQuality"] = new BitmapTypedValue(quality / 100.0, Windows.Foundation.PropertyType.Single)
            };

            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outputStream, propertySet);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                (uint)scaledWidth,
                (uint)scaledHeight,
                96, 96,
                pixels);

            await encoder.FlushAsync();

            outputStream.Seek(0);
            var buffer = new Windows.Storage.Streams.Buffer((uint)outputStream.Size);
            await outputStream.ReadAsync(buffer, (uint)outputStream.Size, InputStreamOptions.None);
            return buffer.ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static (int width, int height) Fit(uint sourceWidth, uint sourceHeight, int maxWidth, int maxHeight)
    {
        if (sourceWidth == 0 || sourceHeight == 0)
            return (1, 1);

        var scale = Math.Min(1.0, Math.Min((double)maxWidth / sourceWidth, (double)maxHeight / sourceHeight));
        var width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        var height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        return (width, height);
    }
}
#else
/// <summary>Non-Windows targets have no imaging implementation yet — always returns null.</summary>
public sealed class WindowsPosterThumbnailScaler : IPosterThumbnailScaler
{
    public Task<byte[]?> ScaleToJpegAsync(byte[] source, int maxWidth, int maxHeight, int quality, CancellationToken ct) =>
        Task.FromResult<byte[]?>(null);
}
#endif
