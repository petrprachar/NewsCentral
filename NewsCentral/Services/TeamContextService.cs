using System;

namespace NewsCentral.Services
{
    public class TeamContextService
    {
        private string? _currentTeamId;
        private string? _currentTeamFolderName;

        /// <summary>
        /// Event fired when the current team context changes
        /// </summary>
        public event Action? OnTeamChanged;

        /// <summary>
        /// Current team ID
        /// </summary>
        public string? CurrentTeamId
        {
            get => _currentTeamId;
            set
            {
                if (_currentTeamId != value)
                {
                    _currentTeamId = value;
                    OnTeamChanged?.Invoke();
                }
            }
        }

        /// <summary>
        /// Current team folder name (e.g., "EXP_JP", "EXP_US")
        /// </summary>
        public string? CurrentTeamFolderName
        {
            get => _currentTeamFolderName;
            set
            {
                if (_currentTeamFolderName != value)
                {
                    _currentTeamFolderName = value;
                    OnTeamChanged?.Invoke();
                }
            }
        }

        /// <summary>
        /// Set both team ID and folder name at once
        /// </summary>
        public void SetCurrentTeam(string teamId, string teamFolderName)
        {
            _currentTeamId = teamId;
            _currentTeamFolderName = teamFolderName;
            OnTeamChanged?.Invoke();
        }

        /// <summary>
        /// Clear the current team selection
        /// </summary>
        public void ClearCurrentTeam()
        {
            _currentTeamId = null;
            _currentTeamFolderName = null;
            OnTeamChanged?.Invoke();
        }

        /// <summary>
        /// Check if a team is currently selected
        /// </summary>
        public bool HasTeamSelected()
        {
            return !string.IsNullOrEmpty(_currentTeamId) &&
                   !string.IsNullOrEmpty(_currentTeamFolderName);
        }

        /// <summary>
        /// Get the current team folder name
        /// </summary>
        public string? GetCurrentTeamFolderName()
        {
            return _currentTeamFolderName;
        }

        /// <summary>
        /// Get the current team ID
        /// </summary>
        public string? GetCurrentTeamId()
        {
            return _currentTeamId;
        }
    }
}