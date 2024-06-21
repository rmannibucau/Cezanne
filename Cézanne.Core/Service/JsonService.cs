using System.Diagnostics.CodeAnalysis;
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
