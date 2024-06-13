using Cézanne.Core.Descriptor;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Cézanne.Core.Tests.Descriptor
{
    public class ManifestTests
    {
        private readonly JsonSerializerOptions? _options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // UTF-8
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false) }
        };

        [TestCaseSource(nameof(FromJsonDataSet))]
        public Manifest? FromJson(string json)
        {
            return JsonSerializer.Deserialize<Manifest>(json, _options);
        }

        private static IEnumerable<TestCaseData> FromJsonDataSet()
        {
            yield return new TestCaseData(
                    "{\"interpolateRecipe\": false,\"references\": [],\"requirements\": [],\"recipes\": []}")
                .Returns(new Manifest());
            yield return new TestCaseData(
                    "{\"interpolateRecipe\": true,\"references\": [],\"requirements\": [],\"recipes\": []}")
                .Returns(new Manifest { InterpolateRecipe = true });
            yield return new TestCaseData(
                    "{\"interpolateRecipe\": false,\"references\": [{\"path\":\"bar/mf.json\"}],\"requirements\": [],\"recipes\": []}")
                .Returns(new Manifest { References = [new Manifest.ManifestReference { Path = "bar/mf.json" }] });
            yield return new TestCaseData(
                    "{\"interpolateRecipe\": false,\"references\": [],\"requirements\": [{\"minBundlebeeVersion\":\"1.0.1\"}],\"recipes\": []}")
                .Returns(new Manifest { Requirements = [new Manifest.Requirement { MinBundlebeeVersion = "1.0.1" }] });
            yield return new TestCaseData(
                    "{\"interpolateRecipe\": false,\"references\": [],\"requirements\": [],\"recipes\": [{\"descriptors\":[{\"name\":\"test\"}]}]}")
                .Returns(new Manifest
                {
                    Recipes = [new Manifest.Recipe { Descriptors = [new Manifest.Descriptor { Name = "test" }] }]
                });
            yield return new TestCaseData(
                    "{\"interpolateRecipe\": false,\"references\": [],\"requirements\": [],\"recipes\": [{\"descriptors\":[{\"name\":\"test\"}],\"patches\":[{\"descriptorName\":\"foo\",\"patch\":[{\"op\":\"add\",\"path\":\"/foo\",\"value\":1}]}]}]}")
                .Returns(new Manifest
                {
                    Recipes =
                    [
                        new Manifest.Recipe
                        {
                            Descriptors = [new Manifest.Descriptor { Name = "test" }],
                            Patches =
                            [
                                new Manifest.Patch
                                {
                                    DescriptorName = "foo",
                                    PatchValue = new JsonArray(
                                        new JsonObject([
                                            new KeyValuePair<string, JsonNode?>("op", "add"),
                                            new KeyValuePair<string, JsonNode?>("path", "/foo"),
                                            new KeyValuePair<string, JsonNode?>("value", 1)
                                        ])
                                    )
                                }
                            ]
                        }
                    ]
                });

            // bundlebee compatibility
            yield return new TestCaseData(
                    "{\"interpolateRecipe\": false,\"references\": [],\"requirements\": [],\"alveoli\": [{\"descriptors\":[{\"name\":\"test\"}]}]}")
                .Returns(new Manifest
                {
                    Recipes = [new Manifest.Recipe { Descriptors = [new Manifest.Descriptor { Name = "test" }] }]
                });
        }
    }
}