using System.Collections.Concurrent;
using Microsoft.Graph;
using NewsService.Configuration;

namespace NewsService.Services;

/// <summary>
/// Resolves group display names to object ids and evaluates the device's transitive group
/// membership via Microsoft Graph. Reuses the same lazily-built <see cref="GraphServiceClient"/> /
/// credential as <see cref="EntraDeviceClient"/> — nothing is constructed when Entra is disabled.
///
/// This is the only environment-bound part of the group-team feature; the decision logic
/// (<see cref="GroupTeamDecision"/>, <see cref="GroupOutcomeMapper"/>) and the orchestrator are
/// pure / fake-tested.
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

    public async Task<EntraGroupEvaluation> EvaluateAsync(
        string deviceObjectId, string? inclusionName, string? exclusionName, CancellationToken ct)
    {
        try
        {
            // 1. Resolve the inclusion group (required). The caller (orchestrator) only invokes this
            //    when an inclusion group is configured; guard defensively all the same.
            if (string.IsNullOrWhiteSpace(inclusionName))
                return new EntraGroupEvaluation(EntraGroupStatus.Success, InInclusion: false);

            var (incStatus, inclusionId) = await ResolveGroupIdAsync(inclusionName, ct);
            if (incStatus != EntraGroupStatus.Success)
                return new EntraGroupEvaluation(incStatus);

            // 2. Resolve the exclusion group if configured. A misconfigured exclusion name fails
            //    closed (its resolution status is returned → no team) rather than silently granting.
            string? exclusionId = null;
            if (!string.IsNullOrWhiteSpace(exclusionName))
            {
                var (excStatus, id) = await ResolveGroupIdAsync(exclusionName, ct);
                if (excStatus != EntraGroupStatus.Success)
                    return new EntraGroupEvaluation(excStatus);
                exclusionId = id;
            }

            // 3. One checkMemberGroups call evaluates both groups (transitive membership).
            var groupIds = exclusionId is null ? [inclusionId!] : new List<string> { inclusionId!, exclusionId };

            var body = new Microsoft.Graph.Devices.Item.CheckMemberGroups.CheckMemberGroupsPostRequestBody
            {
                GroupIds = groupIds
            };

            var resp = await Graph.Devices[deviceObjectId].CheckMemberGroups
                .PostAsCheckMemberGroupsPostResponseAsync(body, cancellationToken: ct);

            var member = resp?.Value ?? [];

            return new EntraGroupEvaluation(
                EntraGroupStatus.Success,
                InInclusion: member.Contains(inclusionId!),
                InExclusion: exclusionId is not null && member.Contains(exclusionId));
        }
        catch (Exception ex)
        {
            // 403 is persistent (missing GroupMember.Read.All consent); everything else — including an
            // unexpected 404 on these calls — is treated as transient (Unreachable → grace).
            if (GraphFailureClassifier.Classify(ex) == GraphFailureKind.PermissionDenied)
            {
                logger.LogError(ex,
                    "Entra group check denied (403) — check GroupMember.Read.All admin consent.");
                return new EntraGroupEvaluation(EntraGroupStatus.PermissionDenied);
            }

            logger.LogWarning(ex, "Entra group check failed (transient) — treated as Unreachable.");
            return new EntraGroupEvaluation(EntraGroupStatus.Unreachable);
        }
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

    /// <summary>Escapes single quotes for an OData string literal ( ' → '' ).</summary>
    private static string EscapeODataLiteral(string value) => value.Replace("'", "''");
}
