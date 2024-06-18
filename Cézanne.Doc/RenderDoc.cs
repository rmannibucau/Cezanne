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
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Cézanne.Doc
{
    public static class RenderDoc
    {
        private static async Task Main(string[] args)
        {
            string baseDir = Path.GetFullPath($"{AppDomain.CurrentDomain.BaseDirectory}/../../..");
            string docfxConf = $"{baseDir}/docfx.json";

            using ILoggerFactory loggerFactory = LoggerFactory.Create(b => b.AddConsole());
            ILogger logger = loggerFactory.CreateLogger(typeof(RenderDoc));

            _RunPreActions(baseDir, logger);

            await _DoRender(logger, docfxConf);

            if ((args.Length > 0 && bool.Parse(args[0])) ||
                bool.Parse(Environment.GetEnvironmentVariable("DOC_SERVE") ?? "false"))
            {
                await using WebApplication app = _Serve(logger, $"{baseDir}/_site");

                async void OnChange()
                {
                    await _DoRender(logger, docfxConf);
                }

                using FileSystemWatcher watcher = _Watch(baseDir, OnChange);

                await app.WaitForShutdownAsync();
            }
        }

        private static void _RunPreActions(string baseDir, ILogger logger)
        {
            Type manifestType = typeof(Manifest);
            _GenerateJsonSchema(
                manifestType,
                $"{baseDir}/docs/generated/schema/manifest.jsonschema.json",
                logger,
                (type, property) => type == manifestType && property.Name == "Alveoli");

            _GenerateEnvironmentConfiguration($"{baseDir}/docs/generated/configuration/properties.json", logger);
        }

        private static void _GenerateEnvironmentConfiguration(string output, ILogger logger)
        {
            var md = new List<string>();
            foreach (var type in typeof(Cezanne).Assembly.GetTypes())
            {
                if (type.GetCustomAttributes(typeof(ConfigurationPrefixAttribute), false).Length > 0)
                {
                    md.Add(_GenerateEnvironmentConfigurationFor(type));
                }
            }

            if (md.Count > 0)
            {
                DirectoryInfo? dir = Directory.GetParent(output);
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

        // todo: rmannibucau completion generation
        private static string _GenerateEnvironmentConfigurationFor(Type type)
        {
            var prefix = type.GetCustomAttribute<ConfigurationPrefixAttribute>()!.Value;

            var builder = new StringBuilder();
            builder.Append("## ").Append(new CultureInfo("en-us", false).TextInfo.ToTitleCase(prefix)).Append("\n\n");

            var envVarPrefix = "CEZANNE__" + prefix.ToUpperInvariant() + "__";
            var cliPrefix = "--cezanne:" + prefix + ":";
            var referenceInstance = Activator.CreateInstance(type);
            foreach (var prop in type.GetProperties().Where(it => it.DeclaringType == type).OrderBy(it => it.Name))
            {
                var description = prop.GetCustomAttribute<DescriptionAttribute>()!.Description;
                var defaultValue = prop.GetValue(referenceInstance);
                builder.Append(prop.Name).Append("\n:   _")
                    .Append(description).Append("_\n");
                if (defaultValue is not null)
                {
                    builder.Append("    \n    **Default value:** `").Append(defaultValue).Append("`.\n");
                }
                builder
                    .Append("    \n    **Environment variable name:** `").Append(envVarPrefix).Append(prop.Name.ToUpperInvariant()).Append("`.\n")
                    .Append("    \n    **Command line:** `").Append(cliPrefix).Append(prop.Name).Append("=<value>`.\n")
                    .Append("    \n    **`cezanne.json` sample:**\n")
                    .Append("    ````json\n")
                    .Append("    {\n")
                    .Append("      \"cezanne\": {\n")
                    .Append("        \"").Append(prefix).Append("\": {\n")
                    .Append("          \"").Append(prop.Name).Append("\": \"<value>\"\n")
                    .Append("        }\n")
                    .Append("      }\n")
                    .Append("    }\n")
                    .Append("    ````\n")
                    .Append('\n');
            }

            return builder.Append('\n').ToString();
        }

        private static void _GenerateJsonSchema(Type type, string output, ILogger logger,
            Func<Type, PropertyInfo, bool> ignores)
        {
            JsonSchemaGenerator jsonSchemaGenerator = new();
            JsonSchema.JsonSchema schema = jsonSchemaGenerator.For(type, ignores);

            DirectoryInfo? dir = Directory.GetParent(output);
            if (dir?.Exists is false)
            {
                Directory.CreateDirectory(dir.FullName);
            }

            File.WriteAllText(output, JsonSerializer.Serialize(schema, jsonSchemaGenerator.JsonOptions));
            logger.LogInformation("Wrote '{output}'", output);
        }

        private static FileSystemWatcher _Watch(string baseDir, Action onChange)
        {
            FileSystemWatcher watcher = new(baseDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.Attributes | NotifyFilters.Size | NotifyFilters.FileName |
                               NotifyFilters.DirectoryName | NotifyFilters.LastWrite
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
            string url = "http://localhost:8080";
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls(url);
            WebApplication app = builder.Build();
            app.UseFileServer(new FileServerOptions
            {
                StaticFileOptions = { ServeUnknownFileTypes = true },
                EnableDirectoryBrowsing = true,
                FileProvider = new PhysicalFileProvider(docBase)
            });
            logger.LogInformation($"Doc available at '{url}'.", url);
            app.Start();
            return app;
        }

        private static async Task _DoRender(ILogger logger, string directory)
        {
            logger.LogInformation($"Rendering '{directory}'", directory);
            await Docset.Build(
                directory,
                new BuildOptions
                {
                    ConfigureMarkdig = p => p
                        .UseAbbreviations()
                        .UseFootnotes()
                        .UseGridTables()
                        .UseDefinitionLists()
                });
        }
    }
}