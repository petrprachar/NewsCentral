using System.Text.Json;
using NewsService.Services;
using Xunit;

namespace NewsService.Tests;

public sealed class EntraExtensionAttributeMapperTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Map_FullSet_AllMappedRaw_NotLowercased()
    {
        // Mirrors the documented v1.0 device extensionAttributes shape.
        var json = """
        {
          "extensionAttribute1":  "FAT",
          "extensionAttribute2":  "CZ",
          "extensionAttribute3":  "Berlin",
          "extensionAttribute4":  "ITS",
          "extensionAttribute5":  "Prague",
          "extensionAttribute6":  "DE",
          "extensionAttribute7":  "v7",
          "extensionAttribute8":  "v8",
          "extensionAttribute9":  "v9",
          "extensionAttribute10": "v10",
          "extensionAttribute11": "v11",
          "extensionAttribute12": "v12",
          "extensionAttribute13": "v13",
          "extensionAttribute14": "v14",
          "extensionAttribute15": "New York"
        }
        """;

        var map = EntraExtensionAttributeMapper.Map(Parse(json));

        Assert.Equal(15, map.Count);
        Assert.Equal("FAT", map["extensionAttribute1"]);
        Assert.Equal("CZ", map["extensionAttribute2"]);
        Assert.Equal("Prague", map["extensionAttribute5"]);
        Assert.Equal("New York", map["extensionAttribute15"]);   // raw, not lowercased
    }

    [Fact]
    public void Map_SelectorOnly_OthersNull()
    {
        var map = EntraExtensionAttributeMapper.Map(Parse("""{ "extensionAttribute1": "FAT" }"""));

        Assert.Equal(15, map.Count);
        Assert.Equal("FAT", map["extensionAttribute1"]);
        Assert.Null(map["extensionAttribute2"]);
        Assert.Null(map["extensionAttribute15"]);
    }

    [Fact]
    public void Map_ExplicitJsonNull_MapsToNull()
    {
        var json = """{ "extensionAttribute1": "FAT", "extensionAttribute2": null }""";

        var map = EntraExtensionAttributeMapper.Map(Parse(json));

        Assert.Equal("FAT", map["extensionAttribute1"]);
        Assert.Null(map["extensionAttribute2"]);
    }

    [Fact]
    public void Map_StrayKeys_Ignored()
    {
        var json = """
        {
          "extensionAttribute1": "FAT",
          "extensionAttribute0": "x",
          "extensionAttribute16": "y",
          "displayName": "PC-001",
          "extensionAttributeX": "z"
        }
        """;

        var map = EntraExtensionAttributeMapper.Map(Parse(json));

        Assert.Equal(15, map.Count);
        Assert.Equal("FAT", map["extensionAttribute1"]);
        Assert.False(map.ContainsKey("extensionAttribute0"));
        Assert.False(map.ContainsKey("extensionAttribute16"));
        Assert.False(map.ContainsKey("displayName"));
    }

    [Fact]
    public void Map_NonStringValues_Ignored()
    {
        var json = """
        {
          "extensionAttribute1": "FAT",
          "extensionAttribute2": 123,
          "extensionAttribute3": true,
          "extensionAttribute4": { "nested": "x" }
        }
        """;

        var map = EntraExtensionAttributeMapper.Map(Parse(json));

        Assert.Equal("FAT", map["extensionAttribute1"]);
        Assert.Null(map["extensionAttribute2"]);
        Assert.Null(map["extensionAttribute3"]);
        Assert.Null(map["extensionAttribute4"]);
    }

    [Fact]
    public void Map_EmptyObject_FifteenNulls()
    {
        var map = EntraExtensionAttributeMapper.Map(Parse("{}"));

        Assert.Equal(15, map.Count);
        Assert.All(map.Values, v => Assert.Null(v));
        for (var i = 1; i <= 15; i++)
            Assert.True(map.ContainsKey($"extensionAttribute{i}"));
    }

    [Fact]
    public void Map_NonObjectElement_FifteenNulls()
    {
        var map = EntraExtensionAttributeMapper.Map(default);   // ValueKind.Undefined

        Assert.Equal(15, map.Count);
        Assert.All(map.Values, v => Assert.Null(v));
    }
}
