namespace NewsService.Models;

/// <summary>Written to %programdata%\NewsCentral\status.json after each poll cycle.</summary>
public class StatusFile
{
    public DateTime LastSyncTime { get; set; } = DateTime.UtcNow;
    public bool IsOnline { get; set; }
    public string SyncSource { get; set; } = "None"; // Azure | Share | None
}
