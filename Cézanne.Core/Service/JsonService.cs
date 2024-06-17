using Cézanne.Core.Descriptor;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using YamlDotNet.System.Text.Json;

namespace Cézanne.Core.Service
{
    public sealed class Jsons
    {
        private static readonly IDeserializer _YamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new SystemTextJsonYamlTypeConverter())
            .WithTypeInspector(x => new SystemTextJsonTypeInspector(x))
            .Build();

        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // UTF-8
            Converters =
            {
                new JsonPropertyNameEnumConverter<Manifest.AwaitConditionType>(),
                new JsonPropertyNameEnumConverter<Manifest.ConditionOperator>(),
                new JsonPropertyNameEnumConverter<Manifest.ConditionType>(),
                new JsonPropertyNameEnumConverter<Manifest.JsonPointerOperator>()
            }
        };

        public static JsonNode FromYaml(string descriptorContent)
        {
            return _YamlDeserializer.Deserialize<JsonNode>(descriptorContent);
        }
    }

    internal class JsonPropertyNameEnumConverter<T> : JsonConverter<T> where T : struct
    {
        private readonly Dictionary<T, string> _code2Json;
        private readonly Dictionary<string, T> _json2code;

        public JsonPropertyNameEnumConverter()
        {
            _code2Json = typeof(T)
                .GetMembers(BindingFlags.Public | BindingFlags.Static)
                .ToDictionary(x => Enum.Parse<T>(x.Name),
                    x => x.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ??
                         JsonNamingPolicy.CamelCase.ConvertName(x.Name));
            _json2code = _code2Json.ToDictionary(x => x.Value, x => x.Key);
        }

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string key = reader.GetString()!;
            return _json2code.TryGetValue(key, out T res)
                ? res
                : throw new ArgumentException("Invalid json value for enum {key}");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(_code2Json[value]!);
        }
    }
}