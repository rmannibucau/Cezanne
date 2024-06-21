using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Cézanne.Core.Cli.Progress;
using Cézanne.Core.Descriptor;
using Cézanne.Core.Service;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Cézanne.Core.Cli.Command
{
    public class ListRecipesCommand(ILogger<ListRecipesCommand> logger, RecipeHandler recipeHandler)
        : AsyncCommand<ListRecipesCommand.Settings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
        {
            var id = Guid.NewGuid().ToString();

            var manifest = await AnsiConsole
                .Progress()
                .AutoClear(true)
                .HideCompleted(false)
                .StartAsync(async ctx =>
                {
                    logger.LogTrace(
                        "Looking up recipe id='{Id}' location='{Location}' manifest='{Manifest}'",
                        id,
                        settings.From,
                        settings.Manifest
                    );
                    return await recipeHandler.FindManifest(
                            settings.From,
                            settings.Manifest,
                            id,
                            new ProgressHandler(ctx).OnProgress
                        )
                        ?? throw new InvalidOperationException(
                            "Didn't find the requested manifest"
                        );
                });

            switch (settings.Output)
            {
                case "logger":
                    logger.LogInformation("{AlveoliList}", _AsText(manifest));
                    break;
                case "logger.json":
                    logger.LogInformation("{AlveoliJsonList}", _AsJson(manifest));
                    break;
                default:
                    var file = new FileInfo(settings.Output!);
                    Directory.GetParent(file.FullName)?.Create();
                    await using (var stream = file.OpenWrite())
                    {
                        if (settings.Output!.EndsWith(".json"))
                        {
                            stream.Write(Encoding.UTF8.GetBytes(_AsJson(manifest)));
                        }
                        else
                        {
                            stream.Write(Encoding.UTF8.GetBytes(_AsText(manifest)));
                        }
                    }

                    break;
            }

            return 0;
        }

        private string _AsJson(Manifest manifest)
        {
            return JsonSerializer.Serialize(
                new ItemsWrapper { Items = manifest.Recipes.Select(it => it.Name!).ToList() },
                CezanneJsonContext.Default.ItemsWrapper
            );
        }

        private string _AsText(Manifest manifest)
        {
            if (!manifest?.Recipes.Any() ?? false)
            {
                return "No recipe found.";
            }

            return "Found recipes:\n"
                + string.Join(
                    '\n',
                    manifest!
                        .Recipes.OrderBy(it => it.Name)
                        .Select(it =>
                            $"- {it.Name}"
                            + (it.Description is not null ? $": {it.Description}" : "")
                        )
                );
        }

        public class Settings : CommandSettings
        {
            [Description(
                "Manifest to load to start to lookup (a file path or inline). This optional setting mainly enables to use dependencies easily. "
                    + "Ignored if set to `skip`."
            )]
            [CommandOption("-m|--manifest")]
            [DefaultValue("skip")]
            public string? Manifest { get; set; }

            [Description(
                "Root dependency to download to get the manifest. If set to `auto` it is assumed to be present in current classpath."
            )]
            [CommandOption("-f|--from")]
            [DefaultValue("auto")]
            public string? From { get; set; }

            [Description(
                "`logger` means the standard bundlebee logging output stream else it is considered as a file path. "
                    + "Note that if you end the filename with `.json` it will be formatted as json else just readable text."
            )]
            [CommandOption("-o|--output")]
            [DefaultValue("logger")]
            public string? Output { get; set; }
        }

        public class ItemsWrapper
        {
            public IEnumerable<string> Items { get; set; } = [];
        }
    }
}
