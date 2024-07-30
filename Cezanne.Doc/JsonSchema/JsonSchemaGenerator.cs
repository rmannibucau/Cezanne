using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cezanne.Core.Descriptor;

namespace Cezanne.Doc.JsonSchema
{
    public class JsonSchemaGenerator
    {
        private readonly NullabilityInfoContext _nullabilityInfoContext = new();

        public JsonSerializerOptions JsonOptions =>
            new()
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // UTF-8
                Converters =
                {
                    new JsonStringEnumConverter<Manifest.AwaitConditionType>(
                        JsonNamingPolicy.SnakeCaseUpper
                    ),
                    new JsonStringEnumConverter<Manifest.ConditionOperator>(
                        JsonNamingPolicy.SnakeCaseUpper
                    ),
                    new JsonStringEnumConverter<Manifest.ConditionType>(
                        JsonNamingPolicy.SnakeCaseUpper
                    ),
                    new JsonStringEnumConverter<Manifest.JsonPointerOperator>(
                        JsonNamingPolicy.SnakeCaseUpper
                    ),
                    new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false)
                }
            };

        public bool InstantiateForDefaults { get; init; } = true;

        public JsonSchema For(Type type, Func<Type, PropertyInfo, bool> isIgnored)
        {
            var schema = _GenerateJsonSchema(type, false, isIgnored);
            schema.Schema = "https://json-schema.org/draft/2020-12/schema";
            schema.Title = type.Name;
            schema.Description =
                type.GetCustomAttribute<DescriptionAttribute>()?.Description ?? type.Name;
            return schema;
        }

        private JsonSchema _GenerateJsonSchema(
            Type type,
            bool rootIsNullable,
            Func<Type, PropertyInfo, bool> isIgnored
        )
        {
            JsonSchema schema = new();
            if (rootIsNullable)
            {
                schema.MultipleTypes =
                [
                    JsonSchema.JsonSchemaType.Object,
                    JsonSchema.JsonSchemaType.Null
                ];
            }
            else
            {
                schema.SingleType = JsonSchema.JsonSchemaType.Object;
            }

            schema.Properties = new SortedDictionary<string, JsonSchema>(Comparer<string>.Default);

            object? instance = null;
            if (InstantiateForDefaults)
            {
                instance = Activator.CreateInstance(type);
            }

            IList<string>? required = null;
            foreach (var property in type.GetProperties())
            {
                if (!property.CanRead || isIgnored(type, property))
                {
                    continue;
                }

                var propertyType = property.PropertyType;
                var nullabilityInfo = _nullabilityInfoContext.Create(property);
                var nullable = nullabilityInfo.ReadState is NullabilityState.Nullable;

                var jsonPropertyName = property
                    .GetCustomAttribute<JsonPropertyNameAttribute>()
                    ?.Name;
                var propertyName =
                    jsonPropertyName ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);

                if (nullable)
                {
                    propertyType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
                }
                else
                {
                    if (required == null)
                    {
                        required = [];
                        schema.Required = required;
                    }

                    required.Add(propertyName);
                }

                JsonSchema propertySchema;
                if (propertyType == typeof(string))
                {
                    propertySchema = new JsonSchema();
                    _SetSchemaType(nullable, propertySchema, JsonSchema.JsonSchemaType.String);
                }
                else if (propertyType == typeof(bool))
                {
                    propertySchema = new JsonSchema();
                    _SetSchemaType(nullable, propertySchema, JsonSchema.JsonSchemaType.Boolean);
                }
                else if (propertyType == typeof(JsonArray))
                {
                    propertySchema = new JsonSchema();
                    _SetSchemaType(nullable, propertySchema, JsonSchema.JsonSchemaType.Array);
                    propertySchema.Items = new JsonSchema
                    {
                        MultipleTypes =
                        [
                            JsonSchema.JsonSchemaType.Object,
                            JsonSchema.JsonSchemaType.Null
                        ]
                    };
                }
                else if (propertyType.IsEnum)
                {
                    propertySchema = new JsonSchema
                    {
                        Enum = _EnumValues(propertyType),
                        Description = _EnumDescription(propertyType)
                    };
                    _SetSchemaType(nullable, propertySchema, JsonSchema.JsonSchemaType.String);
                }
                else if (
                    propertyType.IsGenericType && typeof(IEnumerable).IsAssignableFrom(propertyType)
                )
                {
                    var nestedType = propertyType.GetGenericArguments()[0];
                    var nestedIsNullable = Nullable.GetUnderlyingType(propertyType) != null;
                    if (nestedIsNullable)
                    {
                        nestedType = nestedType.GetGenericArguments()[0];
                    }

                    propertySchema = new JsonSchema();
                    _SetSchemaType(nullable, propertySchema, JsonSchema.JsonSchemaType.Array);

                    if (nestedType == typeof(string))
                    {
                        propertySchema.Items = new JsonSchema();
                        _SetSchemaType(
                            nestedIsNullable,
                            propertySchema.Items,
                            JsonSchema.JsonSchemaType.String
                        );
                    }
                    else if (nestedType == typeof(bool))
                    {
                        propertySchema.Items = new JsonSchema();
                        _SetSchemaType(
                            nestedIsNullable,
                            propertySchema.Items,
                            JsonSchema.JsonSchemaType.Boolean
                        );
                    }
                    else if (nestedType.IsEnum)
                    {
                        propertySchema.Items = new JsonSchema
                        {
                            Description = _EnumDescription(propertyType),
                            Enum = _EnumValues(propertyType)
                        };
                        _SetSchemaType(
                            nestedIsNullable,
                            propertySchema.Items,
                            JsonSchema.JsonSchemaType.String
                        );
                    }
                    else
                    {
                        propertySchema.Items = _GenerateJsonSchema(
                            nestedType,
                            nestedIsNullable,
                            isIgnored
                        );
                    }
                }
                else
                {
                    propertySchema = _GenerateJsonSchema(propertyType, nullable, isIgnored);
                }

                schema.Properties[propertyName] = propertySchema;

                schema.Title = _ToTitle(propertyName);
                schema.Description =
                    property.GetCustomAttribute<DescriptionAttribute>()?.Description
                    ?? schema.Description;

                if (instance is not null)
                {
                    var sample = property.GetValue(instance);
                    if (sample is not null)
                    {
                        schema.Default = sample;
                        schema.Example = [sample];
                    }
                }

                if (!property.CanWrite && property.CanRead)
                {
                    schema.ReadOnly = true;
                }
                else if (property.CanWrite && !property.CanRead)
                {
                    schema.WriteOnly = true;
                }
            }

            return schema;
        }

        private IEnumerable<string> _EnumValues(Type propertyType)
        {
            return _GetEnumPropreties(propertyType).Select(it => _EnumName(it));
        }

        private string _EnumName(MemberInfo it)
        {
            return it.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? it.Name;
        }

        private string? _EnumDescription(Type propertyType)
        {
            var prefix = propertyType.GetCustomAttribute<DescriptionAttribute>()?.Description;
            var values = string.Join(
                    ", ",
                    _GetEnumPropreties(propertyType)
                        .Select(it =>
                        {
                            var description =
                                it.GetCustomAttribute<DescriptionAttribute>()?.Description;
                            description =
                                description is not null && description.EndsWith('.')
                                    ? description[..^1]
                                    : description;
                            return description is null ? null : $"{_EnumName(it)} - {description}";
                        })
                        .Where(it => !string.IsNullOrWhiteSpace(it))
                )
                .Trim();
            return (
                prefix ?? "" + (values is not null ? $" Potential values: {values}." : "")
            ).Trim();
        }

        private IEnumerable<MemberInfo> _GetEnumPropreties(Type propertyType)
        {
            return propertyType.GetMembers(BindingFlags.Public | BindingFlags.Static);
        }

        private string _ToTitle(string propertyName)
        {
            var chars = propertyName.ToCharArray();
            StringBuilder result = new((int)(chars.Length * 1.1));
            result.Append(char.ToUpper(chars[0]));
            for (var i = 1; i < chars.Length; i++)
            {
                var c = chars[i];
                if (char.IsUpper(c))
                {
                    result.Append(' ');
                }

                result.Append(c);
            }

            return result.ToString();
        }

        private void _SetSchemaType(
            bool nullable,
            JsonSchema propertySchema,
            JsonSchema.JsonSchemaType type
        )
        {
            if (nullable)
            {
                propertySchema.MultipleTypes = [type, JsonSchema.JsonSchemaType.Null];
            }
            else
            {
                propertySchema.SingleType = type;
            }
        }
    }
}
