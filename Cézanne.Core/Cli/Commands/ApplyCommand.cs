using Cézanne.Core.Cli.Async;
using Cézanne.Core.Service;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace Cézanne.Core.Cli.Command;


public class ApplyCommand(ILogger<ApplyCommand> logger, ArchiveReader archiveReader, RecipeHandler recipeHandler, ConditionAwaiter conditionAwaiter) : AsyncCommand<ApplyCommand.Settings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var id = Guid.NewGuid().ToString();
        logger.LogTrace("Applying id='{Id}' location='{Location}' manifest='{Manifest}'", id, settings.From, settings.Manifest);

        var cache = archiveReader.NewCache();
        var recipes = await recipeHandler.FindRootRecipes(settings.From,settings.Manifest,settings.Alveolus,id);
        IEnumerable<Func<Task>> tasks = recipes
            .Select(it => it.Exclude(settings.ExcludedLocations ?? "none", settings.ExcludedDescriptors ?? "none"))
            .Select(it => {
                async Task handler() {
                    await recipeHandler.ExecuteOnceOnRecipe(
                        "Deploying", it.Manifest, it.Recipe, null, 
                        (ctx, recipe) => {
                            // todo: (ctx, desc) -> kube.apply(desc.getContent(), desc.getExtension(), labels),
                            return Task.CompletedTask;
                        },
                        cache,
                        desc => conditionAwaiter.Await("apply", desc, settings.AwaitTimeout),
                        "deployed", id);
                }
                return (Func<Task>) handler;
            });
        await Asyncs.All(settings.ChainDescriptorsInstallation, tasks);

        return 0;
    }


    public class Settings : CommandSettings
    {
        [Description("Alveolus name to deploy. When set to `auto`, it will deploy all manifests found in the classpath. " +
                "If you set manifest option, alveolus is set to `auto` and there is a single alveolus in it, " +
                "this will default to it instead of using classpath deployment.")]
        [CommandOption("-a|--alveolus")]
        [DefaultValue("auto")]
        public string? Alveolus { get; set; }

        [Description("Manifest to load to start to deploy (a file path or inline). This optional setting mainly enables to use dependencies easily. " +
                "Ignored if set to `skip`.")]
        [CommandOption("-m|--manifest")]
        [DefaultValue("skip")]
        public string? Manifest { get; set; }

        [Description("Root dependency to download to get the manifest. If set to `auto` it is assumed to be present in current classpath.")]
        [CommandOption("-f|--from")]
        [DefaultValue("auto")]
        public string? From { get; set; }

        [Description("" +
                "If `true`, each descriptor installation awaits previous ones instead of being concurrent. " +
                "Enable an easier debugging for errors.")]
        [CommandOption("--chain")]
        [DefaultValue("false")]
        public bool ChainDescriptorsInstallation { get; set; }

        [Description("" +
                "For descriptors with `await` = `true` the max duration the test can last.")]
        [CommandOption("--await-timeout")]
        [DefaultValue("60000")]
        public int AwaitTimeout { get; set; }

        [Description("" +
                "Enables to exclude descriptors from the command line. `none` to ignore. Value is comma separated. " +
                "Note that using this setting, location is set to `*` so only the name is matched.")]
        [CommandOption("--excluded-descriptors")]
        [DefaultValue("none")]
        public string? ExcludedDescriptors { get; set; }

        [Description("Enables to exclude locations (descriptor is set to `*`) from the command line. `none` to ignore. Value is comma separated.")]
        [CommandOption("--excluded-locations")]
        [DefaultValue("none")]
        public string? ExcludedLocations { get; set; }
    }
}
