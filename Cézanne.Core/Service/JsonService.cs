using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cézanne.Core.Cli.Command;
using Cézanne.Core.Descriptor;
using Cézanne.Core.K8s;
using Json.Patch;
using YamlDotNet.Serialization;
using YamlDotNet.System.Text.Json;

namespace Cézanne.Core.Service
{
    public sealed class Jsons
    {
        [UnconditionalSuppressMessage(
            "AOT",
            "IL3050:Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.",
            Justification = "Using explicit typeconverter/inspector"
        )]
        private static readonly IDeserializer _YamlDeserializer = new DeserializerBuilder()
            .WithTypeConverter(new SystemTextJsonYamlTypeConverter())
            .WithTypeInspector(x => new SystemTextJsonTypeInspector(x))
            .Build();

        public static JsonNode FromYaml(string descriptorContent)
        {
            return _YamlDeserializer.Deserialize<JsonNode>(descriptorContent);
        }
    }

    internal class JsonPropertyNameEnumConverter<T> : JsonConverter<T>
        where T : struct
    {
        private readonly Dictionary<T, string> _code2Json;
        private readonly Dictionary<string, T> _json2code;

        public JsonPropertyNameEnumConverter()
        {
            _code2Json = typeof(T)
                .GetMembers(BindingFlags.Public | BindingFlags.Static)
                .ToDictionary(
                    x => Enum.Parse<T>(x.Name),
                    x =>
                        x.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                        ?? JsonNamingPolicy.CamelCase.ConvertName(x.Name)
                );
            _json2code = _code2Json.ToDictionary(x => x.Value, x => x.Key);
        }

        public override T Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options
        )
        {
            var key = reader.GetString()!;
            return _json2code.TryGetValue(key, out var res)
                ? res
                : throw new ArgumentException("Invalid json value for enum {key}");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(_code2Json[value]!);
        }
    }

    public class CustomCamelCaseEnumConverter<TEnum>()
        : JsonStringEnumConverter<TEnum>(
            JsonNamingPolicy.SnakeCaseUpper /* bundlebee uses java convention */
        )
        where TEnum : struct, Enum { }

    [JsonSourceGenerationOptions(
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        GenerationMode = JsonSourceGenerationMode.Default,
        Converters = [
            typeof(CustomCamelCaseEnumConverter<Manifest.AwaitConditionType>),
            typeof(CustomCamelCaseEnumConverter<Manifest.ConditionOperator>),
            typeof(CustomCamelCaseEnumConverter<Manifest.ConditionType>),
            typeof(CustomCamelCaseEnumConverter<Manifest.JsonPointerOperator>)
        ]
    )]
    // config
    [JsonSerializable(typeof(KubeConfig))]
    [JsonSerializable(typeof(ApiPreloader.APIResourceList))]
    [JsonSerializable(typeof(ApiPreloader.APIResource))]
    // descriptor
    [JsonSerializable(typeof(Manifest))]
    [JsonSerializable(typeof(Manifest.AwaitCondition))]
    [JsonSerializable(typeof(Manifest.AwaitConditions))]
    [JsonSerializable(typeof(Manifest.Condition))]
    [JsonSerializable(typeof(Manifest.Dependency))]
    [JsonSerializable(typeof(Manifest.Descriptor))]
    [JsonSerializable(typeof(Manifest.DescriptorRef))]
    [JsonSerializable(typeof(Manifest.IgnoredLintingRule))]
    [JsonSerializable(typeof(Manifest.ManifestReference))]
    [JsonSerializable(typeof(Manifest.Patch))]
    [JsonSerializable(typeof(Manifest.Recipe))]
    [JsonSerializable(typeof(Manifest.Requirement))]
    [JsonSerializable(typeof(Manifest.AwaitConditionType))]
    [JsonSerializable(typeof(Manifest.ConditionOperator))]
    [JsonSerializable(typeof(Manifest.ConditionType))]
    [JsonSerializable(typeof(Manifest.JsonPointerOperator))]
    // generic
    [JsonSerializable(typeof(JsonElement))]
    [JsonSerializable(typeof(JsonNode))]
    [JsonSerializable(typeof(JsonObject))]
    [JsonSerializable(typeof(JsonArray))]
    [JsonSerializable(typeof(JsonValue))]
    [JsonSerializable(typeof(JsonPatch))]
    // commands
    [JsonSerializable(typeof(PlaceholderExtractorCommand.JsonModel))]
    [JsonSerializable(typeof(PlaceholderExtractorCommand.JsonItemModel))]
    [JsonSerializable(typeof(PlaceholderExtractorCommand.ArgoCdJsonItemModel))]
    [JsonSerializable(typeof(ListRecipesCommand.ItemsWrapper))]
    public partial class CezanneJsonContext : JsonSerializerContext { }
}
