using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cezanne.Core.Service;

namespace Cezanne.Core.Descriptor
{
    [Description("Root descriptor holding the recipes.")]
    public class Manifest
    {
        public enum AwaitConditionType
        {
            [JsonPropertyName("JSON_POINTER")]
            [Description("JSON Pointer evaluation (fully custom).")]
            JsonPointer,

            [JsonPropertyName("STATUS_CONDITION")]
            [Description("Evaluate items in `/status/conditions`.")]
            StatusCondition
        }

        public enum ConditionOperator
        {
            [Description("At least one condition must match.")]
            [JsonPropertyName("ANY")]
            Any,

            [Description("All conditions must match.")]
            [JsonPropertyName("ALL")]
            All
        }

        public enum ConditionType
        {
            [Description("Key is read from process environment variables.")]
            [JsonPropertyName("ENV")]
            Env,

            [Description(
                "Key is read from application settings, either `cezanne` section or `AppSettings`."
            )]
            [JsonPropertyName("SYSTEM_PROPERTY")]
            SystemProperty
        }

        public enum JsonPointerOperator
        {
            [JsonPropertyName("EXISTS")]
            [Description("JSON Pointer exists model.")]
            Exists,

            [JsonPropertyName("MISSING")]
            [Description("JSON Pointer does not exist in the resource model.")]
            Missing,

            [JsonPropertyName("EQUALS")]
            [Description("JSON Pointer value is equal to (stringified comparison) value.")]
            EqualsValue,

            [JsonPropertyName("NOT_EQUALS")]
            [Description("JSON Pointer is different from the provided value.")]
            NotEquals,

            [JsonPropertyName("EQUALS_IGNORE_CASE")]
            [Description(
                "JSON Pointer value is equal (ignoring case) to (stringified comparison) value."
            )]
            EqualsIgnoreCase,

            [JsonPropertyName("NOT_EQUALS_IGNORE_CASE")]
            [Description("JSON Pointer is different (ignoring case) from the provided value.")]
            NotEqualsIgnoreCase,

            [JsonPropertyName("CONTAINS")]
            [Description("JSON Pointer contains the configured value.")]
            Contains
        }

        private static readonly JsonSerializerOptions DefaultToStringJsonOptions =
            new()
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                TypeInfoResolver = CezanneJsonContext.Default,
                Converters =
                {
                    new JsonStringEnumConverter<AwaitConditionType>(
                        JsonNamingPolicy.SnakeCaseUpper
                    ),
                    new JsonStringEnumConverter<ConditionOperator>(JsonNamingPolicy.SnakeCaseUpper),
                    new JsonStringEnumConverter<ConditionType>(JsonNamingPolicy.SnakeCaseUpper),
                    new JsonStringEnumConverter<JsonPointerOperator>(
                        JsonNamingPolicy.SnakeCaseUpper
                    )
                }
            };

        [Description("Ignored linting rule names when using `lint` command.")]
        public IEnumerable<IgnoredLintingRule> IgnoredLintingRules { get; set; } = [];

        [Description(
            "Enables to consider all recipes have their `interpolateDescriptors` descriptor set to `true`, "
                + "you can still set it to `false` if you want to disable it for one."
        )]
        public bool InterpolateRecipe { get; set; } = false;

        [Description(
            "List of files referenced as other manifests. "
                + "They are merged with this (main) manifest by *appending* _requirements_ and _recipe_. "
                + "It is relative to this manifest location. "
                + "Important: it is only about the same module references, external references are dependencies in an recipes. "
                + "It enables to split a huge `manifest.json` for an easier maintenance."
        )]
        public IEnumerable<ManifestReference> References { get; set; } = [];

        [Description(
            "Pre manifest execution checks (bundlebee version typically). "
                + "Avoids to install using a bundlebee/Cezanne version not compatible with the recipes. Can be fully omitted."
        )]
        public IEnumerable<Requirement> Requirements { get; set; } = [];

