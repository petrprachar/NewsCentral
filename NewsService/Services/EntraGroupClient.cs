using System.Collections.Concurrent;
using Microsoft.Graph;
using NewsService.Configuration;

namespace NewsService.Services;

/// <summary>
/// Resolves group display names to object ids and evaluates the device's transitive group
/// membership via Microsoft Graph, for a whole set of names in one batch. Reuses the same
/// lazily-built <see cref="GraphServiceClient"/> / credential as <see cref="EntraDeviceClient"/> —
/// nothing is constructed when Entra is disabled or the requested name set is empty.
///
/// This is the only environment-bound part of the group-team feature; the decision logic
/// (<see cref="GroupTeamDecision"/>, <see cref="GroupOutcomeMapper"/>), the chunking/mapping helper
/// (<see cref="GroupMembershipChunker"/>), and the orchestrator are pure / fake-tested.
/// </summary>
public sealed class EntraGroupClient(
    AzureBlobSection azureBlob,
    AzureProxyTransportFactory transportFactory,
    ILogger<EntraGroupClient> logger) : IEntraGroupClient
{
    private static readonly string[] GraphScopes = ["https://graph.microsoft.com/.default"];

    // Successful display-name → object-id resolutions, cached for the process lifetime.
    // Ambiguous / not-found results are never cached.
    private readonly ConcurrentDictionary<string, string> _nameToId = new(StringComparer.OrdinalIgnoreCase);

    private GraphServiceClient? _graph;

    private GraphServiceClient Graph => _graph ??= BuildGraph();

    private GraphServiceClient BuildGraph()
    {
        var credential = AzureCredentialFactory.Create(azureBlob, transportFactory.AzureTransport);

        // Off (GraphHttpClient == null) → credential-only construction, exactly as before; on → use the
        // shared HttpClient (Graph middleware over the WinHTTP transport).
        return transportFactory.GraphHttpClient is null
            ? new GraphServiceClient(credential, GraphScopes)
            : new GraphServiceClient(transportFactory.GraphHttpClient, credential, GraphScopes);
    }

    public async Task<EntraGroupSnapshot> EvaluateAsync(
        string deviceObjectId, IReadOnlyCollection<string> groupNames, CancellationToken ct)
    {
        var distinctNames = groupNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinctNames.Count == 0)
            return Empty(EntraGroupStatus.Success);

        try
        {
            // 1. Resolve every distinct name. Names that don't resolve simply carry their failure
            //    status — they contribute no id to the membership check below.
            var nameStatus = new Dictionary<string, EntraGroupStatus>(StringComparer.OrdinalIgnoreCase);
            var idToName   = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in distinctNames)
            {
                var (status, id) = await ResolveGroupIdAsync(name, ct);
                nameStatus[name] = status;
                if (status == EntraGroupStatus.Success && id is not null)
                    idToName[id] = name;
            }

            // 2. If NO name resolved, there is nothing to check membership for — skip the Graph call.
            var memberOfNames = idToName.Count == 0
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : GroupMembershipChunker.MapMembership(
                    await CheckMembershipChunksAsync(deviceObjectId, idToName.Keys.ToList(), ct),
                    idToName);

            return new EntraGroupSnapshot(EntraGroupStatus.Success, nameStatus, memberOfNames);
        }
        catch (Exception ex)
        {
            // 403 is persistent (missing GroupMember.Read.All consent); everything else — including an
            // unexpected 404 on these calls — is treated as transient (Unreachable → grace). A failure
            // in any chunk fails the WHOLE snapshot — never return partial membership, which would
            // read as "not a member" and could wrongly grant a team past an exclusion.
            if (GraphFailureClassifier.Classify(ex) == GraphFailureKind.PermissionDenied)
            {
                logger.LogError(ex,
                    "Entra group check denied (403) — check GroupMember.Read.All admin consent.");
                return Empty(EntraGroupStatus.PermissionDenied);
            }

            logger.LogWarning(ex, "Entra group check failed (transient) — treated as Unreachable.");
            return Empty(EntraGroupStatus.Unreachable);
        }
    }

    /// <summary>
    /// Issues one <c>checkMemberGroups</c> call per <see cref="GroupMembershipChunker.ChunkSize"/>
    /// ids and returns each call's raw member-id result set (unmapped) — <see cref="EvaluateAsync"/>
    /// unions and maps them back to display names via <see cref="GroupMembershipChunker.MapMembership"/>.
    /// </summary>
    private async Task<List<IReadOnlyCollection<string>>> CheckMembershipChunksAsync(
        string deviceObjectId, IReadOnlyList<string> ids, CancellationToken ct)
    {
        var results = new List<IReadOnlyCollection<string>>();

        foreach (var chunk in GroupMembershipChunker.Chunk(ids))
        {
            var body = new Microsoft.Graph.Devices.Item.CheckMemberGroups.CheckMemberGroupsPostRequestBody
            {
                GroupIds = chunk.ToList()
            };

            var resp = await Graph.Devices[deviceObjectId].CheckMemberGroups
                .PostAsCheckMemberGroupsPostResponseAsync(body, cancellationToken: ct);

            results.Add(resp?.Value ?? []);
        }

        return results;
    }

    /// <summary>
    /// Resolves a group display name to its object id. >1 match → <see cref="EntraGroupStatus.NameAmbiguous"/>;
    /// 0 matches → <see cref="EntraGroupStatus.NameNotFound"/>. Successful single resolutions are cached.
    /// </summary>
    private async Task<(EntraGroupStatus Status, string? Id)> ResolveGroupIdAsync(string name, CancellationToken ct)
    {
        if (_nameToId.TryGetValue(name, out var cached))
            return (EntraGroupStatus.Success, cached);

        var resp = await Graph.Groups.GetAsync(rc =>
        {
            rc.QueryParameters.Filter = $"displayName eq '{EscapeODataLiteral(name)}'";
            rc.QueryParameters.Select = ["id"];
            rc.QueryParameters.Count  = true;                 // advanced query — needs eventual consistency
            rc.Headers.Add("ConsistencyLevel", "eventual");
        }, ct);

        var groups = resp?.Value ?? [];
        if (groups.Count == 0) return (EntraGroupStatus.NameNotFound, null);
        if (groups.Count > 1)  return (EntraGroupStatus.NameAmbiguous, null);

        var id = groups[0].Id!;
        _nameToId[name] = id;                                  // cache only a clean single match
        return (EntraGroupStatus.Success, id);
    }

    private static EntraGroupSnapshot Empty(EntraGroupStatus status) =>
        new(status,
            new Dictionary<string, EntraGroupStatus>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Escapes single quotes for an OData string literal ( ' → '' ).</summary>
    private static string EscapeODataLiteral(string value) => value.Replace("'", "''");
}
