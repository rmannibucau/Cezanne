using Cézanne.Core.Cli.Async;
using Cézanne.Core.Descriptor;
using Cézanne.Core.K8s;
using Cézanne.Core.Runtime;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Cézanne.Core.Service
{
    public class ConditionAwaiter(
        ILogger<ConditionAwaiter> logger,
        ConditionJsonEvaluator conditionJsonEvaluator,
        K8SClient client)
    {
        public async Task Await(string command, LoadedDescriptor descriptor, long timeout)
        {
            if (descriptor.Configuration is null)
            {
                return;
            }

            var conditions =
                (descriptor.Configuration.AwaitConditions ?? []).Where(it => it.Command == command);
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
                var result = await client.ForDescriptor(descriptor.Content, descriptor.Extension,
                    async item =>
                    {
                        var name = item.Prepared["metadata"]!.AsObject()["name"]!.ToString();
                        var baseUri = await client.ToBaseUri(item.Prepared) + '/' + name;
                        using var response = await client.SendAsync(HttpMethod.Get, baseUri + "/" + name);
                        return response.IsSuccessStatusCode;
                    });
                return (result.Count() == 1 && result.First()) == expected;
            }

            await client.WithRetry(timeout, descriptor, "resource exists", exists);
        }

        private async Task _Await(Manifest.AwaitConditions conditions, DateTime expiration, LoadedDescriptor descriptor)
        {
            if (conditions.Conditions.Any())
            {
                Func<IEnumerable<Task>, Task> combiner = conditions.OperatorType == Manifest.ConditionOperator.Any
                    ? Task.WhenAny
                    : Task.WhenAll;
                await combiner(conditions.Conditions.Select(it => _Await(it, expiration, descriptor)));
            }
        }

        private async Task _Await(Manifest.AwaitCondition condition, DateTime expiration, LoadedDescriptor descriptor)
        {
            async Task<bool> getResource()
            {
                var result = await client.ForDescriptor(descriptor.Content, descriptor.Extension,
                    async item =>
                    {
                        var name = item.Prepared["metadata"]!.AsObject()["name"]!.ToString();
                        var baseUri = await client.ToBaseUri(item.Prepared) + '/' + name;
                        var response = await client.SendAsync(HttpMethod.Get, baseUri);
                        try
                        {
                            return response.IsSuccessStatusCode &&
                                   (response.Headers.TryGetValues("x-dry-run", out var dryRun) ||
                                    await _Evaluate(condition, response));
                        }
                        finally
                        {
                            response.Dispose();
                        }
                    });
                return (result ?? []).FirstOrDefault(false);
            }

            await client.WithRetry(expiration, descriptor, condition.ToString(), getResource);
        }

        private async Task<bool> _Evaluate(Manifest.AwaitCondition condition, HttpResponseMessage response)
        {
            var body = await response.Content.ReadAsStringAsync();
            try
            {
                return conditionJsonEvaluator.Evaluate(condition,
                    JsonSerializer.Deserialize<JsonElement>(body, Jsons.Options));
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
    }
}