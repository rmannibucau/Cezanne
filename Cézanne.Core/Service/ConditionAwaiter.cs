using Cézanne.Core.Cli.Async;
using Cézanne.Core.Descriptor;
using Cézanne.Core.K8s;
using Cézanne.Core.Runtime;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cézanne.Core.Service;

public class ConditionAwaiter(ILogger<ConditionAwaiter> logger, ConditionJsonEvaluator conditionJsonEvaluator, K8SClient client)
{
    public async Task Await(string command, LoadedDescriptor descriptor, long timeout)
    {
        if (descriptor.Configuration is null)
        {
            return;
        }

        var conditions = (descriptor.Configuration.AwaitConditions ?? []).Where(it => it.Command == command);
        if (!descriptor.Configuration.Await && !conditions.Any())
        {
            return;
        }

        var expiration = DateTime.UtcNow.AddMilliseconds(timeout);
        if (descriptor.Configuration.Await)
        {
            await _Exists(descriptor, DateTime.UtcNow.AddMilliseconds(timeout), "delete" != command);
        }
        await Asyncs.All(false, conditions.Select<Manifest.AwaitConditions, Func<Task>>(it =>
        {
            async Task awaiter()
            {
                await _Await(it, expiration, descriptor);
            }
            return awaiter;
        }));
    }

    private async Task _Exists(LoadedDescriptor descriptor, DateTime timeout, bool expected)
    {
        async Task<bool> exists()
        {
            var result = await client.ForDescriptor(descriptor.Content, descriptor.Extension, async item =>
            {
                var kindLowerCased = item.Prepared["kind"]!.ToString().ToLowerInvariant() + 's';
                // todo: await ensureResourceSpec(desc, kindLoaded)
                var metadata = item.Prepared["metadata"]!.AsObject();
                var name = metadata["name"]!;
                var namespaceValue = metadata.TryGetPropertyValue("namespace", out var ns) ? ns : client.DefaultNamespace;

                // todo: io.yupiik.bundlebee.core.kube.KubeClient#toBaseUri once ensureResourceSpec is done
                var nsSegment = _IsSkipNameSpace(kindLowerCased) ? "" : ("/namespaces" + namespaceValue);
                var baseUri = $"{client.Base}{_FindApiPrefix(kindLowerCased, item.Prepared)}{nsSegment}/{kindLowerCased}/{name}";
                var response = await client.SendAsync(HttpMethod.Get, baseUri + "/" + name);
                return response.IsSuccessStatusCode;
            });
            return (result.Count() == 1 && result.First()) == expected;
        }
        await _WithRetry(timeout, descriptor, "resource exists", exists);
    }


    private async Task _Await(IEnumerable<Manifest.AwaitConditions> conditions, DateTime expiration, LoadedDescriptor descriptor)
    {
        if (conditions.Any())
        {
            await Asyncs.All(false, conditions.Select<Manifest.AwaitConditions, Func<Task>>(it => () => _Await(it, expiration, descriptor)));
        }
    }

    private async Task _Await(Manifest.AwaitConditions conditions, DateTime expiration, LoadedDescriptor descriptor)
    {
        if (conditions.Conditions.Any())
        {
            Func<IEnumerable<Task>, Task> combiner = conditions.OperatorType == Manifest.ConditionOperator.Any ? Task.WhenAny : Task.WhenAll;
            await combiner(conditions.Conditions.Select(it => _Await(it, expiration, descriptor)));
        }
    }

    private async Task _Await(Manifest.AwaitCondition condition, DateTime expiration, LoadedDescriptor descriptor)
    {
        async Task<bool> getResource()
        {
            var result = await client.ForDescriptor(descriptor.Content, descriptor.Extension, async item =>
            {
                var kindLowerCased = item.Prepared["kind"]!.ToString().ToLowerInvariant() + 's';
                // todo: await ensureResourceSpec(desc, kindLoaded)
                var metadata = item.Prepared["metadata"]!.AsObject();
                var name = metadata["name"]!;
                var namespaceValue = metadata.TryGetPropertyValue("namespace", out var ns) ? ns : client.DefaultNamespace;

                // todo: io.yupiik.bundlebee.core.kube.KubeClient#toBaseUri once ensureResourceSpec is done
                var nsSegment = _IsSkipNameSpace(kindLowerCased) ? "" : ("/namespaces" + namespaceValue);
                var baseUri = $"{client.Base}{_FindApiPrefix(kindLowerCased, item.Prepared)}{nsSegment}/{kindLowerCased}/{name}";
                var response = await client.SendAsync(HttpMethod.Get, baseUri + "/" + name);
                return response.IsSuccessStatusCode &&
                    (response.Headers.TryGetValues("x-dry-run", out var dryRun) || await _Evaluate(condition, response));
            });
            return (result ?? []).FirstOrDefault(false);
        }
        await _WithRetry(expiration, descriptor, condition.ToString(), getResource);
    }

    private async Task<bool> _Evaluate(Manifest.AwaitCondition condition, HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        try
        {
            return conditionJsonEvaluator.Evaluate(condition, JsonSerializer.Deserialize<JsonElement>(body, Jsons.Options));
        }
        catch (Exception ex)
        {
            if (condition.OperatorType == Manifest.JsonPointerOperator.Missing)
            {
                return true;
            }
            logger.LogTrace("{Exception} awaiting on {Condition}", ex, condition);
            return false;
        }
    }

    private bool _IsSkipNameSpace(string kindLowerCased)
    {
        return kindLowerCased is "nodes" or "persistentvolumes" or "clusterroles" or "clusterrolebindings";
    }

    private string _FindApiPrefix(string kindLowerCased, JsonObject desc)
    {
        return kindLowerCased switch
        {
            "deployments" or "statefulsets" or "daemonsets" or "replicasets" or "controllerrevisions" => "/apis/apps/v1",
            "cronjobs" => "/apis/batch/v1beta1",
            "apiservices" => "/apis/apiregistration.k8s.io/v1",
            "customresourcedefinitions" => "/apis/apiextensions.k8s.io/v1beta1",
            "mutatingwebhookconfigurations" or "validatingwebhookconfigurations" => "/apis/admissionregistration.k8s.io/v1",
            "roles" or "rolebindings" or "clusterroles" or "clusterrolebindings" => "/apis/" + desc["apiVersion"]!.ToString(),
            _ => "/api/v1",
        };

    }

    private async Task _WithRetry(DateTime expiration,
                                LoadedDescriptor descriptor,
                                string timeoutMarker,
                                Func<Task<bool>> evaluator)
    {
        while (true)
        {
            logger.LogTrace("waiting for {Descriptor}", descriptor);
            bool result = false;
            try
            {
                result = await evaluator();
                if (result)
                {
                    logger.LogTrace("Awaited for {Descriptor}, succeeded", descriptor);
                    return;
                }
            }
            catch (Exception e)
            {
                logger.LogTrace("waiting for {Descriptor}: {Error}", descriptor, e);
            }
            if (!result)
            {
                if (DateTime.UtcNow > expiration)
                {
                    throw new InvalidOperationException($"Timeout on condition: {descriptor.Configuration} reached: {timeoutMarker}");
                }
                logger.LogTrace("Will retry awaiting for {Descriptor}", descriptor);
                Thread.Sleep(500); // todo: configuration
            }
        }
    }
}
