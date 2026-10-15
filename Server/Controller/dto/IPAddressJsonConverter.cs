#nullable enable
using System.Net;
using System.Text.Json.Serialization;
using System.Text.Json;


namespace WANT.Controller.Dto
{
    /// <summary>Сериализует IPAddress как строку и обратно.</summary>
    public sealed class IPAddressJsonConverter : JsonConverter<IPAddress>
    {
        public override IPAddress? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var s = reader.GetString();
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (IPAddress.TryParse(s, out var ip)) return ip;
            throw new JsonException($"Invalid IP address: '{s}'");
        }

        public override void Write(Utf8JsonWriter writer, IPAddress value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString());
        }
    }
}
