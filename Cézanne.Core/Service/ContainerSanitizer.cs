using System.Text.Json.Nodes;
using Json.Patch;
using Json.Pointer;
using Microsoft.Extensions.Logging;

namespace Cézanne.Core.Service
{
    public class ContainerSanitizer(ILogger<ContainerSanitizer> logger)
    {
        public bool CanSanitizeCpuResource(string kindLowerCased)
        {
            return "cronjobs" == kindLowerCased
                || "deployments" == kindLowerCased
                || "daemonsets" == kindLowerCased
                || "pods" == kindLowerCased
                || "jobs" == kindLowerCased;
        }

        public JsonObject DropCpuResources(string kind, JsonObject desc)
        {
            var containersParentPointer = kind switch
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
                containersParentPointer,
                "containers",
                _DropNullCpu
            );
        }

        private JsonObject _ReplaceIfPresent(
            JsonObject source,
            string parentPtr,
            string name,
            Func<JsonArray, JsonArray> fn
        )
        {
            var ptr = JsonPointer.Parse(string.Join('/', parentPtr, name));
            try
            {
                if (!ptr.TryEvaluate(source, out var result))
                {
                    return source;
                }

                var original = result!.AsArray();
                var changed = fn(original);
                if (original == changed)
                {
                    return source;
                }

                var patch = new JsonPatch(PatchOperation.Replace(ptr, changed)).Apply(source);
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

            var resources = container["resources"]!.AsObject();
            if (resources.ContainsKey("requests"))
            {
                var requests = resources["requests"]!.AsObject();
                if (requests.ContainsKey("cpu") && requests["cpu"] == null)
                {
                    requests.Remove("cpu");
                }
            }

            if (resources.ContainsKey("limits"))
            {
                var limits = resources["limits"]!.AsObject();
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
                var i = 0;
                foreach (var item in array)
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
