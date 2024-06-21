using Cézanne.Core.Interpolation;
using Cézanne.Core.Service;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Cézanne.Core.Cli.Command
{
    public class
        PlaceholderExtractorCommand : CollectingCommand<PlaceholderExtractorCommand,
        PlaceholderExtractorCommand.Settings>
    {
        [TypeConverter(typeof(OutputTypeConverter))]
        public enum OutputType
        {
            [Description("Just log the output - will be on the standard output using logging framework.")]
            LOG,

            [Description("Write the output to a file.")]
            FILE,

            [Description("Write the output to the standard output.")]
            CONSOLE,

            [Description("Use an output formatting ArgoCD understands.")]
            ARGOCD
        }

        private readonly ILogger<PlaceholderExtractorCommand> _logger;
        private readonly Substitutor _substitutor;

        public PlaceholderExtractorCommand(
            ILogger<PlaceholderExtractorCommand> logger,
            ArchiveReader archiveReader, RecipeHandler recipeHandler,
            Substitutor substitutor) :
            base(logger, archiveReader, recipeHandler, "Inspecting", "inspected")
        {
            _substitutor = substitutor;
            _logger = logger;
        }

        public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
        {
            var placeholders = new List<VisitedPlaceholder>();
            var captureResult = await _Capture(placeholders, context, settings);
            if (captureResult != 0)
            {
                return captureResult;
            }

            var aggregatedPlaceholders = placeholders
                .GroupBy(p => p.Name)
                .Select(it =>
                {
                    var defaultValues =
                        it.Where(p => p.DefaultValue is not null).Select(p => p.DefaultValue!).ToList() ?? [];
                    if (defaultValues.Count > 0 && defaultValues.Count == it.Count() &&
                        defaultValues.Distinct().Count() == defaultValues.Count)
                    {
                        // unique default value, just promote it, else kepp all potential values
                        return new Placeholder(it.Key, defaultValues[0], defaultValues);
                    }

                    return new Placeholder(it.Key, null, defaultValues);
                })
                .ToList();

            _Generate(aggregatedPlaceholders, settings);

            return 0;
        }

        private void _Generate(IList<Placeholder> placheholders, Settings settings)
        {
            var descriptions = new Dictionary<string, string>();
            if (settings.Descriptions is not null)
            {
                foreach (var descriptionFile in settings.Descriptions!.Split(','))
                {
                    _LoadProperties(descriptionFile, descriptions);
                }
            }

            switch (settings.OutputType)
            {
                case OutputType.ARGOCD:
                    // must be on stdout - argo uses it, ensure to set CEZANNE_LOG_LEVEL=Error to not pollute the output
                    var jsonModel = placheholders.Select(it => new ArgoCdJsonItemModel(
                        it.Name, it.Name, it.DefaultValues is null || it.DefaultValues.Count == 0,
                        it.DefaultValue is not null
                            ? it.DefaultValue
                            :
                            it.DefaultValues is not null && it.DefaultValues.Count > 0
                                ?
                                string.Join(",", it.DefaultValues)
                                : null,
                        descriptions.TryGetValue(it.Name, out var desc) ? desc : null));
                    Console.WriteLine(JsonSerializer.Serialize(jsonModel, Jsons.Options));
                    break;
                default:
                    _DoHandleFiles(settings, placheholders, descriptions);
                    break;
            }
        }

        private void _DoHandleFiles(Settings settings, IList<Placeholder> placheholders,
            IDictionary<string, string> descriptions)
        {
            if (settings.JsonFilename is not null and not "skip")
            {
                _DoWrite(settings, "JSON", () => Path.Combine(settings.DumpLocation ?? "", settings.JsonFilename), () =>
                {
                    var model = new JsonModel
                    {
                        Items = placheholders
                            .Select(it => new JsonItemModel(
                                it.Name,
                                descriptions.TryGetValue(it.Name, out var desc) ? desc! : "",
                                it.DefaultValues is null || it.DefaultValues.Count == 0)
                            {
                                DefaultValue = it.DefaultValue, DefaultValues = it.DefaultValues
                            })
                            .ToList()
                    };
                    return JsonSerializer.Serialize(model, Jsons.Options);
                });
            }

            if (settings.PropertiesFilename is not null and not "skip")
            {
                _DoWrite(
                    settings, "Sample",
                    () => Path.Combine(settings.DumpLocation ?? "", settings.PropertiesFilename),
                    () => string.Join("\n\n", placheholders.Select(p =>
                    {
                        var key = p.Name;
                        var desc = descriptions.TryGetValue(key, out var d) ? d : key;
                        var defaultValue = p.DefaultValue;
                        var help = desc != key && !string.IsNullOrWhiteSpace(desc)
                            ? "# HELP: " + desc.Replace("\n", "\n# HELP: ") + "\n"
                            : "";
                        var sampleValue = p switch
                        {
                            { DefaultValue: not null } => _FormatSampleDefault(p.DefaultValue),
                            { DefaultValues: not null } => string.Join(" OR ",
                                p.DefaultValues.Select(_FormatSampleDefault)),
                            _ => "-"
                        };
                        return $"{help}#{key} = {sampleValue}";
                    })) + "\n");
            }

            if (settings.CompletionFilename is not null and not "skip")
            {
                _DoWrite(
                    settings, "Completion",
                    () => Path.Combine(settings.DumpLocation ?? "", settings.CompletionFilename),
                    () => string.Join("\n\n", placheholders.Select(p =>
                    {
                        var desc = descriptions.TryGetValue(p.Name, out var d) ? d : p.Name;
                        return $"{p.Name} = {desc.Replace("\n", "\\n")}";
                    })) + "\n");
            }

            if (settings.ADocFilename is not null and not "skip")
            {
                _DoWrite(
                    settings, "Asciidoc",
                    () => Path.Combine(settings.DumpLocation ?? "", settings.ADocFilename),
                    () => _FormatDoc(settings, descriptions, placheholders, (p, required, desc) =>
                    {
                        var formattedDesc = desc is null ? "" : '\n' + desc.Trim() + '\n';
                        var requiredMarker = p.DefaultValue is null ? "*" : "";
                        return
                            $"`{p.Name}`{requiredMarker}::{formattedDesc}{_FormatAdocDefault(p.Name, p.DefaultValue)}";
                    }));
            }

            if (settings.MdDocFilename is not null and not "skip")
            {
                _DoWrite(
                    settings, "Markdown",
                    () => Path.Combine(settings.DumpLocation ?? "", settings.MdDocFilename),
                    () => _FormatDoc(settings, descriptions, placheholders, (p, required, desc) =>
                    {
                        var requiredMarker = p.DefaultValue is null ? "*" : "";
                        var formattedDesc = desc is not null && desc.Length > 0
                            ? $"   {desc.Trim().Replace("\n", "\n    ")}"
                            : "-";
                        var example = _FormatMdDefault(p.Name, p.DefaultValue);
                        if (example.Length > 0)
                        {
                            example = "    \n    " + example.Trim().Replace("\n", "\n    ") + '\n';
                        }

                        return $"`{p.Name}`{requiredMarker}\n:{formattedDesc}\n{example}";
                    }));
            }
        }

        private string _FormatDoc(
            Settings settings, IDictionary<string, string> descriptions, IList<Placeholder> placheholders,
            Func<Placeholder, bool, string?, string> formatter)
        {
            var missing = new HashSet<string>();
            var adoc = string.Join("\n\n", placheholders.Select(it =>
            {
                var key = it.Name;
                var desc = descriptions.TryGetValue(it.Name, out var d) ? d : null;
                if (desc is null)
                {
                    missing.Add(it.Name);
                }

                var formattedDesc = desc is null ? "" : "\n" + desc.Trim();
                return formatter(it, it.DefaultValue is null, desc);
            }));
            if (settings.FailOnInvalidDescription && missing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Missing placeholder descriptors for\n{string.Join("\n", missing.OrderBy(it => it))}");
            }

            return adoc;
        }

        private string _FormatMdDefault(string key, string? defaultValue)
        {
            if (defaultValue is null)
            {
                return "";
            }

            var sample = defaultValue.Contains('\n') || key.StartsWith("bundlebee-json-inline-file:")
                ? "````json\n" +
                  (key.StartsWith("bundlebee-json-inline-file:")
                      ? JsonSerializer.Deserialize<JsonValue>('"' + defaultValue + '"', new JsonSerializerOptions
                      {
                          WriteIndented = true,
                          PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                          DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                          Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping // UTF-8
                      })!.ToString()
                      : defaultValue) + '\n' +
                  "````\n"
                : $" `{defaultValue}`.";
            return $"Default:{sample}\n";
        }

        private string _FormatAdocDefault(string key, string? defaultValue)
        {
            if (defaultValue is null)
            {
                return "";
            }

            var sample = defaultValue.Contains('\n') || key.StartsWith("bundlebee-json-inline-file:")
                ? "\n[example%collapsible]\n" +
                  "====\n" +
                  "[source]\n" +
                  "----\n" +
                  (key.StartsWith("bundlebee-json-inline-file:")
                      ?
                      // unescape json if needed
                      JsonSerializer.Deserialize<JsonValue>('"' + defaultValue + '"', new JsonSerializerOptions
                      {
                          WriteIndented = true,
                          PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                          DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                          Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping // UTF-8
                      })!.ToString()
                      : defaultValue) + '\n' +
                  "----\n" +
                  "====\n"
                : $" `{defaultValue}`.";
            return $"Default:{sample}";
        }

        private string _FormatSampleDefault(string defaultValue)
        {
            if (defaultValue.Contains('\n'))
            {
                return defaultValue.Replace("\n", "\\\n");
            }

            return defaultValue;
        }

        private void _DoWrite(Settings settings, string what, Func<string> location, Func<string> contentProvider)
        {
            switch (settings.OutputType)
            {
                case OutputType.FILE:
                    var output = location();
                    Directory.GetParent(output)?.Create();
                    File.WriteAllText(output, contentProvider());
                    _logger.LogInformation("Generated {Type} '{File}'", what, output);
                    break;
                case OutputType.CONSOLE:
                    AnsiConsole.MarkupLine(contentProvider());
                    break;
                default:
                    _logger.LogInformation("[{Type}] {Output}", what, contentProvider());
                    break;
            }
        }

        private void _LoadProperties(string descriptionFile, Dictionary<string, string> descriptions)
        {
            using var reader = new StreamReader(File.OpenRead(descriptionFile));
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.EndsWith('\\')) // trivial multiline support
                {
                    var buffer = new StringBuilder(line);
                    while ((line = reader.ReadLine()) is not null && line.EndsWith('\\'))
                    {
                        buffer.Append(line[..^1].Trim());
                    }

                    if (line is not null)
                    {
                        buffer.Append(line.Trim());
                    }

                    line = buffer.ToString();
                    // let it be parsed as if it was current line
                }

                var sep = line.IndexOf('=');
                if (sep > 0)
                {
                    descriptions.Add(line[..sep].Trim(), line[(sep + 1)..].Trim());
                }
            }
        }

        private async Task<int> _Capture(IList<VisitedPlaceholder> placeholders, CommandContext context,
            Settings settings)
        {
            var id = Guid.NewGuid().ToString();
            Action<string, string?, string?> onLookup = (key, defaultValue, resolved) =>
            {
                if (settings.IgnoredPlaceholders is not null &&
                    settings.IgnoredPlaceholders.Any(it =>
                        it == key || (it.EndsWith(".*") && key.StartsWith(it[..^2]))))
                {
                    return;
                }

                lock (placeholders) { placeholders.Add(new VisitedPlaceholder(key, defaultValue, resolved)); }
            };

            var listeners = _substitutor.LookupListeners.AddOrUpdate(id, k => [], (a, b) => b);
            lock (listeners)
            {
                listeners.Add(onLookup);
            }

            try
            {
                var result = await base.DoExecuteAsync(id, context, settings);
                if (result != 0)
                {
                    return result;
                }
            }
            finally
            {
                _substitutor.LookupListeners.Remove(id, out var _);
            }

            return 0;
        }


        public class Settings : CollectorSettings
        {
            [Description(
                "How to dump the placeholders, by default (`LOG`) it will print it but `FILE` will store it in a local file (using `dumpLocation`).")]
            [CommandOption("-t|--output-type")]
            [DefaultValue("CONSOLE")]
            public OutputType OutputType { get; set; }

            [Description("Extraction location (directory) when `outputType` is `FILE`.")]
            [CommandOption("-o|--dump")]
            [DefaultValue("out/bundlebee_extract")]
            public string? DumpLocation { get; set; }

            [Description(
                "Properties filename (relative to `dumpLocation`) when `outputType` is `FILE`. Ignores properties extraction if value is `skip`.")]
            [CommandOption("--properties")]
            [DefaultValue("placeholders.properties")]
            public string? PropertiesFilename { get; set; }

            [Description(
                "JSON filename (relative to `dumpLocation`) when `outputType` is `FILE`. Ignores JSON dump if value is `skip`.")]
            [CommandOption("--json")]
            [DefaultValue("placeholders.json")]
            public string? JsonFilename { get; set; }

            [Description(
                "Completion properties filename - see https://github.com/rmannibucau/vscode-properties-custom-completion - (relative to `dumpLocation`) when `outputType` is `FILE`. Ignores this extraction if value is `skip`.")]
            [CommandOption("--completion")]
            [DefaultValue("placeholders.completion.properties")]
            public string? CompletionFilename { get; set; }

            [Description(
                "Asciidoc filename (relative to `dumpLocation`) when `outputType` is `FILE`. Ignores this extraction if value is `skip`.")]
            [CommandOption("--asciidoc")]
            [DefaultValue("placeholders.adoc")]
            public string? ADocFilename { get; set; }

            [Description(
                "Markdown filename (relative to `dumpLocation`) when `outputType` is `FILE`. Ignores this extraction if value is `skip`. Note it uses definition lists so it requires `.UseDefinitionLists()` for `docfx`/Markdig.")]
            [CommandOption("--markdown")]
            [DefaultValue("placeholders.md")]
            public string? MdDocFilename { get; set; }

            [Description(
                "Properties (WARN: it is read line by line and not as java properties) file locations which contain key=the placeholder and value=the placeholder description.")]
            [CommandOption("--descriptions")]
            [DefaultValue("bundlebee/descriptions.properties")]
            public string? Descriptions { get; set; }

            [Description(
                "List of placeholders or prefixes (ended with `.*`) to ignore. This is common for templates placeholders which don't need documentation since they are wired in the manifest in general.")]
            [CommandOption("-x|--ignored-placeholders")]
            [DefaultValue("service..*")]
            public IList<string>? IgnoredPlaceholders { get; set; }

            [Description("Should documentation generation fail on missing/unexpected placeholder description.")]
            [CommandOption("-e|--fail-on-invalid")]
            [DefaultValue("true")]
            public bool FailOnInvalidDescription { get; set; }
        }

        public class OutputTypeConverter : TypeConverter
        {
            public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
            {
                return sourceType == typeof(OutputType) || base.CanConvertFrom(context, sourceType);
            }

            public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
            {
                var s = value as string;
                return s is not null ? Enum.Parse<OutputType>(s) : base.ConvertFrom(context, culture, value);
            }

            public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value,
                Type destinationType)
            {
                var casted = value as OutputType?;
                if (casted.HasValue && destinationType == typeof(string))
                {
                    return Enum.GetName(casted.Value);
                }

                return base.ConvertTo(context, culture, value, destinationType);
            }
        }

        protected record VisitedPlaceholder(string Name, string? DefaultValue, string? ResolvedValue)
        {
        }

        protected record Placeholder(string Name, string? DefaultValue, IList<string> DefaultValues)
        {
        }

        public class JsonModel
        {
            public IList<JsonItemModel>? Items { get; init; }
        }

        public class JsonItemModel
        {
            public JsonItemModel(string name, string description, bool required)
            {
                (Name, Description, Required) = (name, description, required);
            }

            public string Name { get; }

            public string Description { get; }
            public bool Required { get; }

            public string? DefaultValue { get; init; }
            public IList<string>? DefaultValues { get; init; }
        }

        public class ArgoCdJsonItemModel
        {
            public ArgoCdJsonItemModel(string name, string title, bool required, string? defaultValue, string? tooltip)
            {
                (Name, Title, Required, DefaultValue, Tooltip) = (name, title, required, defaultValue, tooltip);
            }

            public string Name { get; }
            public string Title { get; }
            public bool Required { get; }

            [JsonPropertyName("string")] public string? DefaultValue { get; }

            public string? Tooltip { get; }
        }
    }
}