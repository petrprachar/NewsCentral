using System.Text.Json;
using System.Text.Json.Serialization;

namespace NewsService;

internal static class JsonDefaults
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
