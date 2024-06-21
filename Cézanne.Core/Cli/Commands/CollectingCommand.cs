using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.RegularExpressions;
using Cézanne.Core.Cli.Progress;
using Cézanne.Core.Descriptor;
using Cézanne.Core.Runtime;
using Cézanne.Core.Service;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Cézanne.Core.Cli.Command
{
    public abstract class CollectingCommand<T, S>(
        ILogger<T> baseLogger,
        ArchiveReader archiveReader,
        RecipeHandler recipeHandler,
        string visitingLogMarker,
        string visitedLogMarker
    ) : AsyncCommand<S>
        where T : CollectingCommand<T, S>
        where S : CollectingCommand<T, S>.CollectorSettings
    {
        protected virtual int AfterCollection(
            string id,
            S settings,
            (
                IDictionary<string, Manifest.Recipe> recipes,
                IDictionary<string, IList<LoadedDescriptor>> descriptors
            ) collected
        )
        {
            return 0;
        }

        public override async Task<int> ExecuteAsync(CommandContext context, S settings)
        {
            return await DoExecuteAsync(Guid.NewGuid().ToString(), context, settings);
        }

        protected virtual async Task<int> DoExecuteAsync(
            string id,
            CommandContext context,
            S settings
        )
        {
            Predicate<string> descriptorFilter = settings.Descriptor is null
                ? s => true
                : settings.Descriptor.StartsWith("r/")
                    ? _AsPredicate(new Regex(settings.Descriptor["r/".Length..]))
                    : settings.Descriptor.Equals;
            var (recipes, descriptors) = await AnsiConsole
                .Progress()
                .AutoClear(true)
                .HideCompleted(false)
                .StartAsync(async ctx =>
                {
                    baseLogger.LogTrace(
                        "Looking up recipe id='{Id}' location='{Location}' manifest='{Manifest}' descriptor='{Descriptor}'",
                        id,
                        settings.From,
                        settings.Manifest,
                        settings.Descriptor
                    );
                    var cache = archiveReader.NewCache();
                    var progress = new ProgressHandler(ctx).OnProgress;
                    var recipes = await recipeHandler.FindRootRecipes(
                        settings.From,
                        settings.Manifest,
                        settings.Recipe,
                        id,
                        progress
                    );

                    var collectedRecipes = new ConcurrentDictionary<string, Manifest.Recipe>();
                    var collectedDescriptors =
                        new ConcurrentDictionary<string, IList<LoadedDescriptor>>();
                    foreach (var recipe in recipes)
                    {
                        await recipeHandler.ExecuteOnceOnRecipe(
                            visitingLogMarker,
                            recipe.Manifest,
                            recipe.Recipe,
                            null,
                            (ctx, desc) =>
                            {
                                collectedRecipes.AddOrUpdate(
                                    ctx.Recipe.Name!,
                                    k => ctx.Recipe,
                                    (k, v) => v
                                );

                                if (!descriptorFilter(desc.Configuration.Name ?? ""))
                                {
                                    return Task.CompletedTask;
                                }

                                var recipeDescriptors = collectedDescriptors.AddOrUpdate(
                                    ctx.Recipe.Name!,
                                    k => [],
                                    (k, v) => v
                                );
                                lock (recipeDescriptors)
                                {
                                    recipeDescriptors.Add(desc);
                                }

                                return Task.CompletedTask;
                            },
                            cache,
                            d => Task.CompletedTask,
                            visitedLogMarker,
                            id,
                            progress
                        );
                    }

                    return (collectedRecipes, collectedDescriptors);
                });

            if (recipes.IsEmpty)
            {
                AnsiConsole.MarkupLine("No recipe matched, check your options please");
                return 1;
            }

            if (descriptors.IsEmpty)
            {
                AnsiConsole.MarkupLine("No descriptor matched, check your options please");
                return 1;
            }

            return AfterCollection(id, settings, (recipes, descriptors));
        }

        private Predicate<string> _AsPredicate(Regex regex)
        {
            return s => regex.Match(s).Success;
        }

        public abstract class CollectorSettings : CommandSettings
        {
            [Description(
                "Recipe/Alveolus name to lookup. When set to `auto`, it will look through all available manifests found in the from location. "
                    + "If you set manifest option, alveolus is set to `auto` and there is a single alveolus in it, this will default to it."
            )]
            [CommandOption("-a|-r|--alveolus|--recipe")]
            [DefaultValue("auto")]
            public string? Recipe { get; set; }

            [Description("Enable to filer a single descriptor.")]
            [CommandOption("-d|--descriptor")]
            public string? Descriptor { get; set; }

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
        }
    }
}
