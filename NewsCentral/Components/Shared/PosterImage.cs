using NewsCentral.Models;

namespace NewsCentral.Components.Shared;

/// <summary>
/// UI-2.1: the single shared helper for turning a <see cref="Presentation"/>'s embedded poster
/// into a displayable <c>data:</c> URL — moved here from Assignments.razor's own private
/// <c>PresentationThumb</c> method so Approvals.razor (which lost its poster preview in UI-2) can
/// build the same data URL once per batch, from the already-cached presentation.
/// </summary>
public static class PosterImage
{
    public static string? ToDataUrl(Presentation? presentation)
    {
        if (presentation is null || string.IsNullOrEmpty(presentation.ContentImageBase64))
            return null;

        var ext = Path.GetExtension(presentation.ImageName ?? presentation.ImageOriginalName ?? string.Empty)
            .ToLowerInvariant();

        var mime = ext switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "image/jpeg"
        };

        return $"data:{mime};base64,{presentation.ContentImageBase64}";
    }
}
