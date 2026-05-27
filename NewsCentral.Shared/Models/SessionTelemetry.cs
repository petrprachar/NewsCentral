using NewsCentral.Security;

namespace NewsCentral.Models;

public class SessionTelemetry : ISignable
{
    public string SessionId { get; set; } = string.Empty;
    public string PresentationId { get; set; } = string.Empty;
    public string TeamId { get; set; } = string.Empty;
    public DateTime SessionStartTime { get; set; }
    public DateTime SessionEndTime { get; set; }
    public string CloseReason { get; set; } = string.Empty;
    public string? Signature { get; set; }
}
