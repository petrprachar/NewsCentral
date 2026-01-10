using NewsCentral.Configuration;
using NewsCentral.Models;
using NewsCentral.Repositories;

namespace NewsCentral.Services;

public class PresentationService
{
    private readonly string _basePath;
    private readonly AuthenticationService _authService;

    public PresentationService(AppConfiguration config, AuthenticationService authService)
    {
        _basePath = config.DataPath;
        _authService = authService;
    }

    public async Task<List<Presentation>> GetPresentationsForTeamAsync(string teamFolderName)
    {
        var repo = new TeamAwareRepository<Presentation>(_basePath, teamFolderName, "presentations");
        return await repo.GetAllAsync();
    }

    public async Task<Presentation?> GetPresentationAsync(string teamFolderName, string presentationId)
    {
        var repo = new TeamAwareRepository<Presentation>(_basePath, teamFolderName, "presentations");
        return await repo.GetByIdAsync(presentationId);
    }

    public async Task<Presentation> CreatePresentationAsync(
        string teamId,
        string teamFolderName,
        string name,
        string description,
        string moreUrl,
        byte[] imageData,
        string originalImageName)
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null)
        {
            throw new UnauthorizedAccessException("Not authenticated");
        }

        // Check if user has ContentAuthor role for this team
        if (!_authService.HasRole(teamId, "ContentAuthor") && !currentUser.IsSystemAdmin)
        {
            throw new UnauthorizedAccessException("You don't have permission to create content for this team");
        }

        // Save original image
        var imageId = Guid.NewGuid().ToString();
        var imageExtension = Path.GetExtension(originalImageName);
        var savedImageName = $"img_{imageId}{imageExtension}";
        var imagePath = Path.Combine(_basePath, teamFolderName, "images", "original", savedImageName);

        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        await File.WriteAllBytesAsync(imagePath, imageData);

        // Create presentation
        var presentation = new Presentation
        {
            PresentationID = Guid.NewGuid().ToString(),
            Version = "1",
            TeamID = teamId,
            TeamFolderName = teamFolderName,
            Name = name,
            Description = description,
            MoreUrl = moreUrl,
            DateCreated = DateTime.UtcNow,
            OriginalImagePath = $"{teamFolderName}/images/original/{savedImageName}",
            ImageOriginalName = originalImageName,
            ImageName = savedImageName,
            CreatedBy = currentUser.UserID,
            LastModified = DateTime.UtcNow,
            ModifiedBy = currentUser.UserID
        };

        var repo = new TeamAwareRepository<Presentation>(_basePath, teamFolderName, "presentations");
        return await repo.CreateAsync(presentation);
    }

    public async Task<Presentation> UpdatePresentationAsync(
        string teamFolderName,
        string presentationId,
        string name,
        string description,
        string moreUrl)
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null)
        {
            throw new UnauthorizedAccessException("Not authenticated");
        }

        var repo = new TeamAwareRepository<Presentation>(_basePath, teamFolderName, "presentations");
        var presentation = await repo.GetByIdAsync(presentationId);

        if (presentation == null)
        {
            throw new InvalidOperationException($"Presentation {presentationId} not found");
        }

        // Only creator or SystemAdmin can edit
        if (presentation.CreatedBy != currentUser.UserID && !currentUser.IsSystemAdmin)
        {
            throw new UnauthorizedAccessException("You can only edit your own presentations");
        }

        presentation.Name = name;
        presentation.Description = description;
        presentation.MoreUrl = moreUrl;
        presentation.LastModified = DateTime.UtcNow;
        presentation.ModifiedBy = currentUser.UserID;

        return await repo.UpdateAsync(presentation);
    }

    public async Task<bool> DeletePresentationAsync(string teamFolderName, string presentationId)
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null)
        {
            throw new UnauthorizedAccessException("Not authenticated");
        }

        var repo = new TeamAwareRepository<Presentation>(_basePath, teamFolderName, "presentations");
        var presentation = await repo.GetByIdAsync(presentationId);

        if (presentation == null)
        {
            return false;
        }

        // Only creator or SystemAdmin can delete
        if (presentation.CreatedBy != currentUser.UserID && !currentUser.IsSystemAdmin)
        {
            throw new UnauthorizedAccessException("You can only delete your own presentations");
        }

        return await repo.DeleteAsync(presentationId);
    }

    public async Task<byte[]> GetImageDataAsync(string teamFolderName, string imagePath)
    {
        var fullPath = Path.Combine(_basePath, imagePath);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Image not found: {imagePath}");
        }

        return await File.ReadAllBytesAsync(fullPath);
    }
}