        [Description("List of described applications/libraries.")]
        public IEnumerable<Recipe> Recipes { get; set; } = [];

        // Yupiik bundlebee compatibility
        public IEnumerable<Recipe>? Alveoli
        {
            get => null;
            set => Recipes = value ?? Recipes;
        }

        private bool Equals(Manifest other)
        {
            return IgnoredLintingRules.SequenceEqual(other.IgnoredLintingRules)
                && InterpolateRecipe == other.InterpolateRecipe
                && References.SequenceEqual(other.References)
                && Requirements.SequenceEqual(other.Requirements)
                && Recipes.SequenceEqual(other.Recipes);
        }

        public override bool Equals(object? obj)
        {
            if (obj is null)
            {
                return false;
            }

            if (ReferenceEquals(this, obj))
            {
                return true;
            }

            if (obj.GetType() != GetType())
            {
                return false;
            }

            return Equals((Manifest)obj);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(
                IgnoredLintingRules,
                InterpolateRecipe,
                References,
                Requirements,
                Recipes
            );
        }

        public override string ToString()
        {
            return JsonSerializer.Serialize(this, typeof(Manifest), CezanneJsonContext.Default);
        }

        public class Patch
        {
            [Description(
                "Conditions to include this patch. "
                    + "Enables for example to have an environment variable enabling part of the stack (ex: `MONITORING=true`)"
            )]
            public Conditions? IncludeIf { get; set; }

            [Description(
                "The descriptor to patch. It can be any descriptor, including transitive ones. "
                    + "It can be `*` to patch all descriptors (`/metadata/label/app` for example) or "
                    + "`regex:<java pattern>` to match descriptor names with a regex."
            )]
            public string? DescriptorName { get; set; }

            [Description(
                "If set to `true`, it will interpolate the patch from the execution configuration which means "
                    + "you can use `--<config-key> <value>` to inject bindings too. "
                    + "An interesting interpolation is the ability to extract the ip/host of the host machine (`minikube ip` equivalent) using the kubeconfig file. "
                    + "Syntax is the following one: `{{kubeconfig.cluster.minikube.ip}}` or more generally `{{kubeconfig.cluster.<cluster name>.ip}}`. "
                    + "You can also await for some secret with this syntax "
                    + "`{{kubernetes.<namespace>.serviceaccount.<account name>.secrets.<secret name prefix>.data.<entry name>[.<timeout in seconds, default to 2mn>]}}`. "
                    + "This is particular useful to access freshly created service account tokens for example."
            )]
            public bool? Interpolate { get; set; }

            [Description(
                ""
                    + "JSON-Patch to apply on the JSON representation of the descriptor. "
                    + "It enables to inject configuration in descriptors for example, or changing some name/application."
            )]
            [JsonPropertyName("patch")]
            public JsonArray? PatchValue { get; set; }

            protected bool Equals(Patch other)
            {
                return Equals(IncludeIf, other.IncludeIf)
                    && DescriptorName == other.DescriptorName
                    && Interpolate == other.Interpolate
                    && (
                        PatchValue == other.PatchValue
                        || (
                            PatchValue != null
                            && other.PatchValue != null
                            && PatchValue.ToJsonString() == other.PatchValue.ToJsonString()
                        )
                    );
            }

            public override bool Equals(object? obj)
            {
                if (obj is null)
                {
                    return false;
                }

                if (ReferenceEquals(this, obj))
                {
                    return true;
                }

                if (obj.GetType() != GetType())
                {
                    return false;
                }

                return Equals((Patch)obj);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(IncludeIf, DescriptorName, Interpolate, PatchValue);
            }

            public override string ToString()
            {
                return JsonSerializer.Serialize(this, typeof(Patch), CezanneJsonContext.Default);
            }
        }

