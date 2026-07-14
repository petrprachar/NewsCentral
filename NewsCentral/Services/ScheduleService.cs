using NewsCentral.Configuration;
using NewsCentral.Models;
using NewsCentral.Repositories;

namespace NewsCentral.Services;

public class ScheduleService
{
    private readonly string _basePath;
    private readonly AuthenticationService _authService;

    public ScheduleService(AppConfiguration config, AuthenticationService authService)
    {
        _basePath = config.DataPath;
        _authService = authService;
    }

    public async Task<List<Schedule>> GetSchedulesForTeamAsync(string teamFolderName)
    {
        var repo = new TeamAwareRepository<Schedule>(_basePath, teamFolderName, "schedules");
        return await repo.GetAllAsync();
    }

    public async Task<Schedule?> GetScheduleAsync(string teamFolderName, string scheduleId)
    {
        var repo = new TeamAwareRepository<Schedule>(_basePath, teamFolderName, "schedules");
        return await repo.GetByIdAsync(scheduleId);
    }

    public async Task<Schedule> CreateScheduleAsync(
        string teamFolderName,
        string presentationId,
        string version,
        DateTime scheduleStart,
        DateTime scheduleEnd,
        List<int> daysOfWeek)
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null)
        {
            throw new UnauthorizedAccessException("Not authenticated");
        }

        var schedule = new Schedule
        {
            ScheduleID = Guid.NewGuid().ToString(),
            PresentationID = presentationId,
            Version = version,
            DateCreated = DateTime.UtcNow,
            ScheduleCreated = DateTime.UtcNow,
            ScheduleStart = scheduleStart,
            ScheduleEnd = scheduleEnd,
            DaysOfWeek = string.Join(",", daysOfWeek.OrderBy(d => d)),
            IsActive = true,
            CreatedBy = currentUser.UserID,
            LastModified = DateTime.UtcNow
        };

        var repo = new TeamAwareRepository<Schedule>(_basePath, teamFolderName, "schedules");
        return await repo.CreateAsync(schedule);
    }

    public async Task<Schedule> UpdateScheduleAsync(
        string teamFolderName,
        string scheduleId,
        DateTime scheduleStart,
        DateTime scheduleEnd,
        List<int> daysOfWeek,
        bool isActive)
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null)
        {
            throw new UnauthorizedAccessException("Not authenticated");
        }

        var repo = new TeamAwareRepository<Schedule>(_basePath, teamFolderName, "schedules");
        var schedule = await repo.GetByIdAsync(scheduleId);

        if (schedule == null)
        {
            throw new InvalidOperationException($"Schedule {scheduleId} not found");
        }

        schedule.ScheduleStart = scheduleStart;
        schedule.ScheduleEnd = scheduleEnd;
        schedule.DaysOfWeek = string.Join(",", daysOfWeek.OrderBy(d => d));
        schedule.IsActive = isActive;
        schedule.LastModified = DateTime.UtcNow;

        return await repo.UpdateAsync(schedule);
    }

    public async Task<bool> DeleteScheduleAsync(string teamFolderName, string scheduleId)
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null || !currentUser.IsSystemAdmin)
        {
            throw new UnauthorizedAccessException("Only SystemAdmin can delete schedules");
        }

        var repo = new TeamAwareRepository<Schedule>(_basePath, teamFolderName, "schedules");
        return await repo.DeleteAsync(scheduleId);
    }
}
