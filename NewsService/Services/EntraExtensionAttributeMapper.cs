using System.Text.Json;
using System.Text.RegularExpressions;

namespace NewsService.Services;

/// <summary>
/// Pure, offline-testable mapper from a Graph device's <c>extensionAttributes</c> JSON object
/// into the "extensionAttribute1".."extensionAttribute15" dictionary the resolver expects.
///
/// All 15 slots are pre-seeded null. Values are passed through raw (no lowercasing — the
/// resolver canonicalizes). Keys outside the extensionAttribute1..15 set are ignored, as are
/// non-string / non-null JSON values.
/// </summary>
public static class EntraExtensionAttributeMapper
{
    private static readonly Regex KeyPattern =
        new(@"^extensionAttribute(?:[1-9]|1[0-5])$", RegexOptions.Compiled);

    public static Dictionary<string, string?> Map(JsonElement extensionAttributes)
    {
        var result = new Dictionary<string, string?>(15);
        for (var i = 1; i <= 15; i++)
            result[$"extensionAttribute{i}"] = null;

        if (extensionAttributes.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var prop in extensionAttributes.EnumerateObject())
        {
            if (!KeyPattern.IsMatch(prop.Name)) continue;   // ignore stray keys

            result[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),   // raw value
                JsonValueKind.Null   => null,
                _                    => null                       // ignore other JSON types
            };
        }

        return result;
    }
}