        public class Recipe
        {
            [Description(
                "Enables to consider all descriptors have their `interpolate` descriptor set to `true`, you can still set it to `false` if you want to disable it for one. "
                    + "If not set, `interpolateAlveoli` flag from the manifest."
            )]
            public bool? InterpolateDescriptors { get; set; }

            [Description("Optional description for `list-recipes` command.")]
            public string? Description { get; set; }

            [Description(
                "Name of the recipe. It must be unique accross the whole classpath. "
                    + "Using maven style identifier, it is recommended to name it "
                    + "`<groupId>:<artifactId>:<version>` using maven filtering but it is not enforced."
            )]
            public string? Name { get; set; }

            [Description(
                "If name does not follow `<groupId>:<artifactId>:<version>` naming (i.e. version can't be extracted from the name) "
                    + "then you can specify the version there. "
                    + "Note that if set, this is used in priority (explicit versus deduced)."
            )]
            public string? Version { get; set; }

            [Description(
                "List of descriptors to install for this recipe. This is required even if an empty array."
            )]
            public IEnumerable<Descriptor>? Descriptors { get; set; } = [];

            [Description(
                "Dependencies of this recipe. It is a way to import transitively a set of descriptors."
            )]
            public IEnumerable<Dependency>? Dependencies { get; set; } = [];

            [Description(
                "Should dependencies be installed one after the other or in parallel (default). "
                    + "It is useful when you install a namespace for example which must be awaited before next dependencies are installed."
            )]
            public bool? ChainDependencies { get; set; }

            [Description(
                "List of descriptors to ignore for this recipe (generally coming from dependencies)."
            )]
            public IEnumerable<DescriptorRef>? ExcludedDescriptors { get; set; } = [];

            [Description(
                "Patches on descriptors. "
                    + "It enables to inject configuration in descriptors by patching "
                    + "(using JSON-Patch or plain interpolation with `${key}` values) their JSON representation. "
                    + "The key is the descriptor name and each time the descriptor is found it will be applied."
            )]
            public IEnumerable<Patch>? Patches { get; set; } = [];

            [Description(
                "Local placeholders for this particular recipe and its dependencies. "
                    + "It is primarly intended to be able to create a template recipe and inject the placeholders inline."
            )]
            public IDictionary<string, string>? Placeholders { get; set; } =
                new Dictionary<string, string>();

            private bool Equals(Recipe other)
            {
                return (InterpolateDescriptors ?? false) == (other.InterpolateDescriptors ?? false)
                    && Name == other.Name
                    && Version == other.Version
                    && (
                        Equals(Descriptors, other.Descriptors)
                        || (
                            Descriptors != null
                            && other.Descriptors != null
                            && Descriptors.SequenceEqual(other.Descriptors)
                        )
                    )
                    && (
                        Equals(Dependencies, other.Dependencies)
                        || (
                            Dependencies != null
                            && other.Dependencies != null
                            && Dependencies.SequenceEqual(other.Dependencies)
                        )
                    )
                    && (
                        Equals(ExcludedDescriptors, other.ExcludedDescriptors)
                        || (
                            ExcludedDescriptors != null
                            && other.ExcludedDescriptors != null
                            && ExcludedDescriptors.SequenceEqual(other.ExcludedDescriptors)
                        )
                    )
                    && (
                        Equals(Placeholders, other.Placeholders)
                        || (
                            Placeholders != null
                            && other.Placeholders != null
                            && Placeholders.SequenceEqual(other.Placeholders)
                        )
                    );
            }

            public override bool Equals(object? obj)
            {
                if (obj is null)
                {
                    return false;
                }

                if (ReferenceEquals(this, obj))
                {
                    return true;
                }

                if (obj.GetType() != GetType())
                {
                    return false;
                }

                return Equals((Recipe)obj);
            }

