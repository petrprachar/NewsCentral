using System.Text.Json;
using NewsCentral.Models;
using NewsCentral.Security;

namespace NewsViewer.Services;

public sealed class TelemetryWriter
{
    private readonly string _uploadsPath;
    private readonly HmacService _hmac;

    public TelemetryWriter(string cacheRootPath, HmacService hmac)
    {
        _uploadsPath = Path.Combine(cacheRootPath, "uploads");
        _hmac        = hmac;
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
                SessionId        = Guid.NewGuid().ToString(),
                PresentationId   = presentationId,
                TeamId           = teamId,
                SessionStartTime = startTime,
                SessionEndTime   = endTime,
                CloseReason      = closeReason,
                Signature        = null
            };

            record.Signature = _hmac.Sign(record);

            var path = Path.Combine(_uploadsPath, $"session-{record.SessionId}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(record, JsonDefaults.Options));
        }
        catch { }
    }
}
