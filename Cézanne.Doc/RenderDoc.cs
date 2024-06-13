using Cézanne.Core.Descriptor;
using Cézanne.Doc.JsonSchema;
using Docfx;
using Markdig;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
            _GenerateJsonSchema(typeof(Manifest), $"{baseDir}/docs/generated/schema/manifest.jsonschema.json", logger);
        }

        private static void _GenerateJsonSchema(Type type, string output, ILogger logger)
        {
            JsonSchemaGenerator jsonSchemaGenerator = new();
            JsonSchema.JsonSchema schema = jsonSchemaGenerator.For(type);
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
            FileSystemWatcher watcher = new(baseDir);
            watcher.IncludeSubdirectories = true;
            watcher.NotifyFilter = NotifyFilters.Attributes | NotifyFilters.Size | NotifyFilters.FileName |
                                   NotifyFilters.DirectoryName | NotifyFilters.LastWrite;
            watcher.Filters.Add("index.md");
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
                        .UseBootstrap()
                });
        }
    }
}