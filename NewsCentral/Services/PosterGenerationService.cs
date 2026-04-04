using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.Fonts;
using NewsCentral.Configuration;
using SixLaborsImage = SixLabors.ImageSharp.Image;
using SixLaborsSize = SixLabors.ImageSharp.Size;
using SixLaborsColor = SixLabors.ImageSharp.Color;
using SixLaborsPoint = SixLabors.ImageSharp.Point;
using SixLaborsPointF = SixLabors.ImageSharp.PointF;
using SixLaborsRectangle = SixLabors.ImageSharp.Rectangle;
using SixLaborsHorizontalAlignment = SixLabors.Fonts.HorizontalAlignment;
using SixLaborsVerticalAlignment = SixLabors.Fonts.VerticalAlignment;
using SixLaborsResizeMode = SixLabors.ImageSharp.Processing.ResizeMode;

namespace NewsCentral.Services;

public class PosterGenerationService
{
    private readonly string _basePath;

    public PosterGenerationService(AppConfiguration config)
    {
        _basePath = config.DataPath;
    }

    public async Task<string> GeneratePosterAsync(
        string teamFolderName,
        string presentationId,
        string version,
        byte[] originalImageData,
        string headlineText,
        string bodyText,
        string ctaText = "Learn More",
        PosterLayout layout = PosterLayout.Standard)
    {
        using var image = SixLaborsImage.Load<Rgba32>(originalImageData);

        // Resize to standard poster size (1920x1080)
        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new SixLaborsSize(1920, 1080),
            Mode = SixLaborsResizeMode.Crop
        }));

        // Apply dark overlay for text readability
        image.Mutate(x => x.Fill(
            new SixLaborsColor(new Rgba32(0, 0, 0, 180)), // Semi-transparent black
            new Rectangle(0, 800, 1920, 280)
        ));

        // Try preferred fonts first
        var fontFamilyEnumerable = SystemFonts.Families.Where(f =>
            f.Name.Contains("Arial", StringComparison.OrdinalIgnoreCase) ||
            f.Name.Contains("Segoe", StringComparison.OrdinalIgnoreCase) ||
            f.Name.Contains("Helvetica", StringComparison.OrdinalIgnoreCase));

        FontFamily fontFamily;
        if (fontFamilyEnumerable.Any())
        {
            fontFamily = fontFamilyEnumerable.First();
        }
        else if (SystemFonts.Families.Any())
        {
            fontFamily = SystemFonts.Families.First();
        }
        else
        {
            // Last resort - try to get Arial explicitly
            try
            {
                fontFamily = SystemFonts.Get("Arial");
            }
            catch
            {
                throw new InvalidOperationException("No system fonts available for poster generation");
            }
        }

        var headlineFont = fontFamily.CreateFont(72, FontStyle.Bold);
        var bodyFont = fontFamily.CreateFont(36, FontStyle.Regular);
        var ctaFont = fontFamily.CreateFont(32, FontStyle.Bold);

        // Draw headline
        var headlineOptions = new RichTextOptions(headlineFont)
        {
            Origin = new SixLaborsPointF(60, 820),
            WrappingLength = 1800,
            HorizontalAlignment = SixLaborsHorizontalAlignment.Left,
            VerticalAlignment = SixLaborsVerticalAlignment.Top
        };

        image.Mutate(x => x.DrawText(
            headlineOptions,
            headlineText,
            SixLaborsColor.White
        ));

        // Draw body text
        var bodyOptions = new RichTextOptions(bodyFont)
        {
            Origin = new SixLaborsPointF(60, 920),
            WrappingLength = 1800,
            HorizontalAlignment = SixLaborsHorizontalAlignment.Left,
            VerticalAlignment = SixLaborsVerticalAlignment.Top
        };

        image.Mutate(x => x.DrawText(
            bodyOptions,
            bodyText,
            SixLaborsColor.White
        ));

        // Draw CTA if provided
        if (!string.IsNullOrWhiteSpace(ctaText))
        {
            var ctaOptions = new RichTextOptions(ctaFont)
            {
                Origin = new SixLaborsPointF(60, 1010),
                HorizontalAlignment = SixLaborsHorizontalAlignment.Left,
                VerticalAlignment = SixLaborsVerticalAlignment.Top
            };

            image.Mutate(x => x.DrawText(
                ctaOptions,
                $"→ {ctaText}",
                new SixLaborsColor(new Rgba32(102, 126, 234)) // Brand color
            ));
        }

        // Save generated poster
        var posterFileName = $"poster_{presentationId}_v{version}.jpg";
        var posterPath = Path.Combine(_basePath, teamFolderName, "images", "generated", posterFileName);

        Directory.CreateDirectory(Path.GetDirectoryName(posterPath)!);

        await image.SaveAsJpegAsync(posterPath);

        return $"{teamFolderName}/images/generated/{posterFileName}";
    }

    public async Task<byte[]> GetPosterImageDataAsync(string teamFolderName, string posterPath)
    {
        var fullPath = Path.Combine(_basePath, posterPath);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Poster not found: {posterPath}");
        }

        return await File.ReadAllBytesAsync(fullPath);
    }
}

public enum PosterLayout
{
    Standard,
    MinimalTop,
    CenterFocus,
    BottomOverlay
}