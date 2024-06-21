using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Xml.Serialization;
using Cézanne.Core.Cli;
using Cézanne.Core.Descriptor;
using Cézanne.Core.Lang;
using Cézanne.Doc.JsonSchema;
using Docfx;
using Markdig;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Cézanne.Doc
{
    public static class RenderDoc
    {
        private static async Task Main(string[] args)
        {
            var baseDir = Path.GetFullPath($"{AppDomain.CurrentDomain.BaseDirectory}/../../..");
            var docfxConf = $"{baseDir}/docfx.json";

            using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
            var logger = loggerFactory.CreateLogger(typeof(RenderDoc));

            _RunPreActions(baseDir, logger);

            await _DoRender(logger, docfxConf);

            if (
                (args.Length > 0 && bool.Parse(args[0]))
                || bool.Parse(Environment.GetEnvironmentVariable("DOC_SERVE") ?? "false")
            )
            {
                await using var app = _Serve(logger, $"{baseDir}/_site");

                async void OnChange()
                {
                    await _DoRender(logger, docfxConf);
                }

                using var watcher = _Watch(baseDir, OnChange);

                await app.WaitForShutdownAsync();
            }
        }

        // todo: likely replace it by something like Markdig.Extensions.ScriptCs
        private static void _RunPreActions(string baseDir, ILogger logger)
        {
            var manifestType = typeof(Manifest);
            _GenerateJsonSchema(
                manifestType,
                $"{baseDir}/docs/generated/schema/manifest.jsonschema.json",
                logger,
                (type, property) => type == manifestType && property.Name == "Alveoli"
            );

            _GenerateEnvironmentConfiguration(
                $"{baseDir}/docs/generated/configuration/properties.md",
                logger
            );
            _GenerateCommands(
                $"{baseDir}/docs/generated/commands/list.md",
                $"{baseDir}/docs/commands",
                logger
            );
        }

        private static void _GenerateCommands(
            string output,
            string commandBaseOutput,
            ILogger logger
        )
        {
            var cezanne = new Cezanne();
            cezanne.Run(
                [],
                (app, ioc) =>
                {
                    var writer = new StringWriter();
                    try
                    {
                        app.Configure(conf =>
                            conf.ConfigureConsole(
                                AnsiConsole.Create(
                                    new AnsiConsoleSettings
                                    {
                                        Ansi = AnsiSupport.No,
                                        ColorSystem = ColorSystemSupport.NoColors,
                                        Interactive = InteractionSupport.No,
                                        Out = new AnsiConsoleOutput(writer)
                                    }
                                )
                            )
                        );

                        var code = app.Run(["cli", "xmldoc"]);
                        if (code != 0)
                        {
                            throw new InvalidOperationException(
                                $"Invalid xmlddoc result {code}:\n{writer}"
                            );
                        }
                    }
                    finally
                    {
                        writer.Dispose();
                    }

                    XmlDocModel model;
                    using (var reader = new StringReader(writer.ToString()))
                    {
                        model = (XmlDocModel)
                            new XmlSerializer(typeof(XmlDocModel)).Deserialize(reader)!;
                    }

                    var dir = Directory.GetParent(output);
                    if (dir?.Exists is false)
                    {
                        Directory.CreateDirectory(dir.FullName);
                    }

                    var index = string.Join(
                        '\n',
                        (model.Command ?? [])
                            .OrderBy(it => it.Name)
                            .Select(it =>
                                $"* [`{it.Name}`](~/docs/commands/{it.Name}.md): {it.Description ?? "-"}"
                            )
                    );
                    File.WriteAllText(output, index);
                    logger.LogInformation("Wrote '{output}'", output);

                    // now for each command generate a dedicated page
                    foreach (var command in model.Command ?? [])
                    {
                        var commandDoc =
                            "---\n"
                            + $"uid: command-{command.Name}\n"
                            + "---\n"
                            + "\n"
                            + $"# Command `{command.Name}`\n"
                            + "\n"
                            + $"{command.Description ?? ""}\n"
                            + "\n"
                            + "## Options\n"
                            + "\n"
                            + string.Join(
                                "\n",
                                (command.Parameters ?? [])
                                    .OrderBy(it =>
                                        string.IsNullOrEmpty(it.Long)
                                            ? string.IsNullOrEmpty(it.Short)
                                                ? it.Value
                                                : it.Short
                                            : it.Long
                                    )
                                    .Select(it =>
                                    {
                                        IList<IEnumerable<string>> names =
                                        [
                                            (
                                                !string.IsNullOrEmpty(it.Long)
                                                    ? it.Long.Split(',')
                                                    : []
                                            ).Select(it => $"`--{it}`"),
                                            (
                                                !string.IsNullOrEmpty(it.Short)
                                                    ? it.Short.Split(',')
                                                    : []
                                            ).Select(it => $"`-{it}`")
                                        ];
                                        var builder = new StringBuilder(
                                            string.Join(", ", names.SelectMany(it => it))
                                        );
                                        if (it.Required)
                                        {
                                            builder.Append('*');
                                        }

                                        if (it.Description is not null)
                                        {
                                            builder
                                                .Append("\n:   _")
                                                .Append(it.Description)
                                                .Append("_\n");
                                        }

                                        // needs spectre upgrade
                                        if (it.DefaultValue is not null)
                                        {
                                            builder
                                                .Append("    \n    **Default value:** `")
                                                .Append(it.DefaultValue)
                                                .Append("`.\n");
                                        }

                                        return builder.ToString();
                                    })
                            )
                            + "\n";

                        File.WriteAllText($"{commandBaseOutput}/{command.Name}.md", commandDoc);
                    }

                    return 0;
                }
            );
        }

        private static void _GenerateEnvironmentConfiguration(string output, ILogger logger)
        {
            var md = new List<string>();
            foreach (var type in typeof(Cezanne).Assembly.GetTypes())
            {
                if (
                    type.GetCustomAttributes(typeof(ConfigurationPrefixAttribute), false).Length > 0
                )
                {
                    md.Add(_GenerateEnvironmentConfigurationFor(type));
                }
            }

            if (md.Count > 0)
            {
                var dir = Directory.GetParent(output);
                if (dir?.Exists is false)
                {
                    Directory.CreateDirectory(dir.FullName);
                }

                md.Sort();
                File.WriteAllText(output, string.Join('\n', md));
                logger.LogInformation("Wrote '{output}'", output);
            }
            else
            {
                logger.LogWarning("No [ConfigurationPrefix] found");
            }
        }

        private static string _GenerateEnvironmentConfigurationFor(Type type)
        {
            var prefix = type.GetCustomAttribute<ConfigurationPrefixAttribute>()!.Value;

            var builder = new StringBuilder();
            builder
                .Append("## ")
                .Append(new CultureInfo("en-us", false).TextInfo.ToTitleCase(prefix))
                .Append("\n\n");

            var envVarPrefix = "CEZANNE__" + prefix.ToUpperInvariant() + "__";
            var cliPrefix = "--cezanne:" + prefix + ":";
            var referenceInstance = Activator.CreateInstance(type);
            foreach (
                var prop in type.GetProperties()
                    .Where(it => it.DeclaringType == type)
                    .OrderBy(it => it.Name)
            )
            {
                var description = prop.GetCustomAttribute<DescriptionAttribute>()!.Description;
                var defaultValue = prop.GetValue(referenceInstance);
                builder.Append(prop.Name).Append("\n:   _").Append(description).Append("_\n");
                if (defaultValue is not null)
                {
                    builder
                        .Append("    \n    **Default value:** `")
                        .Append(defaultValue)
                        .Append("`.\n");
                }

                builder
                    .Append("    \n    **Environment variable name:** `")
                    .Append(envVarPrefix)
                    .Append(prop.Name.ToUpperInvariant())
                    .Append("`.\n")
                    .Append("    \n    **Command line:** `")
                    .Append(cliPrefix)
                    .Append(prop.Name)
                    .Append("=<value>`.\n")
                    .Append("    \n    **`cezanne.json` sample:**\n")
                    .Append("    ````json\n")
                    .Append("    {\n")
                    .Append("      \"cezanne\": {\n")
                    .Append("        \"")
                    .Append(prefix)
                    .Append("\": {\n")
                    .Append("          \"")
                    .Append(prop.Name)
                    .Append("\": \"<value>\"\n")
                    .Append("        }\n")
                    .Append("      }\n")
                    .Append("    }\n")
                    .Append("    ````\n")
                    .Append('\n');
            }

            return builder.Append('\n').ToString();
        }

        private static void _GenerateJsonSchema(
            Type type,
            string output,
            ILogger logger,
            Func<Type, PropertyInfo, bool> ignores
        )
        {
            JsonSchemaGenerator jsonSchemaGenerator = new();
            var schema = jsonSchemaGenerator.For(type, ignores);

            var dir = Directory.GetParent(output);
            if (dir?.Exists is false)
            {
                Directory.CreateDirectory(dir.FullName);
            }

            File.WriteAllText(
                output,
                JsonSerializer.Serialize(schema, jsonSchemaGenerator.JsonOptions)
            );
            logger.LogInformation("Wrote '{output}'", output);
        }

        private static FileSystemWatcher _Watch(string baseDir, Action onChange)
        {
            FileSystemWatcher watcher =
                new(baseDir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter =
                        NotifyFilters.Attributes
                        | NotifyFilters.Size
                        | NotifyFilters.FileName
                        | NotifyFilters.DirectoryName
                        | NotifyFilters.LastWrite
                };
            watcher.Filters.Add("*.md");
            watcher.Filters.Add("docfx.json");
            watcher.Filters.Add("toc.yml");
            watcher.Filters.Add("docs/**");
            watcher.Changed += (_, _) => onChange();
            watcher.Created += (_, _) => onChange();
            watcher.Deleted += (_, _) => onChange();
            watcher.Renamed += (_, _) => onChange();
            watcher.EnableRaisingEvents = true;
            return watcher;
        }

        private static WebApplication _Serve(ILogger logger, string docBase)
        {
            var url = "http://localhost:8080";
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls(url);
            var app = builder.Build();
            app.UseFileServer(
                new FileServerOptions
                {
                    StaticFileOptions = { ServeUnknownFileTypes = true },
                    EnableDirectoryBrowsing = true,
                    FileProvider = new PhysicalFileProvider(docBase)
                }
            );
            logger.LogInformation("Doc available at '{url}'.", url);
            app.Start();
            return app;
        }

        private static async Task _DoRender(ILogger logger, string directory)
        {
            logger.LogInformation("Rendering '{Directory}'", directory);
            await Docset.Build(
                directory,
                new BuildOptions
                {
                    ConfigureMarkdig = p =>
                        p.UseAbbreviations().UseFootnotes().UseGridTables().UseDefinitionLists()
                }
            );
        }
    }

    [XmlRoot(ElementName = "Model")]
    public class XmlDocModel
    {
        [XmlElement(ElementName = "Command")]
        public List<XmlCommand>? Command { get; set; }
    }

    public class XmlOption
    {
        [XmlAttribute("ClrType")]
        public string? ClrType { get; set; }

        [XmlElement("DefaultValue")]
        public string? DefaultValue { get; set; }

        [XmlElement("Description")]
        public string? Description { get; set; }

        [XmlAttribute("Kind")]
        public string? Kind { get; set; }

        [XmlAttribute("Long")]
        public string? Long { get; set; }

        [XmlAttribute("Required")]
        public bool Required { get; set; }

        [XmlAttribute("Short")]
        public string? Short { get; set; }

        [XmlAttribute("Value")]
        public string? Value { get; set; }
    }

    public class XmlCommand
    {
        [XmlElement("ClrType")]
        public string? ClrType { get; set; }

        [XmlElement("Description")]
        public string? Description { get; set; }

        [XmlElement("IsBranch")]
        public bool? IsBranch { get; set; }

        [XmlAttribute("Name")]
        public string? Name { get; set; }

        [XmlArray("Parameters")]
        [XmlArrayItem("Option")]
        public List<XmlOption>? Parameters { get; set; }

        [XmlElement("Settings")]
        public string? Settings { get; set; }
    }
}
