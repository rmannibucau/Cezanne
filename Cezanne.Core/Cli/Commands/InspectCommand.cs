using System.Collections.Immutable;
using System.ComponentModel;
using System.Text.Json;
using Cezanne.Core.Cli.Completable;
using Cezanne.Core.Descriptor;
using Cezanne.Core.Runtime;
using Cezanne.Core.Service;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Json;

namespace Cezanne.Core.Cli.Command
{
    public class InspectCommand(
        ILogger<InspectCommand> logger,
        ArchiveReader archiveReader,
        RecipeHandler recipeHandler
    )
        : CollectingCommand<InspectCommand, InspectCommand.Settings>(
            logger,
            archiveReader,
            recipeHandler,
            "Inspecting",
            "inspected"
        ),
            ICompletable
    {
        public async Task<IEnumerable<string>> CompleteOptionAsync(
            string option,
            IList<string> args,
            int currentWord
        )
        {
            return await Task.FromResult(ImmutableList<string>.Empty);
        }

        protected override int AfterCollection(
            string id,
            Settings settings,
            (
                IDictionary<string, Manifest.Recipe> recipes,
                IDictionary<string, IList<LoadedDescriptor>> descriptors
            ) collected
        )
        {
            AnsiConsole.Write(_BuildTree(collected.recipes, collected.descriptors, settings));
            return 0;
        }

        private Tree _BuildTree(
            IDictionary<string, Manifest.Recipe> recipes,
            IDictionary<string, IList<LoadedDescriptor>> descriptors,
            Settings settings
        )
        {
            var root = new Tree("Inspection Result");
            foreach (var recipe in recipes)
            {
                var recipeNode = root.AddNode($"[bold]{recipe.Key}[/]");

                var descriptorsRootNode = recipeNode.AddNode("Descriptors");
                foreach (var descriptor in recipe.Value.Descriptors ?? [])
                {
                    var existing = descriptors[recipe.Key]
                        .FirstOrDefault(it => it!.Configuration.Name == descriptor.Name, null);
                    if (existing is null)
                    {
                        continue;
                    }

                    var descriptorNode = descriptorsRootNode.AddNode(descriptor.Name!);
                    if (descriptor.Location is not null && settings.Verbose)
                    {
                        descriptorNode.AddNode($"From [bold]{descriptor.Location}[/]");
                    }

                    _AddCondition(descriptorNode, descriptor.IncludeIf);

                    if (descriptor.Await)
                    {
                        descriptorNode.AddNode("Await on apply command is enabled");
                    }

                    if (descriptor.AwaitOnDelete ?? false)
                    {
                        descriptorNode.AddNode("Await on delete command is enabled");
                    }

                    if (descriptor.AwaitConditions is not null)
                    {
                        var awaitRootNode = descriptorNode.AddNode("Await conditions");
                        foreach (var conditions in descriptor.AwaitConditions)
                        {
                            var awaitNode = awaitRootNode.AddNode(
                                $"Conditions Operator: {conditions.OperatorType}"
                            );
                            if (conditions.Command is not null)
                            {
                                awaitNode.AddNode($"For command: {conditions.Command}");
                            }

                            foreach (var condition in conditions.Conditions)
                            {
                                var typeDescription = condition.TypeValue switch
                                {
                                    Manifest.AwaitConditionType.JsonPointer => "JSON-Pointer",
                                    Manifest.AwaitConditionType.StatusCondition
                                        => "Status condition",
                                    _ => "?"
                                };
                                var comparatorDescription = (
                                    condition.OperatorType
                                    ?? Manifest.JsonPointerOperator.EqualsValue
                                ) switch
                                {
                                    Manifest.JsonPointerOperator.Exists => "exists",
                                    Manifest.JsonPointerOperator.Missing => "is missing",
                                    Manifest.JsonPointerOperator.EqualsValue
                                        => $"is equal to {condition.Value}",
                                    Manifest.JsonPointerOperator.NotEquals
                                        => $"is not equal to {condition.Value}",
                                    Manifest.JsonPointerOperator.EqualsIgnoreCase
                                        => $"is equal ignoring case to {condition.Value}",
                                    Manifest.JsonPointerOperator.NotEqualsIgnoreCase
                                        => $"is not equal ignoring case to {condition.Value}",
                                    Manifest.JsonPointerOperator.Contains
                                        => $"contains {condition.Value}",
                                    _ => "?"
                                };
                                awaitNode.AddNode(
                                    $"{typeDescription} [bold]{condition.Pointer}[/] {comparatorDescription}"
                                );
                            }
                        }
                    }

                    if (settings.Verbose)
                    {
                        if (existing is not null)
                        {
                            descriptorNode.AddNode(
                                new Panel(
                                    existing.Content.StartsWith('{')
                                    || existing.Content.StartsWith('[')
                                        ? new JsonText(existing.Content)
                                        : new Text(existing.Content)
                                )
                                    .Header("Content")
                                    .Collapse()
                                    .RoundedBorder()
                            );
                        }
                    }
                }

                if (recipe.Value.Dependencies is not null && recipe.Value.Dependencies.Any())
                {
                    var dependencies = recipeNode.AddNode("Dependencies");
                    foreach (var dependency in recipe.Value.Dependencies ?? [])
                    {
                        var dependencyNode = descriptorsRootNode.AddNode(dependency.Name!);
                        if (dependency.Location is not null)
                        {
                            dependencyNode.AddNode($"From [bold]{dependency.Location}[/]");
                        }

                        _AddCondition(dependencyNode, dependency.IncludeIf);
                    }
                }

                if (recipe.Value.Placeholders is not null && recipe.Value.Placeholders.Any())
                {
                    var placeholderRootNode = recipeNode.AddNode("Placeholders");

                    var table = new Table()
                        .RoundedBorder()
                        .HeavyHeadBorder()
                        .ShowRowSeparators()
                        .AddColumn("Key")
                        .AddColumn("Value");
                    foreach (
                        var keyValue in recipe.Value.Placeholders
                            ?? ImmutableDictionary<string, string>.Empty
                    )
                    {
                        table.AddRow($"[bold]{keyValue.Key}[/]", keyValue.Value);
                    }

                    placeholderRootNode.AddNode(table);
                }

                if (recipe.Value.Patches is not null && recipe.Value.Patches.Any())
                {
                    var patchNode = recipeNode.AddNode("Patches");
                    foreach (var patch in recipe.Value.Patches!)
                    {
                        var patchDescriptorNode = patchNode.AddNode(
                            $"Descriptor [bold]{patch.DescriptorName}[/]"
                        );
                        if (patch.Interpolate ?? false)
                        {
                            patchDescriptorNode.AddNode($"Interpolated: {patch.Interpolate}");
                        }

                        _AddCondition(patchDescriptorNode, patch.IncludeIf);
                        patchDescriptorNode.AddNode(
                            new Panel(
                                new JsonText(
                                    JsonSerializer.Serialize(
                                        patch.PatchValue!,
                                        CezanneJsonContext.Default.JsonArray
                                    )
                                )
                            )
                                .Header("JSON-Patch")
                                .Collapse()
                                .RoundedBorder()
                        );
                    }
                }
            }

            return root;
        }

        private void _AddCondition(TreeNode root, Manifest.Conditions? includeIf)
        {
            if (
                includeIf is null
                || includeIf.ConditionsList is null
                || !includeIf.ConditionsList.Any()
            )
            {
                return;
            }

            var conditions = includeIf.ConditionsList.ToImmutableList();
            var conditionsNode = root.AddNode("Condition" + (conditions.Count > 1 ? "s" : ""));
            foreach (var condition in conditions)
            {
                var type = (condition.Type ?? Manifest.ConditionType.Env) switch
                {
                    Manifest.ConditionType.Env => "environment variable",
                    Manifest.ConditionType.SystemProperty => "application setting",
                    _ => "?"
                };
                var comparison = condition.Negate ?? false ? "has not" : "has";
                conditionsNode.AddNode(
                    $"{type} [bold]{condition.Key}[/] {comparison} value [bold]{condition.Value}[/]"
                );
            }
        }

        public class Settings : CollectorSettings
        {
            [Description("Should the output be complete or limited.")]
            [CommandOption("-v|--verbose")]
            [DefaultValue("true")]
            public bool Verbose { get; set; }
        }
    }
}
