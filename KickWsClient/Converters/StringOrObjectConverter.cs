using System.Text.Json;
using System.Text.Json.Serialization;

namespace KickWsClient.Converters;

// Literal mess, but it is AI generated so what do you expect. Couldn't be bothered writing this myself.
public class StringOrObjectConverter<T> : JsonConverter<T>
{
    public override bool CanConvert(Type typeToConvert)
    {
        return typeof(T).IsAssignableFrom(typeToConvert) || (typeof(T) == typeof(string) && typeToConvert == typeof(object));
    }

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return (T)(object)reader.GetString()!;
        }

        if (reader.TokenType == JsonTokenType.Null)
        {
            return (T)(object)string.Empty;
        }

        using var document = JsonDocument.ParseValue(ref reader);
        return (T)(object)document.RootElement.GetRawText();
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (value is string)
        {
            writer.WriteStringValue((string)(object)value);
        }
        else
        {
            JsonSerializer.Serialize(writer, value, options);
        }
    }
}