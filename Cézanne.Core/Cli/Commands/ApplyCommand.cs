using Cézanne.Core.Cli.Async;
using Cézanne.Core.Cli.Progress;
using Cézanne.Core.K8s;
using Cézanne.Core.Service;
using Json.Patch;
using Json.Pointer;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cézanne.Core.Cli.Command
{
    public class ApplyCommand(
        ILogger<ApplyCommand> logger,
        K8SClient client,
        ArchiveReader archiveReader,
        RecipeHandler recipeHandler,
        ConditionAwaiter conditionAwaiter,
        ContainerSanitizer sanitizer) : AsyncCommand<ApplyCommand.Settings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
        {
            var id = Guid.NewGuid().ToString();

            var tasksToInstall = await AnsiConsole.Progress()
                .AutoClear(true)
                .HideCompleted(false)
                .StartAsync(async ctx =>
                {
                    logger.LogTrace("Applying id='{Id}' location='{Location}' manifest='{Manifest}'", id, settings.From,
                        settings.Manifest);

                    var cache = archiveReader.NewCache();
                    var progress = new ProgressHandler(ctx).OnProgress;
                    var recipes =
                        await recipeHandler.FindRootRecipes(settings.From, settings.Manifest, settings.Recipe, id,
                            progress);
                    var tasks = recipes
                        .Select(it => it.Exclude(settings.ExcludedLocations ?? "none",
                            settings.ExcludedDescriptors ?? "none"))
                        .Select(it =>
                        {
                            async Task handler()
                            {
                                await recipeHandler.ExecuteOnceOnRecipe(
                                    "Deploying", it.Manifest, it.Recipe, null,
                                    async (ctx, recipe) =>
                                    {
                                        await client.ForDescriptor(recipe.Content, recipe.Extension, async item =>
                                        {
                                            if (settings.LogDescriptors)
                                            {
                                                logger.LogInformation("{Descriptor}={Content}",
                                                    recipe.Configuration.Name,
                                                    JsonSerializer.Serialize(item.Prepared, Jsons.Options));
                                            }

                                            var kind = item.Prepared["kind"]!.ToString().ToLowerInvariant() + 's';
                                            return await _DoApply(item.Prepared, kind, 1, settings.DryRun,
                                                settings.FieldValidation ?? "skip",
                                                settings.StatefulSetSpecUpdatableAttributes ??
                                                ImmutableHashSet<string>.Empty);
                                        });
                                    },
                                    cache,
                                    desc => conditionAwaiter.Await("apply", desc, settings.AwaitTimeout),
                                    "deployed", id, progress);
                            }

                            return (Func<Task>)handler;
                        });
                    return tasks;
                });
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.BouncingBar)
                .SpinnerStyle(Style.Parse("dodgerblue1"))
                .StartAsync($"Applying id='{id}' location='{settings.From}' manifest='{settings.Manifest}'",
                    async ctx =>
                    {
                        await Asyncs.All(settings.ChainDescriptorsInstallation, tasksToInstall);
                    });

            return 0;
        }

        private async Task<bool> _DoApply(JsonObject prepared, string kind, int retries, bool dryRun,
            string fieldValidation, ISet<string> statefulSetsAllowedSpecAttributes)
        {
            var metadata = prepared["metadata"]!.AsObject();
            var name = metadata["name"]!.ToString();
            var ns = metadata.TryGetPropertyValue("namespace", out var metaNs)
                ? metaNs
                : client.DefaultNamespace;
            logger.LogInformation("Applying {kind} '{Name}' on namespace '{Namespace}'", name, ns, kind[..^1]);

            var baseUri = await client.ToBaseUri(prepared);
            var completeUri = baseUri + '/' + name;

            using var findResponse = await client.SendAsync(HttpMethod.Get, completeUri);
            if (findResponse.StatusCode > HttpStatusCode.NotFound)
            {
                var error = await findResponse.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"Can't apply {completeUri}: {error}");
            }

            var queryParams = "?fieldManager=kubectl-client-side-apply" +
                              (dryRun ? "" : "&dryRun=All") +
                              ("skip" == fieldValidation ? "" : "&fieldValidation=" + fieldValidation);
            var uriWithQuery = $"{completeUri}{queryParams}";

            if (sanitizer.CanSanitizeCpuResource(kind))
            {
                prepared = sanitizer.DropCpuResources(kind, prepared);
            }

            if (findResponse.StatusCode == HttpStatusCode.OK)
            {
                logger.LogTrace("{Namespace}/{Name} already exist, updating it", ns ?? "-", name);

                if (kind != "persistentvolumeclaims")
                {
                    var payload = prepared;
                    if (payload["metadata"]!.AsObject().TryGetPropertyValue("resourceVersion", out var crv) &&
                        crv is null &&
                        metadata.TryGetPropertyValue("resourceVersion", out var rs) && recipeHandler is not null)
                    {
                        payload = new JsonPatch(PatchOperation.Add(JsonPointer.Parse("/metadata/resourceVersion"), rs))
                            .Apply(payload).Result!.AsObject();
                    }

                    if ("statefulsets" == kind && statefulSetsAllowedSpecAttributes.Any())
                    {
                        var spec = payload.TryGetPropertyValue("spec", out var sss)
                            ? sss!.AsObject()
                            : null;
                        if (spec is not null)
                        {
                            foreach (var attr in spec.ToImmutableList())
                            {
                                if (!statefulSetsAllowedSpecAttributes.Contains(attr.Key))
                                {
                                    spec.Remove(attr.Key);
                                }
                            }
                        }
                    }

                    prepared["metadata"]!.AsObject().TryGetPropertyValue("annotations", out var annotations);
                    if (annotations is not null)
                    {
                        if (annotations.AsObject()
                                .TryGetPropertyValue("io.yupiik.bundlebee/force", out var force) &&
                            force?.GetValueKind() == JsonValueKind.True)
                        {
                            using var deleteResponse = await client.SendAsync(
                                HttpMethod.Delete, completeUri + "?gracePeriodSeconds=-1",
                                "{\"kind\":\"DeleteOptions\",\"apiVersion\":\"v1\",\"propagationPolicy\":\"Foreground\"}",
                                "application/json");
                            if (deleteResponse.StatusCode == HttpStatusCode.UnprocessableEntity)
                            {
                                logger.LogWarning("Invalid deletion {Uri}, {Response}", completeUri, deleteResponse);
                                // should we fail or let it be retried?
                            }
                            else
                            {
                                while (true)
                                {
                                    using var response = await client.SendAsync(HttpMethod.Get, completeUri);
                                    logger.LogTrace("{Namespace}/{Name} deletion:{Response}", ns ?? "-", name,
                                        response);
                                    if (response.StatusCode == HttpStatusCode.NotFound)
                                    {
                                        break;
                                    }
                                }

                                using var putAfterDeleteResponse = await client.SendAsync(
                                    HttpMethod.Put, uriWithQuery,
                                    JsonSerializer.Serialize(payload, Jsons.Options), "application/json");
                                return putAfterDeleteResponse.StatusCode switch
                                {
                                    HttpStatusCode.OK or HttpStatusCode.Created => true,
                                    _ => throw new InvalidOperationException(
                                        $"Can't put {completeUri}: {putAfterDeleteResponse}")
                                };
                            }
                        }

                        if (annotations.AsObject()
                                .TryGetPropertyValue("io.yupiik.bundlebee/putOnUpdate", out var update) &&
                            update?.GetValueKind() == JsonValueKind.True)
                        {
                            using var putResponse = await client.SendAsync(HttpMethod.Put, uriWithQuery,
                                JsonSerializer.Serialize(payload, Jsons.Options), "application/json");
                            return putResponse.StatusCode switch
                            {
                                HttpStatusCode.OK or HttpStatusCode.Created => true,
                                _ => throw new InvalidOperationException($"Can't put {completeUri}: {putResponse}")
                            };
                        }
                    }

                    var patchType = annotations is not null &&
                                    annotations.AsObject().TryGetPropertyValue(
                                        "io.yupiik.bundlebee/patchContentType", out var patchContentType) &&
                                    patchContentType is not null
                        ? patchContentType.ToString()
                        : "application/strategic-merge-patch+json";
                    var patchResponse = await client.SendAsync(HttpMethod.Patch, uriWithQuery,
                        JsonSerializer.Serialize(payload, Jsons.Options), patchType);
                    if (patchResponse.StatusCode == HttpStatusCode.UnsupportedMediaType)
                    {
                        // CRD which does not support strategic for ex
                        patchResponse = await client.SendAsync(HttpMethod.Patch, uriWithQuery,
                            JsonSerializer.Serialize(payload, Jsons.Options), "application/merge-patch+json");
                    }

                    using (patchResponse)
                    {
                        return patchResponse.StatusCode switch
                        {
                            HttpStatusCode.OK or HttpStatusCode.Created => true,
                            _ => throw new InvalidOperationException($"Can't patch {completeUri}: {patchResponse}")
                        };
                    }
                }
            }

            logger.LogTrace("{Name} ({Kind}) does not exist, creating it", name, kind);

            using var postResponse = await client.SendAsync(HttpMethod.Post, baseUri,
                JsonSerializer.Serialize(prepared, Jsons.Options), "application/json");
            if (postResponse.StatusCode == HttpStatusCode.Conflict && retries > 0)
            {
                // happens for service accounts for ex when implicitly created
                var json = await postResponse.Content.ReadAsStringAsync();
                var obj = JsonSerializer.Deserialize<JsonObject>(json, Jsons.Options);
                if ((obj?.TryGetPropertyValue("reason", out var reason) ?? false) && reason is not null &&
                    "AlreadyExists" == reason.ToString() &&
                    obj!.TryGetPropertyValue("details", out var details) && details is not null &&
                    details.AsObject()["kind"]?.ToString() == "serviceaccounts")
                {
                    return await _DoApply(prepared, kind, retries - 1, dryRun, fieldValidation,
                        statefulSetsAllowedSpecAttributes);
                }
            }

            if (postResponse.StatusCode != HttpStatusCode.Created)
            {
                throw new InvalidOperationException(
                    $"Can't create {completeUri}: {postResponse}\n{await postResponse.Content.ReadAsStringAsync()}");
            }

            logger.LogTrace("Created {Name} ({Kind})", name, kind);
            return true;
        }

        public class Settings : CommandSettings
        {
            [Description(
                "Recipe/Alveolus name to deploy. When set to `auto`, it will look through all available manifests found in the from location. " +
                "If you set manifest option, alveolus is set to `auto` and there is a single alveolus in it, this will default to it.")]
            [CommandOption("-a|-r|--alveolus|--recipe")]
            [DefaultValue("auto")]
            public string? Recipe { get; set; }

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

            [Description(
                "Enables to exclude locations (descriptor is set to `*`) from the command line. `none` to ignore. Value is comma separated.")]
            [CommandOption("--excluded-locations")]
            [DefaultValue("none")]
            public string? ExcludedLocations { get; set; }

            [Description("Should descriptor contents be logged (after processing).")]
            [CommandOption("--log-descriptors")]
            [DefaultValue("false")]
            public bool LogDescriptors { get; set; }

            [Description("Should `dryRun=All` query parameter be set.")]
            [CommandOption("--dry-run")]
            [DefaultValue("false")]
            public bool DryRun { get; set; } = false;

            [Description(
                "`fieldValidation` - server side validation - value when applying a descriptor, values can be `Strict`, `Warn` pr `Ignore`. Note that using `skip` will ignore the query parameter.")]
            [CommandOption("--field-validation")]
            [DefaultValue("Strict")]
            public string? FieldValidation { get; set; }

            [Description("List of fields allowed for `StatefulSet` updates - all are not updatable.")]
            [CommandOption("--update-statefulset-spec-attributes")]
            [DefaultValue(
                "replicas,template,updateStrategy,persistentVolumeClaimRetentionPolicy,minReadySeconds,serviceName,selector")]
            public ISet<string>? StatefulSetSpecUpdatableAttributes { get; set; }
        }
    }
}