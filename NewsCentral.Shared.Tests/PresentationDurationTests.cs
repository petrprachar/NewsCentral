using System.Text.Json;
using System.Text.Json.Serialization;
using NewsCentral.Models;
using NewsCentral.Models.IndexFile;
using NewsCentral.Security;
using Xunit;

namespace NewsCentral.Shared.Tests;

/// <summary>
/// Display-duration semantics: PresentationDefaults.ResolveDuration is the single tested source of the
/// 30s default and the -1 never-auto-close reservation. 0 (the value every existing presentation
/// carries) and any other negative resolve to the default — only exactly -1 is never-close. Plus a
/// sign/verify round-trip proving an index carrying -1 stays a wire-neutral, signature-covered int.
/// </summary>
public sealed class PresentationDurationTests
{
    [Fact]
    public void Zero_ResolvesToDefault_30()
    {
        Assert.Equal(30, PresentationDefaults.DisplayDurationSeconds);          // the default is 30
        Assert.Equal(30, PresentationDefaults.ResolveDuration(0));
    }

    [Fact]
    public void Positive_ResolvesToItself()
    {
        Assert.Equal(45, PresentationDefaults.ResolveDuration(45));
    }

    [Fact]
    public void MinusOne_ResolvesToNeverAutoClose()
    {
        Assert.Equal(PresentationDefaults.NeverAutoClose, PresentationDefaults.ResolveDuration(-1));
        Assert.Equal(-1, PresentationDefaults.ResolveDuration(-1));
    }

    [Fact]
    public void UnknownNegative_ResolvesToDefault_NotNeverClose()
    {
        // Guard against a future raw < 0 bug: only exactly -1 is never-close; -7 is "unset".
        Assert.Equal(30, PresentationDefaults.ResolveDuration(-7));
    }

    [Fact]
    public void IndexCarryingNeverAutoClose_SignsAndVerifiesValid_AndRoundTrips()
    {
        // Mirrors how NewsService / NewsViewer parse index.json (standard ISO, no SmartDateTimeConverter).
        var clientOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            Converters                  = { new JsonStringEnumConverter() }
        };

        var svc = new EcdsaSignatureService();
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();

        var index = new TeamIndexFile
        {
            TeamFolderName = "cz-its",
            TeamName       = "CZ ITS",
            Version        = "1.0.0",
            IndexHash      = "deadbeef",
            PublishedAssignments =
            {
                new PublishedAssignmentIndex
                {
                    AssignmentId            = "a1",
                    PresentationName        = "Hello",
                    DisplayDurationSeconds  = PresentationDefaults.NeverAutoClose   // -1 on the wire
                }
            }
        };

        index.Signature = svc.Sign(index, priv);
        Assert.Equal(VerifyResult.Valid, svc.Verify(index, pub));

        var json     = JsonSerializer.Serialize(index, clientOptions);
        var reloaded = JsonSerializer.Deserialize<TeamIndexFile>(json, clientOptions)!;

        Assert.Equal(-1, reloaded.PublishedAssignments[0].DisplayDurationSeconds);  // value round-tripped
        Assert.Equal(VerifyResult.Valid, svc.Verify(reloaded, pub));                // signature still verifies
    }
}
