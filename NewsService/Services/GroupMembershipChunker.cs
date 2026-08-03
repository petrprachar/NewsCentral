namespace NewsService.Services;

/// <summary>
/// Pure helper for Microsoft Graph's <c>checkMemberGroups</c> limit of 20 ids per call: splits a
/// set of group ids into chunks, and unions per-chunk membership results back into display names
/// via an id→name map. No I/O — the actual chunk calls are made by <see cref="EntraGroupClient"/>;
/// this class only does the splitting and the id→name projection, so it is unit-testable without
/// Graph.
/// </summary>
internal static class GroupMembershipChunker
{
    /// <summary>Microsoft Graph's <c>checkMemberGroups</c> limit — ids per call.</summary>
    public const int ChunkSize = 20;

    /// <summary>Splits <paramref name="ids"/> into chunks of at most <see cref="ChunkSize"/>.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Chunk(IReadOnlyList<string> ids)
    {
        var chunks = new List<IReadOnlyList<string>>();
        for (var i = 0; i < ids.Count; i += ChunkSize)
            chunks.Add(ids.Skip(i).Take(ChunkSize).ToList());
        return chunks;
    }

    /// <summary>
    /// Unions the member ids returned by each chunk call and maps them back to display names via
    /// <paramref name="idToName"/>. An id with no entry in the map (should not happen — every id
    /// checked originates from a successful resolution recorded in the same map) is silently
    /// skipped rather than throwing.
    /// </summary>
    public static HashSet<string> MapMembership(
        IEnumerable<IReadOnlyCollection<string>> memberIdChunks,
        IReadOnlyDictionary<string, string> idToName)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in memberIdChunks)
            foreach (var id in chunk)
                if (idToName.TryGetValue(id, out var name))
                    result.Add(name);
        return result;
    }
}
