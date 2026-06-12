namespace NewsCentral.Models;

/// <summary>
/// Schema for {CacheRootPath}\resolved-teams.json. NewsService writes it each poll cycle;
/// NewsViewer reads it and unions the entries with the registry static team list.
///
/// Lists ONLY dynamic, Entra-resolved teams. Dynamic-team index verification uses a public
/// key delivered with the team content (key-with-content model), so this file carries only
/// team identity + grace state — no signature fields. It is a local-tier file protected by
/// cache ACLs. Serializes with the existing camelCase + JsonStringEnumConverter options used
/// across NewsService/NewsViewer; no new serializer options are introduced here.
/// </summary>
public sealed class ResolvedTeamsFile
{
    public DateTime GeneratedUtc { get; set; }
    public List<ResolvedTeamEntry> Teams { get; set; } = new();
}

public sealed class ResolvedTeamEntry
{
    public string TeamFolderName { get; set; } = "";
    public DateTime LastConfirmedUtc { get; set; }
    public ResolvedTeamState State { get; set; } = ResolvedTeamState.Active;
}

public enum ResolvedTeamState
{
    Active,
    Grace
}