            public override int GetHashCode()
            {
                HashCode hashCode = new();
                hashCode.Add(InterpolateDescriptors);
                hashCode.Add(Name);
                hashCode.Add(Version);
                hashCode.Add(Descriptors);
                hashCode.Add(Dependencies);
                hashCode.Add(ChainDependencies);
                hashCode.Add(ExcludedDescriptors);
                hashCode.Add(Patches);
                hashCode.Add(Placeholders);
                return hashCode.ToHashCode();
            }

            public override string ToString()
            {
                return JsonSerializer.Serialize(this, typeof(Recipe), CezanneJsonContext.Default);
            }
        }

        public class AwaitConditions
        {
            [Description("Operator to combine the conditions.")]
            [JsonPropertyName("operator")]
            public ConditionOperator OperatorType { get; set; } = ConditionOperator.All;

            [Description("List of condition to match according `operator`.")]
            public IEnumerable<AwaitCondition> Conditions { get; set; } = [];

            [Description(
                "Command to apply these conditions to, if not set it will be applied on `apply` command only. "
                    + "Note that for now only `apply` and `delete` commands are supported, others will be ignored."
            )]
            public string Command { get; set; } = "apply";

            protected bool Equals(AwaitConditions other)
            {
                return OperatorType == other.OperatorType
                    && Conditions.SequenceEqual(other.Conditions)
                    && Command == other.Command;
            }

            public override bool Equals(object? obj)
            {
                if (obj is null)
                {
                    return false;
                }

                if (ReferenceEquals(this, obj))
                {
                    return true;
                }

                if (obj.GetType() != GetType())
                {
                    return false;
                }

                return Equals((AwaitConditions)obj);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(OperatorType, Conditions, Command);
            }
        }

        public class AwaitCondition
        {
            [Description("Type of condition.")]
            [JsonPropertyName("type")]
            public AwaitConditionType TypeValue { get; set; } = AwaitConditionType.JsonPointer;

            [Description(
                ""
                    + "JSON Pointer to read from the resource. "
                    + "It can for example be on `/status/phase` to await a namespace creation. "
                    + "(for `type=JSON_POINTER`)."
            )]
            public string? Pointer { get; set; } = "/";

            [Description(
                ""
                    + "The operation to evaluate if this condition is true or not. "
                    + "(for `type=JSON_POINTER`)."
            )]
            public JsonPointerOperator? OperatorType { get; set; } =
                JsonPointerOperator.EqualsValue;

            [Description(
                ""
                    + "When condition type is `STATUS_CONDITION` it is the expected type of the condition. "
                    + "This is ignored when condition type is `JSON_POINTER`."
            )]
            public string? ConditionType { get; set; }

            [Description(
                ""
                    + "When condition type is `JSON_POINTER` and `operatorType` needs a value (`EQUALS` for example), the related value. "
                    + "It can be `Active` if you test namespace `/status/phase` for example. "
                    + "When condition type is `STATUS_CONDITION` it is the expected status."
            )]
            public object? Value { get; set; }

            protected bool Equals(AwaitCondition other)
            {
                return TypeValue == other.TypeValue
                    && Pointer == other.Pointer
                    && OperatorType == other.OperatorType
                    && ConditionType == other.ConditionType
                    && Value == other.Value;
            }

            public override bool Equals(object? obj)
            {
                if (obj is null)
                {
                    return false;
                }

                if (ReferenceEquals(this, obj))
                {
                    return true;
                }

                if (obj.GetType() != GetType())
                {
                    return false;
                }

                return Equals((AwaitCondition)obj);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(TypeValue, Pointer, OperatorType, ConditionType, Value);
            }

            public override string ToString()
            {
                return JsonSerializer.Serialize(
                    this,
                    typeof(AwaitCondition),
                    CezanneJsonContext.Default
                );
            }
        }

        public class Descriptor
        {
            [Description(
                "Type of this descriptor. For now only `kubernetes` is supported. "
                    + "It also defines in which folder under `bundlebee` the descriptor(s) are looked for from its name."
            )]
            public string? Type { get; set; } = "kubernetes";

