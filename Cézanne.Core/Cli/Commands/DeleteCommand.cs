using Cézanne.Core.Cli.Async;
using Cézanne.Core.K8s;
using Cézanne.Core.Runtime;
using Cézanne.Core.Service;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Net;
using System.Text.Json.Nodes;

namespace Cézanne.Core.Cli.Command
{
    public class DeleteCommand(
        ILogger<DeleteCommand> logger,
        K8SClient client,
        ArchiveReader archiveReader,
        RecipeHandler recipeHandler,
        ConditionAwaiter conditionAwaiter) : AsyncCommand<DeleteCommand.Settings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
        {
            string id = Guid.NewGuid().ToString();
            logger.LogTrace("Deleting id='{Id}' location='{Location}' manifest='{Manifest}'", id, settings.From,
                settings.Manifest);

            ArchiveReader.Cache cache = archiveReader.NewCache();
            IEnumerable<RecipeHandler.RecipeContext> recipes =
                await recipeHandler.FindRootRecipes(settings.From, settings.Manifest, settings.Alveolus, id);

            IList<LoadedDescriptor> toDelete = new List<LoadedDescriptor>();
            IEnumerable<Func<Task>> tasks = recipes
                .Select(it => it.Exclude(settings.ExcludedLocations ?? "none", settings.ExcludedDescriptors ?? "none"))
                .Select(it =>
                {
                    async Task handler()
                    {
                        await recipeHandler.ExecuteOnceOnRecipe(
                            "Deleting", it.Manifest, it.Recipe, null,
                            async (ctx, recipe) =>
                            {
                                lock (toDelete)
                                {
                                    toDelete.Add(recipe);
                                }

                                await Task.CompletedTask;
                            },
                            cache,
                            async desc =>
                            {
                                if (desc.Configuration.AwaitOnDelete ?? false)
                                {
                                    await conditionAwaiter.Await("delete", desc, settings.AwaitTimeout);
                                }

                                await Task.CompletedTask;
                            },
                            "deleted", id);
                    }

                    return (Func<Task>)handler;
                });

            await Asyncs.All(false, tasks);

            toDelete = toDelete.Distinct().Reverse().ToImmutableList();

            IDictionary<LoadedDescriptor, (IEnumerable<JsonObject>, Func<Task>)> deletions = toDelete.Select(it =>
                {
                    List<JsonObject> list = new();

                    async Task handler()
                    {
                        await client.ForDescriptor(it.Content, it.Extension, async item =>
                        {
                            lock (list)
                            {
                                list.Add(item.Prepared);
                            }

                            JsonObject metadata = item.Prepared["metadata"]!.AsObject();
                            JsonNode? name = metadata["name"];
                            JsonNode? ns = metadata.TryGetPropertyValue("namespace", out JsonNode? n)
                                ? n
                                : client.DefaultNamespace ?? "default";
                            logger.LogInformation("Deleting {Namespace}/{Name} ({Kind})", ns, name,
                                item.Prepared["kind"]);

                            string query = settings.GracePeriod > 0
                                ? "?gracePeriodSeconds=" + settings.GracePeriod
                                : "";
                            string uri = await client.ToBaseUri(item.Prepared) + $"/{name}{query}";

                            using HttpResponseMessage deleteResponse = await client.SendAsync(
                                HttpMethod.Delete, uri,
                                "{\"kind\":\"DeleteOptions\",\"apiVersion\":\"v1\",\"propagationPolicy\":\"Foreground\"}",
                                "application/json");
                            if (deleteResponse.StatusCode == HttpStatusCode.UnprocessableEntity)
                            {
                                logger.LogWarning("Can't delete entity {Uri}: {Response}\n{ResponsePayload}", uri,
                                    deleteResponse, await deleteResponse.Content.ReadAsStringAsync());
                            }

                            return true;
                        });
                    }

                    return KeyValuePair.Create<LoadedDescriptor, (IEnumerable<JsonObject>, Func<Task>)>(it,
                        (list, handler));
                })
                .ToDictionary();
            await Asyncs.All(true, deletions.Values.Select(it => it.Item2));

            if (settings.Await < 0 || !toDelete.Any())
            {
                return 0;
            }

            DateTime expiration = DateTime.UtcNow.AddMilliseconds(settings.Await);
            HashSet<JsonObject> alreadyDeleted = new();
            int expectedDeleted = deletions.SelectMany(it => it.Value.Item1).Count();
            while (true)
            {
                List<JsonObject> toCheck = deletions.SelectMany(it => it.Value.Item1)
                    .Where(it => !alreadyDeleted.Contains(it)).ToList();
                IEnumerable<Task> checks = toCheck.Select(it =>
                {
                    async Task doCheck()
                    {
                        using HttpResponseMessage response = await client.SendAsync(HttpMethod.Get,
                            await client.ToBaseUri(it) + $"/{it["metadata"]!["name"]}");
                        if (response.StatusCode !=
                            HttpStatusCode.OK) // theorically 404 but don't loop when it is something not existing
                        {
                            lock (alreadyDeleted)
                            {
                                alreadyDeleted.Add(it);
                            }
                        }
                    }

                    return doCheck();
                });
                await Task.WhenAll(checks);
                if (expectedDeleted == alreadyDeleted.Count)
                {
                    break;
                }

                logger.LogInformation("Awaiting deletion, will recheck in 5s");
                await Task.Delay(5_000);
            }

            return 0;
        }

        public class Settings : CommandSettings
        {
            [Description(
                "Alveolus name to deploy. When set to `auto`, it will deploy all manifests found in the classpath. " +
                "If you set manifest option, alveolus is set to `auto` and there is a single alveolus in it, " +
                "this will default to it instead of using classpath deployment.")]
            [CommandOption("-a|--alveolus")]
            [DefaultValue("auto")]
            public string? Alveolus { get; set; }

            [Description(
                "Manifest to load to start to deploy (a file path or inline). This optional setting mainly enables to use dependencies easily. " +
                "Ignored if set to `skip`.")]
            [CommandOption("-m|--manifest")]
            [DefaultValue("skip")]
            public string? Manifest { get; set; }

            [Description(
                "Root dependency to download to get the manifest. If set to `auto` it is assumed to be present in current classpath.")]
            [CommandOption("-f|--from")]
            [DefaultValue("auto")]
            public string? From { get; set; }

            [Description(
                "If set it will be added on REST calls to force a custom grace period (in seconds). Setting it to `0` enables to delete faster objects.")]
            [CommandOption("--grace-period")]
            [DefaultValue("0")]
            public int GracePeriod { get; set; }

            [Description(
                "If an integer > 0, how long (ms) to await for the actual deletion of components, default does not await.")]
            [CommandOption("--await")]
            [DefaultValue("0")]
            public int Await { get; set; }

            [Description("For descriptors with `await` = `true` the max duration the test can last.")]
            [CommandOption("--await-timeout")]
            [DefaultValue("0")]
            public int AwaitTimeout { get; set; }

            [Description("" +
                         "Enables to exclude descriptors from the command line. `none` to ignore. Value is comma separated. " +
                         "Note that using this setting, location is set to `*` so only the name is matched.")]
            [CommandOption("--excluded-descriptors")]
            [DefaultValue("none")]
            public string? ExcludedDescriptors { get; set; }

            [Description(
                "Enables to exclude locations (descriptor is set to `*`) from the command line. `none` to ignore. Value is comma separated.")]
            [CommandOption("--excluded-locations")]
            [DefaultValue("none")]
            public string? ExcludedLocations { get; set; }
        }
    }
}