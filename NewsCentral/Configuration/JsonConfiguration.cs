using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NewsCentral.Configuration
{
    /// <summary>
    /// Centralized JSON serialization configuration
    /// Ensures consistent DateTime handling across the application
    /// </summary>
    public static class JsonConfiguration
    {
        /// <summary>
        /// Get standard JSON serializer options for index file generation
        /// </summary>
        public static JsonSerializerOptions GetIndexJsonOptions()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,  // Pretty print for readability
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,  // camelCase property names
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Converters =
                {
                    new JsonStringEnumConverter(),  // Serialize enums as strings
                    new SmartDateTimeConverter()    // Handle UTC vs Unspecified DateTime
                }
            };

            return options;
        }

        /// <summary>
        /// Get standard JSON serializer options for general application use
        /// </summary>
        public static JsonSerializerOptions GetStandardJsonOptions()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Converters =
                {
                    new JsonStringEnumConverter(),
                    new SmartDateTimeConverter()
                }
            };

            return options;
        }
    }

    /// <summary>
    /// Smart DateTime converter that handles both UTC (system events) 
    /// and Unspecified (schedule times) DateTime values correctly
    /// </summary>
    public class SmartDateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var dateString = reader.GetString();

            if (string.IsNullOrEmpty(dateString))
            {
                return DateTime.MinValue;
            }

            // If ends with Z, parse as UTC
            if (dateString.EndsWith("Z", StringComparison.OrdinalIgnoreCase))
            {
                return DateTime.Parse(dateString).ToUniversalTime();
            }

            // If contains timezone offset like +00:00 or -05:00
            if (dateString.Contains("+") || dateString.LastIndexOf('-') > 8)
            {
                return DateTime.Parse(dateString).ToUniversalTime();
            }

            // Otherwise, parse as Unspecified (local time for schedules)
            return DateTime.SpecifyKind(DateTime.Parse(dateString), DateTimeKind.Unspecified);
        }

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        {
            switch (value.Kind)
            {
                case DateTimeKind.Utc:
                    // System events - with Z suffix
                    // Format: "2026-05-17T14:22:00Z"
                    writer.WriteStringValue(value.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                    break;

                case DateTimeKind.Unspecified:
                    // Schedule times - NO Z suffix
                    // Format: "2026-05-18T09:00:00"
                    writer.WriteStringValue(value.ToString("yyyy-MM-ddTHH:mm:ss"));
                    break;

                case DateTimeKind.Local:
                    // Convert to UTC for system events
                    writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"));
                    break;
            }
        }
    }
}