            [Description(
                "Name of the descriptor to install. For kubernetes descriptors you can omit the `.yaml` extension."
            )]
            public string? Name { get; set; }

            [Description(
                "Optional, if coming from another manifest, the dependency to download to get the recipe."
            )]
            public string? Location { get; set; }

            [Description(
                ""
                    + "If set to `true`, apply/delete commands will await the actual creation of the resource (`GET /x` returns a HTTP 200) before continuing to process next resources. "
                    + "It is useful for namespaces for example to ensure applications can be created in the newly created namespace. "
                    + "It avoids to run and rerun apply command in practise. "
                    + "For more advanced tests, use `awaitConditions`."
            )]
            public bool Await { get; set; } = false;

            [Description(
                "On delete we rarely want to check the resource exists before but in these rare case you can set this toggle to `true`."
            )]
            public bool? AwaitOnDelete { get; set; }

            [Description(
                ""
                    + "Test to do on created/destroyed resources, enables to synchronize and await kubernetes actually starts some resource. "
                    + "For `apply` and `delete` commands, `descriptorAwaitTimeout` is still applied. "
                    + "Note that if you use multiple array entries for the same command it will be evaluated with an `AND`."
            )]
            public IEnumerable<AwaitConditions>? AwaitConditions { get; set; }

            [Description(
                ""
                    + "If set to `true`, it will interpolate the descriptor just before applying it - i.e. after it had been patched if needed. "
                    + "You can use `--<config-key> <value>` to inject bindings set as `{{config-key:-default value}}`. "
                    + "If not set, `interpolateDescriptors` flag from the recipe will be used. "
                    + "Note that having a descriptor extenson of `.hb` or `.handlebars` will force the interpolation (by design)."
            )]
            public bool? Interpolate { get; set; }

            [Description("Conditions to include this descriptor.")]
            public Conditions? IncludeIf { get; set; }

            public void InitInterpolate(bool interpolate)
            {
                Interpolate = interpolate;
            }

            public bool HasInterpolateValue()
            {
                return Interpolate ?? false;
            }

            protected bool Equals(Descriptor other)
            {
                return Type == other.Type
                    && Name == other.Name
                    && Location == other.Location
                    && Await == other.Await
                    && AwaitOnDelete == other.AwaitOnDelete
                    && (AwaitConditions ?? []).SequenceEqual(other.AwaitConditions ?? [])
                    && Interpolate == other.Interpolate
                    && Equals(IncludeIf, other.IncludeIf);
            }

            public override bool Equals(object? obj)
            {
                if (obj is null)
                {
                    return false;
                }

                if (ReferenceEquals(this, obj))
                {
                    return true;
                }

                if (obj.GetType() != GetType())
                {
                    return false;
                }

                return Equals((Descriptor)obj);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(
                    Type,
                    Name,
                    Location,
                    Await,
                    AwaitOnDelete,
                    AwaitConditions,
                    Interpolate,
                    IncludeIf
                );
            }

            public override string ToString()
            {
                return JsonSerializer.Serialize(
                    this,
                    typeof(Descriptor),
                    CezanneJsonContext.Default
                );
            }
        }

        public class Conditions
        {
            [JsonPropertyName("operator")]
            [Description("Operator to combine the conditions.")]
            public ConditionOperator? OperatorType { get; set; } = ConditionOperator.All;

            [JsonPropertyName("conditions")]
            [Description("List of condition to match according `operator`.")]
            public IEnumerable<Condition>? ConditionsList { get; set; } = [];

            protected bool Equals(Conditions other)
            {
                return OperatorType == other.OperatorType
                    && (ConditionsList ?? []).SequenceEqual(other.ConditionsList ?? []);
            }

