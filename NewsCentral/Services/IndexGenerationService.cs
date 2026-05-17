using NewsCentral.Configuration;
using NewsCentral.Models;
using NewsCentral.Models.IndexFile;
using NewsCentral.Repositories;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace NewsCentral.Services
{
    public class IndexGenerationService
    {
        private readonly string _basePath;
        private readonly IServiceProvider _serviceProvider;
        private readonly AuthenticationService _authService;

        private const string INDEX_FILENAME = "index.json";

        public IndexGenerationService(
            AppConfiguration config,
            IServiceProvider serviceProvider,
            AuthenticationService authService)
        {
            _basePath = config.DataPath;
            _serviceProvider = serviceProvider;
            _authService = authService;
        }

        public async Task<TeamIndexFile> GenerateIndexForTeamAsync(string teamFolderName)
        {
            System.Diagnostics.Debug.WriteLine($"=== GenerateIndexForTeam: {teamFolderName} ===");

            // Lazy resolve services
            var teamService = _serviceProvider.GetRequiredService<TeamService>();
            var assignmentService = _serviceProvider.GetRequiredService<AssignmentService>();

            var team = await teamService.GetTeamByFolderNameAsync(teamFolderName);
            if (team == null)
            {
                throw new InvalidOperationException($"Team not found: {teamFolderName}");
            }

            var index = new TeamIndexFile
            {
                TeamFolderName = teamFolderName,
                TeamName = team.Name,
                GeneratedAt = DateTime.UtcNow,
                Version = "1.0.0",
                PublishedAssignments = new List<PublishedAssignmentIndex>()
            };

            var allAssignments = await assignmentService.GetAssignmentsForTeamAsync(teamFolderName);

            var publishedAssignments = allAssignments
                .Where(a => a.Status == AssignmentStatus.Published)
                .OrderByDescending(a => a.PublishedDate ?? a.DateCreated)
                .ToList();

            System.Diagnostics.Debug.WriteLine($"Found {publishedAssignments.Count} published assignments");

            foreach (var assignment in publishedAssignments)
            {
                try
                {
                    var indexEntry = await BuildIndexEntryAsync(teamFolderName, assignment);
                    if (indexEntry != null)
                    {
                        index.PublishedAssignments.Add(indexEntry);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error building index entry for assignment {assignment.AssignmentID}: {ex.Message}");
                }
            }

            index.Statistics = CalculateStatistics(index.PublishedAssignments);
            index.IndexHash = CalculateIndexHash(index);

            System.Diagnostics.Debug.WriteLine($"Index generated with {index.PublishedAssignments.Count} entries");

            return index;
        }

        private async Task<PublishedAssignmentIndex?> BuildIndexEntryAsync(string teamFolderName, Assignment assignment)
        {
            // Lazy resolve services
            var presentationService = _serviceProvider.GetRequiredService<PresentationService>();
            var scheduleService = _serviceProvider.GetRequiredService<ScheduleService>();
            var teamService = _serviceProvider.GetRequiredService<TeamService>();

            var presentation = await presentationService.GetPresentationAsync(
                assignment.SourceTeam,
                assignment.PresentationID);

            if (presentation == null)
            {
                System.Diagnostics.Debug.WriteLine($"Presentation not found: {assignment.PresentationID}");
                return null;
            }

            var schedule = await scheduleService.GetScheduleAsync(
                assignment.SourceTeam,
                assignment.ScheduleID);

            if (schedule == null)
            {
                System.Diagnostics.Debug.WriteLine($"Schedule not found: {assignment.ScheduleID}");
                return null;
            }

            var sourceTeam = await teamService.GetTeamByFolderNameAsync(assignment.SourceTeam);

            var contentInfo = await BuildContentInfoAsync(presentation);

            var indexEntry = new PublishedAssignmentIndex
            {
                AssignmentId = assignment.AssignmentID,
                PresentationId = presentation.PresentationID,
                ScheduleId = schedule.ScheduleID,

                PresentationName = presentation.Name,
                PresentationDescription = presentation.Description,
                PresentationVersion = presentation.Version,
                PresentationLastModified = presentation.LastModified,

                ScheduleStart = DateTime.SpecifyKind(schedule.ScheduleStart, DateTimeKind.Unspecified),
                ScheduleEnd = DateTime.SpecifyKind(schedule.ScheduleEnd, DateTimeKind.Unspecified),
                DaysOfWeek = schedule.DaysOfWeek,

                PublishedDate = assignment.PublishedDate ?? assignment.DateCreated,
                PublishedBy = assignment.PublishedBy ?? assignment.CreatedBy,

                DisplayTypes = new DisplayTypeInfo
                {
                    IsNewsOfWeek = presentation.IsNewsOfWeek,
                    IsWallpaper = presentation.IsWallpaper,
                    IsLogonScreen = presentation.IsLogonScreen
                },

                Content = contentInfo,

                SourceTeamFolderName = assignment.SourceTeam,
                SourceTeamName = sourceTeam?.Name ?? assignment.SourceTeam
            };

            return indexEntry;
        }

        private async Task<ContentInfo> BuildContentInfoAsync(Presentation presentation)
        {
            var imagePath = !string.IsNullOrEmpty(presentation.GeneratedImagePath)
                ? presentation.GeneratedImagePath
                : presentation.OriginalImagePath;

            var contentInfo = new ContentInfo
            {
                ImagePath = imagePath,
                MoreInfoUrl = presentation.MoreUrl,
                ImageLastModified = presentation.LastModified
            };

            if (!string.IsNullOrEmpty(imagePath))
            {
                var fullImagePath = Path.Combine(_basePath, imagePath);

                if (File.Exists(fullImagePath))
                {
                    try
                    {
                        var fileInfo = new FileInfo(fullImagePath);
                        contentInfo.ImageSizeBytes = fileInfo.Length;

                        var imageHash = await CalculateFileHashAsync(fullImagePath);
                        contentInfo.ImageHash = $"sha256:{imageHash}";

                        System.Diagnostics.Debug.WriteLine($"Image: {Path.GetFileName(imagePath)} - Size: {contentInfo.ImageSizeBytes} bytes, Hash: {imageHash.Substring(0, 16)}...");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error processing image {imagePath}: {ex.Message}");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"Warning: Image file not found: {fullImagePath}");
                }
            }

            return contentInfo;
        }

        private IndexStatistics CalculateStatistics(List<PublishedAssignmentIndex> assignments)
        {
            var now = DateTime.Now;

            var stats = new IndexStatistics
            {
                TotalPublishedAssignments = assignments.Count,
                ActiveAssignments = 0,
                UpcomingAssignments = 0,
                ExpiredAssignments = 0
            };

            foreach (var assignment in assignments)
            {
                var scheduleStart = assignment.ScheduleStart;
                var scheduleEnd = assignment.ScheduleEnd;

                if (now >= scheduleStart && now <= scheduleEnd)
                {
                    stats.ActiveAssignments++;
                }
                else if (now < scheduleStart)
                {
                    stats.UpcomingAssignments++;
                }
                else if (now > scheduleEnd)
                {
                    stats.ExpiredAssignments++;
                }
            }

            return stats;
        }

        public async Task SaveIndexFileAsync(string teamFolderName, TeamIndexFile index)
        {
            var indexPath = GetIndexFilePath(teamFolderName);

            var directory = Path.GetDirectoryName(indexPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var jsonOptions = JsonConfiguration.GetIndexJsonOptions();
            var jsonContent = JsonSerializer.Serialize(index, jsonOptions);

            await File.WriteAllTextAsync(indexPath, jsonContent, Encoding.UTF8);

            System.Diagnostics.Debug.WriteLine($"✓ Index file saved: {indexPath}");
            System.Diagnostics.Debug.WriteLine($"  Size: {jsonContent.Length} bytes");
            System.Diagnostics.Debug.WriteLine($"  Entries: {index.PublishedAssignments.Count}");
        }

        public async Task GenerateAndSaveIndexAsync(string teamFolderName)
        {
            System.Diagnostics.Debug.WriteLine($"=== GenerateAndSaveIndex: {teamFolderName} ===");

            var index = await GenerateIndexForTeamAsync(teamFolderName);
            await SaveIndexFileAsync(teamFolderName, index);

            System.Diagnostics.Debug.WriteLine($"✓ Index generation complete for {teamFolderName}");
        }

        public string GetIndexFilePath(string teamFolderName)
        {
            return Path.Combine(_basePath, teamFolderName, INDEX_FILENAME);
        }

        public bool IndexFileExists(string teamFolderName)
        {
            var indexPath = GetIndexFilePath(teamFolderName);
            return File.Exists(indexPath);
        }

        public async Task<string?> GetCurrentIndexHashAsync(string teamFolderName)
        {
            var indexPath = GetIndexFilePath(teamFolderName);

            if (!File.Exists(indexPath))
            {
                return null;
            }

            try
            {
                var jsonContent = await File.ReadAllTextAsync(indexPath);
                var jsonOptions = JsonConfiguration.GetIndexJsonOptions();
                var index = JsonSerializer.Deserialize<TeamIndexFile>(jsonContent, jsonOptions);

                return index?.IndexHash;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error reading index hash: {ex.Message}");
                return null;
            }
        }

        private async Task<string> CalculateFileHashAsync(string filePath)
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hashBytes = await sha256.ComputeHashAsync(stream);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }

        private string CalculateIndexHash(TeamIndexFile index)
        {
            var indexForHashing = new
            {
                index.TeamFolderName,
                index.TeamName,
                index.GeneratedAt,
                index.Version,
                index.PublishedAssignments,
                index.Statistics
            };

            var jsonOptions = JsonConfiguration.GetIndexJsonOptions();
            var jsonContent = JsonSerializer.Serialize(indexForHashing, jsonOptions);

            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(jsonContent));
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }

        public async Task<int> RegenerateAllIndexesAsync()
        {
            System.Diagnostics.Debug.WriteLine("=== RegenerateAllIndexes ===");

            // Lazy resolve service
            var teamService = _serviceProvider.GetRequiredService<TeamService>();
            var allTeams = await teamService.GetAllTeamsAsync();
            var successCount = 0;

            foreach (var team in allTeams)
            {
                try
                {
                    await GenerateAndSaveIndexAsync(team.FolderName);
                    successCount++;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error generating index for {team.FolderName}: {ex.Message}");
                }
            }

            System.Diagnostics.Debug.WriteLine($"✓ Regenerated {successCount} of {allTeams.Count} team indexes");
            return successCount;
        }

        public async Task DeleteIndexFileAsync(string teamFolderName)
        {
            var indexPath = GetIndexFilePath(teamFolderName);

            if (File.Exists(indexPath))
            {
                await Task.Run(() => File.Delete(indexPath));
                System.Diagnostics.Debug.WriteLine($"✓ Index file deleted: {indexPath}");
            }
        }
    }
}