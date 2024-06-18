using Json.Patch;
using Json.Pointer;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace Cézanne.Core.Service
{
    public class ContainerSanitizer(ILogger<ContainerSanitizer> logger)
    {
        public bool CanSanitizeCpuResource(string kindLowerCased)
        {
            return "cronjobs" == kindLowerCased || "deployments" == kindLowerCased ||
                   "daemonsets" == kindLowerCased || "pods" == kindLowerCased || "jobs" == kindLowerCased;
        }

        public JsonObject DropCpuResources(string kind, JsonObject desc)
        {
            string? containersParentPointer = kind switch
            {
                "deployments" or "daemonsets" or "jobs" => "/spec/template/spec",
                "cronjobs" => "/spec/jobTemplate/spec/template/spec",
                "pods" => "/spec",
                _ => null
            };
            if (containersParentPointer is null)
            {
                // wrong call?
                return desc;
            }

            return _ReplaceIfPresent(
                _ReplaceIfPresent(desc, containersParentPointer, "initContainers", _DropNullCpu),
                containersParentPointer, "containers", _DropNullCpu);
        }

        private JsonObject _ReplaceIfPresent(JsonObject source, string parentPtr, string name,
            Func<JsonArray, JsonArray> fn)
        {
            JsonPointer ptr = JsonPointer.Parse(string.Join('/', parentPtr, name));
            try
            {
                if (!ptr.TryEvaluate(source, out JsonNode? result))
                {
                    return source;
                }

                JsonArray original = result!.AsArray();
                JsonArray changed = fn(original);
                if (original == changed)
                {
                    return source;
                }

                PatchResult patch = new JsonPatch(PatchOperation.Replace(ptr, changed)).Apply(source);
                if (patch.IsSuccess && patch.Result is not null)
                {
                    return patch.Result.AsObject();
                }

                return source;
            }
            catch (Exception je)
            {
                logger.LogTrace("{Exception}", je);
                return source;
            }
        }


        private JsonObject _DropNullCpu(JsonObject container)
        {
            if (!container.ContainsKey("resources"))
            {
                return container;
            }

            JsonObject resources = container["resources"]!.AsObject();
            if (resources.ContainsKey("requests"))
            {
                JsonObject requests = resources["requests"]!.AsObject();
                if (requests.ContainsKey("cpu") && requests["cpu"] == null)
                {
                    requests.Remove("cpu");
                }
            }

            if (resources.ContainsKey("limits"))
            {
                JsonObject limits = resources["limits"]!.AsObject();
                if (limits.ContainsKey("cpu") && limits["cpu"] == null)
                {
                    limits.Remove("cpu");
                }
            }

            return container;
        }

        private JsonArray _DropNullCpu(JsonArray array)
        {
            try
            {
                int i = 0;
                foreach (JsonNode? item in array)
                {
                    array[i++] = _DropNullCpu(item!.AsObject());
                }
            }
            catch (Exception re)
            {
                logger.LogTrace(re, "Can't check null cpu resources: {Exception}", re.Message);
            }

            return array;
        }
    }
}