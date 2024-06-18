using Cézanne.Core.K8s;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cézanne.Core.Service
{
    public class ApiPreloader(ILogger<ApiPreloader> logger, K8SClient k8s) : IAsyncDisposable
    {
        private readonly IDictionary<string, string> _baseUrls = new ConcurrentDictionary<string, string>();
        private readonly ISet<string> _fetchedResources = new HashSet<string>();

        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly IDictionary<string, Task> _pendingFetches = new ConcurrentDictionary<string, Task>();

        public string? this[string key] => _baseUrls.TryGetValue(key, out string? url) ? url : null;

        public async ValueTask DisposeAsync()
        {
            await Task.WhenAll(_pendingFetches.Values);
        }

        public async Task EnsureResourceSpec(JsonObject desc, string kindLowerCased)
        {
            string basePath;
            if (!_baseUrls.ContainsKey(kindLowerCased) &&
                desc.TryGetPropertyValue("apiVersion", out JsonNode? apiVersion) && apiVersion?.ToString() != "v1")
            {
                basePath = "/apis/" + desc["apiVersion"];
            }
            else
            {
                basePath = "/api/v1";
            }

            await _ChainedAPIResourceListFetch(basePath, async () =>
            {
                HttpResponseMessage response = await k8s.SendAsync(HttpMethod.Get, basePath);
                await _ProcessResourceListDefinition(basePath, response);
            });
        }

        private async Task _ProcessResourceListDefinition(string basePath, HttpResponseMessage response)
        {
            logger.LogTrace("Fetched {Response}", response);
            await Task.CompletedTask; // enforce one await to get an awaiter
            try
            {
                switch (response.StatusCode)
                {
                    case HttpStatusCode.OK:
                        APIResourceList? list =
                            JsonSerializer.Deserialize<APIResourceList>(await response.Content.ReadAsStringAsync(),
                                _jsonOptions);
                        if (list is not null)
                        {
                            foreach (APIResource resource in
                                     list.Resources.OrderBy(it => it.Name ?? it.SingularName ?? "z"))
                            {
                                string key = resource.Kind.ToLowerInvariant() + 's';
                                string value = (resource is { Group: not null, Version: not null }
                                        ? $"/apis/{resource.Group}/{resource.Version}"
                                        : basePath) +
                                    (resource.Namespaced ? "/namespaces/${namespace}" : "") +
                                    '/' + (!string.IsNullOrWhiteSpace(resource.Name)
                                        ? resource.Name
                                        : resource.SingularName) ?? "";
                                if (_baseUrls.TryAdd(key, value))
                                {
                                    logger.LogTrace("Registered API {Key}={Value}", key, value);
                                }
                                else
                                {
                                    logger.LogTrace("API {Key} already present, ignoring {Value}", key, value);
                                }
                            }
                        }

                        break;
                    case HttpStatusCode.NotFound:
                        logger.LogWarning("Didn't find apiVersion '{Response}'", response);
                        break;
                    default:
                        logger.LogTrace("Can't get apiVersion '{Response}'\n{Body}", response,
                            await response.Content.ReadAsStringAsync());
                        break;
                }
            }
            finally
            {
                response.Dispose();
            }
        }


        private async Task _ChainedAPIResourceListFetch(string marker, Func<Task> supplier)
        {
            bool missing;
            lock (this)
            {
                missing = _fetchedResources.Add(marker) is false;
            }

            if (missing)
            {
                Task pending = _pendingFetches.TryGetValue(marker, out Task? p) ? p : Task.CompletedTask;
                await pending;
                return;
            }

            Task current = supplier();
            _pendingFetches.Add(marker, current);
            try
            {
                await current;
            }
            catch (Exception e)
            {
                logger.LogError(e, "An error occurred preloading {Api}: {Exception}", marker, e.Message);
            }
            finally
            {
                _pendingFetches.Remove(marker);
            }
        }

        public record APIResourceList(string Kind, string ApiVersion, IEnumerable<APIResource> Resources)
        {
        }

        public record APIResource(
            string? Group,
            string Kind,
            string? Name,
            string? SingularName,
            string? Version,
            bool Namespaced,
            IEnumerable<string> Verb)
        {
        }
    }
}