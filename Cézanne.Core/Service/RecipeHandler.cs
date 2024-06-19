using Cézanne.Core.Cli.Async;
using Cézanne.Core.Descriptor;
using Cézanne.Core.Interpolation;
using Cézanne.Core.Runtime;
using Json.Patch;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cézanne.Core.Service
{
    public class RecipeHandler(
        ILogger<RecipeHandler> logger,
        ManifestReader manifestReader,
        ArchiveReader archiveReader,
        RequirementService requirementService,
        ConditionEvaluator conditionEvaluator,
        Substitutor substitutor)
    {
        public async Task ExecuteOnceOnRecipe(string prefixOnVisitLog,
            Manifest manifest, Manifest.Recipe recipe,
            Func<VisitContext, Task>? userCallback,
            Func<VisitContext, LoadedDescriptor, Task> onDescriptor,
            ArchiveReader.Cache cache,
            Func<LoadedDescriptor, Task> awaiter,
            string alreadyHandledMarker, string id,
            Action<string, double> progress)
        {
            HashSet<string> alreadyDone = [];
            await ExecuteOnRecipe(prefixOnVisitLog, manifest, recipe, userCallback, async (ctx, desc) =>
            {
                if (alreadyDone.Add(desc.Configuration.Name + '|' + desc.Content))
                {
                    await onDescriptor(ctx, desc);
                }
                else
                {
                    logger.LogInformation("{Name} already {handler}, skipping", desc.Configuration.Name,
                        alreadyHandledMarker);
                }
            }, cache, awaiter, id, progress);
        }

        public async Task ExecuteOnRecipe(string prefixOnVisitLog, Manifest manifest, Manifest.Recipe recipe,
            Func<VisitContext, Task>? userCallback,
            Func<VisitContext, LoadedDescriptor, Task> onDescriptor,
            ArchiveReader.Cache cache, Func<LoadedDescriptor, Task> awaiter,
            string? id, Action<string, double>? progress)
        {
            var aggregatedHandler = new Func<VisitContext, Task>[1];
            Func<VisitContext, Task> internalCallback = ctx =>
                _OnRecipe(prefixOnVisitLog, manifest, ctx.Recipe, ctx.Patches, ctx.Excludes, ctx.Cache,
                    aggregatedHandler[0], onDescriptor, awaiter, ctx.Placeholders, id, progress);
            Func<VisitContext, Task> combinedCallbacks = userCallback == null
                ? internalCallback
                : async ctx =>
                {
                    await userCallback(ctx);
                    await internalCallback(ctx);
                };
            aggregatedHandler[0] = combinedCallbacks;
            await combinedCallbacks(new VisitContext(
                manifest,
                recipe,
                ImmutableDictionary<Predicate<string>, Manifest.Patch>.Empty,
                ImmutableDictionary<string, string>.Empty,
                [], cache, id
            ));
        }

        private async Task _OnRecipe(string prefixOnVisitLog, Manifest manifest, Manifest.Recipe from,
            IDictionary<Predicate<string>, Manifest.Patch> patches,
            IEnumerable<Manifest.DescriptorRef> excludes,
            ArchiveReader.Cache cache,
            Func<VisitContext, Task> onRecipe,
            Func<VisitContext, LoadedDescriptor, Task> onDescriptor,
            Func<LoadedDescriptor, Task> awaiter,
            IDictionary<string, string> placeholders,
            string? id,
            Action<string, double>? progress)
        {
            logger.LogInformation("{Marker} '{Recipe}'", prefixOnVisitLog, from.Name);

            IEnumerable<Manifest.DescriptorRef> currentExcludes = [.. excludes, .. from.ExcludedDescriptors ?? []];
            var currentPatches =
                !(from.Patches ?? []).Any() ? patches : _MergePatches(patches, from.Patches);
            var currentPlaceholders = from.Placeholders is null || !from.Placeholders.Any()
                ? placeholders
                : placeholders
                    .Where(it => !from.Placeholders.ContainsKey(it.Key))
                    .Union(from.Placeholders)
                    .ToDictionary();

            var dependenciesTasks = (from.Dependencies ?? [])
                .Where(it => conditionEvaluator.Test(it.IncludeIf))
                .Select(it => _PrepareDependencyResolution(
                    manifest, it, cache, id,
                    currentExcludes, currentPatches,
                    currentPlaceholders, onRecipe, progress));

            if (dependenciesTasks.Any())
            {
                await Asyncs.All(from.ChainDependencies ?? false, dependenciesTasks);
            }

            var descriptorsTasks = await Task.WhenAll(
                _SelectDescriptors(from, excludes)
                    .Select(desc => _FindDescriptor(desc, cache, id, progress)));
            await _AfterDependencies(
                manifest, from, patches, excludes, cache, onDescriptor,
                awaiter, currentPlaceholders, currentPatches, descriptorsTasks ?? [], id);
        }

        private async Task _AfterDependencies(Manifest manifest, Manifest.Recipe from,
            IDictionary<Predicate<string>, Manifest.Patch> patches,
            IEnumerable<Manifest.DescriptorRef> excludes, ArchiveReader.Cache cache,
            Func<VisitContext, LoadedDescriptor, Task> onDescriptor,
            Func<LoadedDescriptor, Task> awaiter,
            IDictionary<string, string> placeholders,
            IDictionary<Predicate<string>, Manifest.Patch> currentPatches,
            LoadedDescriptor[] descriptors, string? id)
        {
            logger.LogTrace("Visiting {descriptors}", descriptors);

            var rankedDescriptors = _RankDescriptors(descriptors);
            foreach (var descs in rankedDescriptors)
            {
                if (!descs.Any())
                {
                    continue;
                }

                if (awaiter is null)
                {
                    await _PrepareDescriptors(
                        manifest, from, patches, placeholders, excludes, cache,
                        onDescriptor, currentPatches, descs, id);
                }
                else
                {
                    List<LoadedDescriptor> filteredDescriptors = new();
                    await _PrepareDescriptors(
                        manifest, from, patches, placeholders, excludes, cache,
                        (ctx, desc) =>
                        {
                            lock (filteredDescriptors)
                            {
                                filteredDescriptors.Add(desc);
                            }

                            return onDescriptor(ctx, desc);
                        },
                        currentPatches, descs, id);
                    if (filteredDescriptors.Count > 0)
                    {
                        var aggregatedAwait = filteredDescriptors.Select(awaiter);
                        await Task.WhenAll(aggregatedAwait);
                    }
                }
            }
        }

        private async Task _PrepareDescriptors(Manifest manifest, Manifest.Recipe from,
            IDictionary<Predicate<string>, Manifest.Patch> patches,
            IDictionary<string, string> placeholders,
            IEnumerable<Manifest.DescriptorRef> excludes,
            ArchiveReader.Cache cache, Func<VisitContext, LoadedDescriptor, Task> onDescriptor,
            IDictionary<Predicate<string>, Manifest.Patch> currentPatches,
            IEnumerable<LoadedDescriptor> descs, string? id)
        {
            var tasks = descs
                .Select(it => _Prepare(from, it, currentPatches, placeholders, id))
                .Select(it =>
                    onDescriptor(new VisitContext(manifest, from, patches, placeholders, excludes, cache, id), it));
            await Task.WhenAll(tasks);
        }

        private LoadedDescriptor _Prepare(Manifest.Recipe from, LoadedDescriptor desc,
            IDictionary<Predicate<string>, Manifest.Patch> currentPatches,
            IDictionary<string, string> placeholders,
            string? id)
        {
            return substitutor.WithContext(placeholders, () =>
            {
                var content = desc.Content;
                var patches = currentPatches
                    .Where(it =>
                        it.Key(desc.Configuration.Name ?? "") || it.Key($"{desc.Configuration.Name}.{desc.Extension}"));
                var alreadyInterpolated = false;
                foreach (var patch in patches.Select(it => it.Value))
                {
                    if (patch.Interpolate ?? false)
                    {
                        content = substitutor.Replace(from, desc, content, id);
                    }

                    if (patch.PatchValue is null)
                    {
                        continue;
                    }

                    if (!conditionEvaluator.Test(patch.IncludeIf))
                    {
                        continue;
                    }

                    var patchString = JsonSerializer.Serialize(patch.PatchValue, Jsons.Options);
                    if (patch.Interpolate ?? false)
                    {
                        patchString = substitutor.Replace(from, desc, patchString, id);
                    }

                    var jsonPatch = JsonSerializer.Deserialize<JsonPatch>(patchString, Jsons.Options) ??
                                    throw new InvalidOperationException(
                                        $"Can't read interpolated patch {patch} (interpolated={patchString})");

                    if ("json" != desc.Extension)
                    {
                        throw new InvalidOperationException($"not json descriptors are not yet supported: {desc}");
                    }

                    try
                    {
                        var json = desc.Extension switch
                        {
                            "json" => JsonSerializer.Deserialize<JsonNode>(desc.Content, Jsons.Options),
                            _ => Jsons.FromYaml(desc.Content)
                        };
                        content = JsonSerializer.Serialize(jsonPatch.Apply(json), Jsons.Options);
                    }
                    catch (Exception e)
                    {
                        if (!desc.Configuration.Interpolate ?? false)
                        {
                            throw new InvalidOperationException($"Can't patch '{desc.Configuration.Name}': {e}");
                        }

                        logger.LogTrace(
                            "Trying to interpolate the descriptor before patching it since it failed without: '{Name}'",
                            desc.Configuration.Name);
                        content = substitutor.Replace(from, desc, content, id);
                        alreadyInterpolated = true;
                        content = JsonSerializer.Serialize(
                            jsonPatch.Apply(JsonSerializer.Deserialize<JsonNode>(content, Jsons.Options)));
                    }
                }

                if (!alreadyInterpolated && (desc.Configuration.Interpolate ?? false))
                {
                    content = substitutor.Replace(from, desc, content, id);
                }

                return new LoadedDescriptor(desc.Configuration, content, desc.Extension, desc.Uri, desc.Resource);
            });
        }

        private IEnumerable<IEnumerable<LoadedDescriptor>> _RankDescriptors(IEnumerable<LoadedDescriptor> descriptors)
        {
            List<IEnumerable<LoadedDescriptor>> rankedDescriptors = new(1 /*generally 1 or 2*/);
            List<LoadedDescriptor> current = new();
            foreach (var desc in descriptors)
            {
                current.Add(desc);
                if (desc.Configuration.Await)
                {
                    rankedDescriptors.Add(current);
                    current = [];
                }
            }

            if (current.Count > 0)
            {
                rankedDescriptors.Add(current);
            }

            return rankedDescriptors;
        }

        private async Task<LoadedDescriptor> _FindDescriptor(Manifest.Descriptor desc, ArchiveReader.Cache cache,
            string? id, Action<string, double>? progress)
        {
            if (desc.Location is null)
            {
                throw new InvalidOperationException($"Location missing to descriptor {desc}");
            }

            var type = desc.Type ?? "kubernetes";
            var resource = string.Join('/',
                "bundlebee",
                type,
                desc.Name + _FindExtension(
                    desc.Name ??
                    throw new ArgumentNullException($"Descriptor name can't be null {desc}", nameof(desc.Name)), type));

            var archive = await cache.LoadArchive(desc.Location, id, new ActionProgress(desc.Location, progress));
            var content = archive.Descriptors.TryGetValue(resource, out var descriptors)
                ? descriptors
                : throw new InvalidOperationException($"No descriptor '{resource}' found in '{desc.Location}'");
            var uri = Directory.Exists(desc.Location)
                ? new Uri(Path.Combine(desc.Location, resource)).AbsoluteUri
                : $"{new Uri(desc.Location).AbsoluteUri}!{resource}";
            return new LoadedDescriptor(desc, content, _ExtractExtension(resource), uri, resource);
        }

        private string _ExtractExtension(string resource)
        {
            var lastDot = resource.LastIndexOf('.');
            var extension = lastDot > 0 ? resource[(lastDot + 1)..] : "yaml";
            return (extension switch
            {
                "yml" => "yaml",
                _ => extension
            }).ToLowerInvariant();
        }

        private string? _FindExtension(string name, string type)
        {
            if ("kubernetes" == type)
            {
                if (name.EndsWith(".yaml") || name.EndsWith("yml") ||
                    name.EndsWith(".json") ||
                    name.EndsWith(".hb") || name.EndsWith(".handlebars"))
                {
                    return "";
                }

                // yaml is the most common even if we would like json....
                return ".yaml";
            }

            throw new InvalidOperationException($"Unsupported type: '{type}'");
        }

        private IEnumerable<Manifest.Descriptor> _SelectDescriptors(Manifest.Recipe from,
            IEnumerable<Manifest.DescriptorRef> excludes)
        {
            return (from.Descriptors ?? [])
                .Where(desc => conditionEvaluator.Test(desc.IncludeIf) && !excludes.Any(it =>
                    _Matches(it.Location, desc.Location) && _Matches(it.Name, desc.Name)));
        }

        private bool _Matches(string? expected, string? actual)
        {
            return expected == actual || expected is null || "*" == expected;
        }

        private Func<Task> _PrepareDependencyResolution(Manifest manifest, Manifest.Dependency it,
            ArchiveReader.Cache cache, string? id, IEnumerable<Manifest.DescriptorRef> currentExcludes,
            IDictionary<Predicate<string>, Manifest.Patch> currentPatches,
            IDictionary<string, string> currentPlaceholders, Func<VisitContext, Task> onRecipe,
            Action<string, double>? progress)
        {
            if (it.Location is not null)
            {
                var recipe = (manifest.Recipes ?? [])
                             .Where(recipe => recipe.Name == it.Name)
                             .FirstOrDefault((Manifest.Recipe?)null) ??
                             throw new ArgumentException($"Didn't find recipe '{it.Name}'",
                                 nameof(manifest));

                async Task result()
                {
                    await onRecipe(new VisitContext(manifest, recipe, currentPatches, currentPlaceholders,
                        currentExcludes, cache, id));
                }

                ;
                return result;
            }

            var name = it.Name ?? throw new ArgumentNullException(nameof(it.Name), "No dependency name");

            async Task resultWithLookup()
            {
                var ctx = await _FindRecipe(it.Location ?? name, name, cache, id, progress);
                await onRecipe(new VisitContext(manifest, ctx.Recipe, currentPatches, currentPlaceholders,
                    currentExcludes, cache, id));
            }

            ;
            return resultWithLookup;
        }

        private async Task<RecipeContext> _FindRecipe(string from, string recipeName,
            ArchiveReader.Cache cache,
            string? id,
            Action<string, double>? progress)
        {
            var archive = await cache.LoadArchive(from, id, new ActionProgress(from, progress));
            var selected = archive.Manifest.Recipes.First(it => it.Name == recipeName);
            foreach (var item in selected.Descriptors ?? [])
            {
                item.Location ??= from;
            }

            return new RecipeContext(archive.Manifest, selected);
        }

        private IDictionary<Predicate<string>, Manifest.Patch> _MergePatches(
            IDictionary<Predicate<string>, Manifest.Patch> current, IEnumerable<Manifest.Patch>? others)
        {
            Dictionary<Predicate<string>, Manifest.Patch> result = new(current);
            foreach (var pair in others ?? [])
            {
                if (pair.DescriptorName is not null)
                {
                    result.Add(_ToPredicate(pair.DescriptorName), pair);
                }
            }

            return result;
        }

        private Predicate<string> _ToPredicate(string descriptorName)
        {
            if (descriptorName.Contains("*"))
            {
                return "*" == descriptorName ? s => true : new Regex(descriptorName).IsMatch;
            }

            if (descriptorName.StartsWith("regex:"))
            {
                return new Regex(descriptorName["regex:".Length..]).IsMatch;
            }

            return descriptorName.Equals;
        }

        public async Task<Manifest?> FindManifest(string? from, string? manifest, string? id,
            Action<string, double>? progress)
        {
            if (manifest is not null and not "skip")
            {
                return _ReadManifest(manifest, id);
            }

            if (from is null or "auto")
            {
                return await Task.FromResult<Manifest?>(null);
            }

            var archive = await archiveReader.NewCache()
                .LoadArchive(from, id, new ActionProgress(from ?? manifest ?? "manifest", progress));
            return archive.Manifest;
        }

        public async Task<IEnumerable<RecipeContext>> FindRootRecipes(string? from, string? manifest, string? recipe,
            string? id, Action<string, double>? progress)
        {
            var result = await _DoFindRootRecipes(from, manifest, recipe, id, progress);
            foreach (var mf in result)
            {
                requirementService.CheckRequirements(mf.Manifest);
            }

            return result;
        }

        public async Task<IEnumerable<RecipeContext>> _DoFindRootRecipes(string? from, string? manifest, string? recipe,
            string? id, Action<string, double>? progress)
        {
            if (manifest is not null && manifest != "skip")
            {
                var mf = _ReadManifest(manifest, id);
                if (!mf.Recipes.Any())
                {
                    throw new InvalidOperationException("No recipe in manifest");
                }

                var recipeName = recipe == "auto" && mf.Recipes.Count() == 1 ? mf.Recipes.First().Name : recipe;
                var selected = mf.Recipes.Where(it => it.Name == recipeName).First();
                return [new RecipeContext(mf, selected)];
            }

            if (from is null)
            {
                throw new ArgumentException("No manifest nor location set", nameof(from));
            }

            var cache = archiveReader.NewCache();
            var archive = await cache.LoadArchive(from, id,
                new ActionProgress(from ?? recipe ?? manifest ?? "recipe", progress));
            var selectedRecipe = recipe == "auto" && archive.Manifest.Recipes.Count() == 1
                ? archive.Manifest.Recipes.First()
                : archive.Manifest.Recipes.FirstOrDefault(it => it?.Name == recipe, null) ??
                  throw new InvalidOperationException(
                      $"No alveolus {recipe} found in '{from}' (available: {string.Join(", ", archive.Manifest.Recipes.Select(it => it.Name))})");
            foreach (var descriptor in selectedRecipe.Descriptors ?? [])
            {
                descriptor.Location ??= from;
            }

            return [new RecipeContext(archive.Manifest, selectedRecipe)];
        }

        private Manifest _ReadManifest(string manifest, string? id)
        {
            if (manifest.StartsWith('{'))
            {
                return manifestReader.ReadManifest(
                    null,
                    () => new MemoryStream(Encoding.UTF8.GetBytes(manifest)),
                    s => throw new ArgumentException("References unsupported in manifest memory mode", nameof(s)),
                    id);
            }

            var bundlebee = Directory.GetParent(Path.GetFullPath(manifest))?.FullName ??
                            throw new ArgumentException($"Invalid manifest path {manifest}", nameof(manifest));
            var root = Directory.GetParent(bundlebee)?.FullName;
            return manifestReader.ReadManifest(
                root,
                () => File.OpenRead(manifest),
                relative =>
                {
                    if (File.Exists(relative))
                    {
                        return File.OpenRead(relative);
                    }

                    return File.OpenRead(Path.Combine(bundlebee, relative));
                },
                id);
        }

        public record VisitContext(
            Manifest Manifest,
            Manifest.Recipe Recipe,
            IDictionary<Predicate<string>, Manifest.Patch> Patches,
            IDictionary<string, string> Placeholders,
            IEnumerable<Manifest.DescriptorRef> Excludes,
            ArchiveReader.Cache Cache,
            string? Id)
        {
        }

        public record RecipeContext(Manifest Manifest, Manifest.Recipe Recipe)
        {
            public RecipeContext Exclude(string? excludedLocations, string excludedDescriptors)
            {
                if (excludedDescriptors is null or "none" && excludedLocations is null or "none")
                {
                    return this;
                }

                Manifest.Recipe recipe = new()
                {
                    Name = Recipe.Name,
                    Version = Recipe.Version,
                    Descriptors = Recipe.Descriptors,
                    Dependencies = Recipe.Dependencies,
                    Patches = Recipe.Patches,
                    Placeholders = Recipe.Placeholders,
                    ExcludedDescriptors =
                    [
                        .. Recipe.ExcludedDescriptors ?? [],
                        .. excludedDescriptors is null or "none"
                            ? []
                            : excludedDescriptors.Split(',').Select(it =>
                                new Manifest.DescriptorRef { Name = it, Location = "*" }),
                        .. excludedLocations is null or "none"
                            ? []
                            : excludedLocations.Split(',')
                                .Select(it => new Manifest.DescriptorRef { Name = "*", Location = it })
                    ]
                };
                return new RecipeContext(Manifest, recipe);
            }
        }
    }

    internal class ActionProgress(string Name, Action<string, double>? callback) : IProgress<double>
    {
        public void Report(double value)
        {
            if (callback is not null)
            {
                callback(Name, value);
            }
        }
    }
}