using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orchestrator.Services.Manifest;

public sealed class LenientDateTimeConverter : JsonConverter<DateTime?> {
    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType == JsonTokenType.Null) {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String) {
            var str = reader.GetString();
            if (string.IsNullOrWhiteSpace(str)) {
                return null;
            }

            if (DateTime.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)) {
                return parsed;
            }

            // Ignore bad date formats instead of failing startup.
            return null;
        }

        if (reader.TryGetDateTime(out var dt)) {
            return dt;
        }

        return null;
    }

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options) {
        if (value.HasValue) {
            writer.WriteStringValue(value.Value.ToUniversalTime().ToString("O"));
        }
        else {
            writer.WriteNullValue();
        }
    }
}
