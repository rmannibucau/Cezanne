using System.Text.Json.Serialization;

namespace Cézanne.Doc.JsonSchema
{
    public class JsonSchema
    {
        public enum JsonSchemaType
        {
            [JsonPropertyName("object")]
            Object,

            [JsonPropertyName("array")]
            Array,

            [JsonPropertyName("string")]
            String,

            [JsonPropertyName("number")]
            Number,

            [JsonPropertyName("integer")]
            Integer,

            [JsonPropertyName("boolean")]
            Boolean,

            [JsonPropertyName("null")]
            Null
        }

        [JsonPropertyName("$schema")]
        public string? Schema { get; set; }

        public string? Title { get; set; }
        public string? Description { get; set; }

        [JsonPropertyName("$id")]
        public string? Id { get; set; }

        [JsonPropertyName("$ref")]
        public string? Ref { get; set; }

        [JsonPropertyName("$defs")]
        public IEnumerable<JsonSchema>? Defs { get; set; }

        public object? Type { get; set; }

        public IDictionary<string, JsonSchema>? Properties { get; set; }
        public JsonSchema? Items { get; set; }
        public IEnumerable<object>? Enum { get; set; }
        public IEnumerable<string>? Required { get; set; }

        public JsonSchemaType? SingleType
        {
            set => Type = value;
        }

        public IEnumerable<JsonSchemaType>? MultipleTypes
        {
            set => Type = value;
        }
    }
}
