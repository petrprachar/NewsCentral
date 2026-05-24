using System.Text.Json;
using NewsViewer.Models;

namespace NewsViewer.Services;

public sealed class TelemetryWriter
{
    private readonly string _uploadsPath;

    public TelemetryWriter(string cacheRootPath)
    {
        _uploadsPath = Path.Combine(cacheRootPath, "uploads");
    }

    public void WriteSession(
        string presentationId,
        string teamId,
        DateTime startTime,
        DateTime endTime,
        string closeReason)
    {
        try
        {
            Directory.CreateDirectory(_uploadsPath);

            var record = new SessionTelemetry
            {
                SessionId       = Guid.NewGuid().ToString(),
                PresentationId  = presentationId,
                TeamId          = teamId,
                SessionStartTime = startTime,
                SessionEndTime   = endTime,
                CloseReason      = closeReason,
                Signature        = null
            };

            var path = Path.Combine(_uploadsPath, $"session-{record.SessionId}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(record, JsonDefaults.Options));
        }
        catch { }
    }
}
