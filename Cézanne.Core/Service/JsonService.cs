using Cézanne.Core.Descriptor;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cézanne.Core.Service
{
    public sealed class Jsons
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // UTF-8
            Converters = {
                new JsonPropertyNameEnumConverter<Manifest.AwaitConditionType>(),
                new JsonPropertyNameEnumConverter<Manifest.ConditionOperator>(),
                new JsonPropertyNameEnumConverter<Manifest.ConditionType>(),
                new JsonPropertyNameEnumConverter<Manifest.JsonPointerOperator>()
            }
        };
    }

    internal class JsonPropertyNameEnumConverter<T> : JsonConverter<T> where T : struct
    {
        private readonly Dictionary<T, string> _code2Json;
        private readonly Dictionary<string, T> _json2code;

        public JsonPropertyNameEnumConverter()
        {
            _code2Json = typeof(T)
                .GetMembers(BindingFlags.Public | BindingFlags.Static)
                .ToDictionary(x => Enum.Parse<T>(x.Name), x => x.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(x.Name));
            _json2code = _code2Json.ToDictionary(x => x.Value, x => x.Key);
        }

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var key = reader.GetString()!;
            return _json2code.TryGetValue(key, out var res) ? res : throw new ArgumentException("Invalid json value for enum {key}");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(_code2Json[value]!);
        }
    }
}