            public override bool Equals(object? obj)
            {
                if (obj is null)
                {
                    return false;
                }

                if (ReferenceEquals(this, obj))
                {
                    return true;
                }

                if (obj.GetType() != GetType())
                {
                    return false;
                }

                return Equals((Conditions)obj);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(OperatorType, ConditionsList);
            }

            public override string ToString()
            {
                return JsonSerializer.Serialize(
                    this,
                    typeof(Conditions),
                    CezanneJsonContext.Default
                );
            }
        }

        public class Condition
        {
            [Description("Type of condition, defines the implementation to use.")]
            public ConditionType? Type { get; set; } = ConditionType.Env;

            [Description("Should the condition be reversed (ie \"not in this case\").")]
            public bool? Negate { get; set; } = false;

            [Description(
                "Expected key. If empty/null condition is ignored. If read value is null it defaults to an empty string."
            )]
            public string? Key { get; set; }

            [Description(
                "Expected value. If empty/null, `true` is assumed. Note that empty is allowed."
            )]
            public string? Value { get; set; }

            protected bool Equals(Condition other)
            {
                return Type == other.Type
                    && Negate == other.Negate
                    && Key == other.Key
                    && Value == other.Value;
            }

            public override bool Equals(object? obj)
            {
                if (obj is null)
                {
                    return false;
                }

                if (ReferenceEquals(this, obj))
                {
                    return true;
                }

                if (obj.GetType() != GetType())
                {
                    return false;
                }

                return Equals((Condition)obj);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(Type, Negate, Key, Value);
            }

            public override string ToString()
            {
                return JsonSerializer.Serialize(
                    this,
                    typeof(Condition),
                    CezanneJsonContext.Default
                );
            }
        }

        public class Dependency
        {
            [Description("Recipe name.")]
            public string? Name { get; set; }

            [Description(
                "Where to find the recipe. "
                    + "Note it will ensure the jar is present on the local maven repository."
            )]
            public string? Location { get; set; }

            [Description(
                "Conditions to include this dependency. "
                    + "Enables for example to have an environment variable enabling part of the stack (ex: `MONITORING=true`)"
            )]
            public Conditions? IncludeIf { get; set; }

            protected bool Equals(Dependency other)
            {
                return Name == other.Name
                    && Location == other.Location
                    && Equals(IncludeIf, other.IncludeIf);
            }

            public override bool Equals(object? obj)
            {
                if (obj is null)
                {
                    return false;
                }

                if (ReferenceEquals(this, obj))
                {
                    return true;
                }

                if (obj.GetType() != GetType())
                {
                    return false;
                }

                return Equals((Dependency)obj);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(Name, Location, IncludeIf);
            }

            public override string ToString()
            {
                return JsonSerializer.Serialize(
                    this,
                    typeof(Dependency),
                    CezanneJsonContext.Default
                );
            }
        }

        public class DescriptorRef
        {
            [Description(
                "Name of the descriptor (as declared, ie potentially without the extension)."
            )]
            public string? Name { get; set; }

            [Description("The container of the descriptor (maven coordinates generally).")]
            public string? Location { get; set; }

            protected bool Equals(DescriptorRef other)
            {
                return Name == other.Name && Location == other.Location;
            }

            public override bool Equals(object? obj)
            {
                if (obj is null)
                {
                    return false;
                }

                if (ReferenceEquals(this, obj))
                {
                    return true;
                }

                if (obj.GetType() != GetType())
                {
                    return false;
                }

                return Equals((DescriptorRef)obj);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(Name, Location);
            }

            public override string ToString()
            {
                return JsonSerializer.Serialize(
                    this,
                    typeof(DescriptorRef),
                    CezanneJsonContext.Default
                );
            }
        }

        public class Requirement
        {
            [Description(
                "Minimum bundlebee version, use `*` to replace any digit in a segment. "
                    + "Note that snapshot is ignored in the comparison for convenience. "
                    + "It is an inclusive comparison."
            )]
            public string? MinBundlebeeVersion { get; set; }

