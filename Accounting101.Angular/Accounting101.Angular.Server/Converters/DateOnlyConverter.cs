using System.Text.Json;
using System.Text.Json.Serialization;

namespace Accounting101.Angular.Server.Converters;

public class DateOnlyConverter : JsonConverter<DateOnly>
{
    public override DateOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return typeToConvert == typeof(DateOnly)
            ? DateOnly.FromDateTime(DateTime.Parse(reader.GetString() ?? string.Empty))
            : default;
    }

    public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString("yyyy-MM-dd"));
    }
}
