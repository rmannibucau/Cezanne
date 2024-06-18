using Cézanne.Core.Service;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Cézanne.Core.Tests.Service
{
    public class ContainerSanitizerTests
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        private readonly ContainerSanitizer _sanitizer = new(new Logger<ContainerSanitizer>(new NullLoggerFactory()));

        [Test]
        public void NoResources()
        {
            _AssertSanitization("pods", "{\"spec\":{\"containers\":[]}}", "{\"spec\":{\"containers\":[]}}");
        }

        [Test]
        public void EmptyResources()
        {
            _AssertSanitization("pods", "{\"spec\":{\"containers\":[{\"resources\":{}}]}}",
                "{\"spec\":{\"containers\":[{\"resources\":{}}]}}");
        }

        [Test]
        public void CpuOk()
        {
            _AssertSanitization(
                "pods", "{\"spec\":{\"containers\":[{\"resources\":{\"requests\":{\"cpu\":1}}}]}}",
                "{\"spec\":{\"containers\":[{\"resources\":{\"requests\":{\"cpu\":1}}}]}}");
        }

        [Test]
        public void CpuNull()
        {
            _AssertSanitization(
                "pods", "{\"spec\":{\"containers\":[{\"resources\":{\"requests\":{\"cpu\":null}}}]}}",
                "{\"spec\":{\"containers\":[{\"resources\":{\"requests\":{}}}]}}");
        }

        [Test]
        public void CpuNullWithMemory()
        {
            _AssertSanitization(
                "pods",
                "{\"spec\":{\"containers\":[{\"resources\":{\"requests\":{\"cpu\":null,\"memory\":\"512Mi\"}}}]}}",
                "{\"spec\":{\"containers\":[{\"resources\":{\"requests\":{\"memory\":\"512Mi\"}}}]}}");
        }

        private void _AssertSanitization(string kind, string input, string output)
        {
            Assert.That(
                JsonSerializer.Serialize(
                    _sanitizer.DropCpuResources(kind, JsonSerializer.Deserialize<JsonObject>(input, Options)!),
                    Options),
                Is.EqualTo(output));
        }
    }
}