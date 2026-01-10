namespace NewsCentral.Services;

public class TeamContextService
{
    private string? _currentTeamId;
    private string? _currentTeamFolderName;

    public event Action? OnTeamChanged;

    public void SetCurrentTeam(string teamId, string teamFolderName)
    {
        _currentTeamId = teamId;
        _currentTeamFolderName = teamFolderName;
        OnTeamChanged?.Invoke();
    }

    public string? GetCurrentTeamId() => _currentTeamId;
    public string? GetCurrentTeamFolderName() => _currentTeamFolderName;

    public bool HasTeamSelected() => !string.IsNullOrEmpty(_currentTeamId);
}