using NewsService.Services;
using Xunit;

namespace NewsService.Tests;

/// <summary>
/// Pure unit coverage for the Graph checkMemberGroups 20-id-per-call chunking and the
/// id→display-name mapping — the one piece of EntraGroupClient's batching logic that doesn't
/// require a live Graph call to verify.
/// </summary>
public sealed class GroupMembershipChunkerTests
{
    [Fact]
    public void Chunk_45Ids_ProducesThreeChunksOf20_20_5()
    {
        var ids = Enumerable.Range(1, 45).Select(i => $"id-{i}").ToList();

        var chunks = GroupMembershipChunker.Chunk(ids);

        Assert.Equal(3, chunks.Count);
        Assert.Equal(20, chunks[0].Count);
        Assert.Equal(20, chunks[1].Count);
        Assert.Equal(5, chunks[2].Count);
        Assert.Equal(ids, chunks.SelectMany(c => c));   // order preserved, nothing dropped or duplicated
    }

    [Fact]
    public void Chunk_EmptyInput_ProducesNoChunks()
    {
        Assert.Empty(GroupMembershipChunker.Chunk(new List<string>()));
    }

    [Fact]
    public void Chunk_ExactMultipleOfChunkSize_ProducesNoTrailingEmptyChunk()
    {
        var ids = Enumerable.Range(1, 40).Select(i => $"id-{i}").ToList();

        var chunks = GroupMembershipChunker.Chunk(ids);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(20, chunks[0].Count);
        Assert.Equal(20, chunks[1].Count);
    }

    [Fact]
    public void MapMembership_UnionsChunksAndMapsBackToDisplayNames()
    {
        var ids = Enumerable.Range(1, 45).Select(i => $"id-{i}").ToList();
        var idToName = ids.ToDictionary(id => id, id => $"Group {id}");
        var chunks = GroupMembershipChunker.Chunk(ids);

        // Simulate three separate Graph responses — a couple of "member" ids from each chunk.
        var chunkResults = new List<IReadOnlyCollection<string>>
        {
            new[] { chunks[0][0], chunks[0][5] },   // from the first 20
            new[] { chunks[1][3] },                 // from the second 20
            new[] { chunks[2][2] },                 // from the last 5
        };

        var members = GroupMembershipChunker.MapMembership(chunkResults, idToName);

        Assert.Equal(4, members.Count);
        Assert.Contains(idToName[chunks[0][0]], members);
        Assert.Contains(idToName[chunks[0][5]], members);
        Assert.Contains(idToName[chunks[1][3]], members);
        Assert.Contains(idToName[chunks[2][2]], members);
    }

    [Fact]
    public void MapMembership_IdWithNoNameMapping_IsSkippedNotThrown()
    {
        var idToName = new Dictionary<string, string> { ["id-1"] = "Group 1" };
        var chunkResults = new List<IReadOnlyCollection<string>> { new[] { "id-1", "unknown-id" } };

        var members = GroupMembershipChunker.MapMembership(chunkResults, idToName);

        Assert.Equal(["Group 1"], members);
    }
}