            [Description(
                "Minimum bundlebee version, use `*`to replace any digit in a segment. "
                    + "Note that snapshot is ignored in the comparison for convenience. "
                    + "It is an inclusive comparison."
            )]
            public string? MaxBundlebeeVersion { get; set; }

            [Description(
                "List of forbidden version (due to a bug or equivalent). "
                    + "Here too snapshot suffix is ignored. "
                    + "`*` is usable there too to replace any digit in a segment (ex: `1.*.*`). "
                    + "Note that `1.*` would *NOT* match `1.*.*`, version are always 3 segments."
            )]
            public IEnumerable<string>? ForbiddenVersions { get; set; } = [];

            protected bool Equals(Requirement other)
            {
                return MinBundlebeeVersion == other.MinBundlebeeVersion
                    && MaxBundlebeeVersion == other.MaxBundlebeeVersion
                    && (
                        Equals(ForbiddenVersions, other.ForbiddenVersions)
                        || (
                            ForbiddenVersions != null
                            && other.ForbiddenVersions != null
                            && ForbiddenVersions.SequenceEqual(other.ForbiddenVersions)
                        )
                    );
            }

            public override bool Equals(object? obj)
            {
                if (obj is null)
                {
                    return false;
                }

                if (ReferenceEquals(this, obj))
                {
                    return true;
                }

                if (obj.GetType() != GetType())
                {
                    return false;
                }

                return Equals((Requirement)obj);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(
                    MinBundlebeeVersion,
                    MaxBundlebeeVersion,
                    ForbiddenVersions
                );
            }

            public override string ToString()
            {
                return JsonSerializer.Serialize(
                    this,
                    typeof(Requirement),
                    CezanneJsonContext.Default
                );
            }
        }

        public class ManifestReference
        {
            [Description(
                "Relative or absolute - starting by a `/` - location (referenced to the base directory of `manifest.json`). "
                    + "For example `my-manifest.json` will resolve to `/path/to/cezanne/my-manifest.json` in a folder and `/cezanne/my-manifest.json` in a jar. "
                    + "Important: for resources (jar/classpath), the classloader is used so ensure your name is unique accross your classpath (we recommend you to prefix it with the module name, ex :`/cezanne/my-module.sub-manifest.json` or use a dedicated subfolder (`/cezanne/my-module/sub.json`)."
            )]
            public string? Path { get; set; }

            protected bool Equals(ManifestReference other)
            {
                return Path == other.Path;
            }

            public override bool Equals(object? obj)
            {
                if (obj is null)
                {
                    return false;
                }

                if (ReferenceEquals(this, obj))
                {
                    return true;
                }

                if (obj.GetType() != GetType())
                {
                    return false;
                }

                return Equals((ManifestReference)obj);
            }

            public override int GetHashCode()
            {
                return Path != null ? Path.GetHashCode() : 0;
            }

            public override string ToString()
            {
                return JsonSerializer.Serialize(
                    this,
                    typeof(ManifestReference),
                    CezanneJsonContext.Default
                );
            }
        }

        public class IgnoredLintingRule
        {
            [Description("Name of the rule to ignore.")]
            public string? Name { get; set; }

            protected bool Equals(IgnoredLintingRule other)
            {
                return Name == other.Name;
            }

            public override bool Equals(object? obj)
            {
                if (obj is null)
                {
                    return false;
                }

                if (ReferenceEquals(this, obj))
                {
                    return true;
                }

                if (obj.GetType() != GetType())
                {
                    return false;
                }

                return Equals((IgnoredLintingRule)obj);
            }

            public override int GetHashCode()
            {
                return Name != null ? Name.GetHashCode() : 0;
            }

            public override string ToString()
            {
                return JsonSerializer.Serialize(
                    this,
                    typeof(IgnoredLintingRule),
                    CezanneJsonContext.Default
                );
            }
        }
    }
}
