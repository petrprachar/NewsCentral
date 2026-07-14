using System.Text.Json;
using System.Text.Json.Serialization;
using NewsCentral.Models.IndexFile;
using NewsCentral.Security;
using Xunit;

namespace NewsCentral.Shared.Tests;

/// <summary>
/// Reserved int Priority on the index schema: it is emitted, sits inside the ECDSA-signed payload,
/// and round-trips cleanly through a client-style deserialization. Guards the single signed-schema
/// break (ShowMode removed, Priority added) so the future priority-display feature is wire-neutral.
/// </summary>
public sealed class PriorityFieldTests
{
    private static readonly EcdsaSignatureService Svc = new();

    // Mirrors how NewsService / NewsViewer parse index.json (standard ISO, no SmartDateTimeConverter).
    private static readonly JsonSerializerOptions ClientOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters                  = { new JsonStringEnumConverter() }
    };

    private static TeamIndexFile MakeIndex(int priority) => new()
    {
        TeamFolderName = "cz-its",
        TeamName       = "CZ ITS",
        Version        = "1.0.0",
        IndexHash      = "deadbeef",
        PublishedAssignments =
        {
            new PublishedAssignmentIndex
            {
                AssignmentId     = "a1",
                PresentationName = "Hello",
                Priority         = priority
            }
        }
    };

    [Fact]
    public void NonZeroPriority_SignedIndex_VerifiesValid()
    {
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();
        var index = MakeIndex(priority: 7);

        index.Signature = Svc.Sign(index, priv);

        Assert.Equal(VerifyResult.Valid, Svc.Verify(index, pub));
    }

    [Fact]
    public void NonZeroPriority_SurvivesClientRoundTrip_AndVerifiesValid()
    {
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();
        var index = MakeIndex(priority: 7);
        index.Signature = Svc.Sign(index, priv);

        // Serialize as the authoring tier writes, then deserialize exactly as a client verifier does.
        var json     = JsonSerializer.Serialize(index, ClientOptions);
        var reloaded = JsonSerializer.Deserialize<TeamIndexFile>(json, ClientOptions)!;

        Assert.Equal(7, reloaded.PublishedAssignments[0].Priority);   // value round-tripped
        Assert.Equal(VerifyResult.Valid, Svc.Verify(reloaded, pub));  // signature still verifies
    }

    [Fact]
    public void Priority_IsCoveredBySignature_TamperingInvalidates()
    {
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();
        var index = MakeIndex(priority: 7);
        index.Signature = Svc.Sign(index, priv);

        index.PublishedAssignments[0].Priority = 8;   // mutate only Priority after signing

        Assert.Equal(VerifyResult.Invalid, Svc.Verify(index, pub));
    }

    [Fact]
    public void DefaultPriority_SignsVerifiesAndRoundTrips()
    {
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();
        var index = MakeIndex(priority: 0);   // default
        index.Signature = Svc.Sign(index, priv);

        Assert.Equal(VerifyResult.Valid, Svc.Verify(index, pub));

        var json     = JsonSerializer.Serialize(index, ClientOptions);
        var reloaded = JsonSerializer.Deserialize<TeamIndexFile>(json, ClientOptions)!;

        Assert.Equal(0, reloaded.PublishedAssignments[0].Priority);
        Assert.Equal(VerifyResult.Valid, Svc.Verify(reloaded, pub));
    }
}
