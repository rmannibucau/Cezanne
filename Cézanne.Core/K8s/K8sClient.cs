using Cézanne.Core.Runtime;
using Cézanne.Core.Service;
using Json.Patch;
using Json.Pointer;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cézanne.Core.K8s
{
    public class K8SClient : IAsyncDisposable, IDisposable
    {
        private readonly ApiPreloader _apiPreloader;
        private readonly IEnumerable<JsonPatch> _droppedAttributes;
        private readonly bool _dryRun;
        private readonly ILogger<K8SClient> _logger;
        private readonly bool _verbose;
        private volatile bool _disposed;
        private Action? _refreshAuth;

        public K8SClient(K8SClientConfiguration configuration, ILogger<K8SClient> logger,
            ILogger<ApiPreloader> apiPreloaderLogger)
        {
            _logger = logger;
            _apiPreloader = new ApiPreloader(apiPreloaderLogger, this);
            _droppedAttributes = (configuration.ImplicitlyDroppedAttributes ?? [])
                .Select(path => new JsonPatch(PatchOperation.Remove(JsonPointer.Parse(path))));
            _dryRun = configuration.DryRun;
            _verbose = configuration.Verbose;

            if (File.Exists("/var/run/secrets/kubernetes.io/serviceaccount/namespace"))
            {
                DefaultNamespace = File.ReadAllText("/var/run/secrets/kubernetes.io/serviceaccount/namespace").Trim();
            }

            HttpMessageHandler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5
            };
            HttpClient =
                new HttpClient(HttpMessageHandler) { Timeout = TimeSpan.FromMilliseconds(configuration.Timeout) };
            HttpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json") { CharSet = "UTF-8" });

            var baseUrl = "http://localhost:8080";
            if (configuration.Base.Length > 0)
            {
                baseUrl = configuration.Base;
            }

            var kubeconfig = configuration.Kubeconfig ??
                             Environment.GetEnvironmentVariable("KUBECONFIG") ??
                             Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                 ".kube/config");
            if (kubeconfig != null && kubeconfig != "skip")
            {
                KubeConfig = _LoadFromKubeConfig(kubeconfig);
                if (KubeConfig != null)
                {
                    baseUrl = _InitFromKubeConfig(HttpMessageHandler, HttpClient.DefaultRequestHeaders) ??
                              baseUrl;
                }
            }

            if (configuration.Certificates.Length > 0 && File.Exists(configuration.Certificates))
            {
                _logger.LogDebug("Loading certificate '{Certificate}'", configuration.Certificates);
                HttpMessageHandler.ClientCertificates.Add(new X509Certificate2(configuration.Certificates));
            }

            if (configuration.PrivateKey is not null && configuration.PrivateKeyCertificate is not null)
            {
                _logger.LogDebug("Loading certificate '{Certificate}' with private key '{PrivateKey}'",
                    configuration.PrivateKeyCertificate, configuration.PrivateKey);
                HttpMessageHandler.ClientCertificates.Add(
                    X509Certificate2.CreateFromPemFile(configuration.PrivateKeyCertificate, configuration.PrivateKey));
            }

            if (_refreshAuth is null && File.Exists(configuration.Token))
            {
                _InitTokenCallback(HttpMessageHandler, HttpClient.DefaultRequestHeaders, configuration.Token);
            }

            if (configuration.SkipTls && HttpMessageHandler.ServerCertificateCustomValidationCallback is null)
            {
                _logger.LogDebug("Skipping TLS checks");
                HttpMessageHandler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
            }
            else if (HttpMessageHandler.ServerCertificateCustomValidationCallback is null &&
                     HttpMessageHandler.ClientCertificates.Count > 1 /* likely means custom authority */)
            {
                HttpMessageHandler.ServerCertificateCustomValidationCallback = (_, cert, chain, errors) =>
                {
                    if (errors == SslPolicyErrors.None)
                    {
                        return true;
                    }

                    if ((errors & SslPolicyErrors.RemoteCertificateChainErrors) == 0 || chain == null || cert == null)
                    {
                        return false;
                    }

                    // cluster CA is last one - check order we append them
                    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    chain.ChainPolicy.CustomTrustStore.Add(HttpMessageHandler.ClientCertificates[^1]);

                    return chain.Build(cert);
                };
            }

            HttpClient.BaseAddress = new Uri(baseUrl ?? throw new ArgumentException("No base url found"));

            _logger.LogDebug("Using base url '{BaseUrl}'", baseUrl);

            if (_dryRun)
            {
                _logger.LogInformation("Execution will use dry-run mode");
            }
        }

        public KubeConfig? KubeConfig { get; }
        public string? DefaultNamespace { get; private set; } = "default";
        public string? Base => HttpClient.BaseAddress?.ToString();

        public HttpClient HttpClient { get; }

        public HttpClientHandler HttpMessageHandler { get; }

        public async ValueTask DisposeAsync()
        {
            lock (this)
            {
                var alreadyDisposed = _disposed;
                if (alreadyDisposed)
                {
                    return;
                }

                _disposed = true;
            }

            await _apiPreloader.DisposeAsync();
            HttpClient.Dispose();
            foreach (var clientCertificate in HttpMessageHandler.ClientCertificates)
            {
                clientCertificate.Dispose();
            }
        }

        public void Dispose()
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        public async Task<HttpResponseMessage> SendAsync(
            HttpMethod method,
            string relativeUri,
            string? content = null,
            string? contentType = null)
        {
            _refreshAuth?.Invoke();

            HttpRequestMessage message = new(method, relativeUri);
            if (content is not null)
            {
                MediaTypeHeaderValue json = new(contentType ?? "application/json") { CharSet = "UTF-8" };
                message.Content = new StringContent(content, Encoding.UTF8, json);
                message.Content.Headers.ContentType = json;
            }

            HttpResponseMessage response;
            if (_dryRun)
            {
                // very simplistic client "mock"
                response = await Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Version = new Version(1, 1),
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                });
                response.Headers.Add("x-dry-run",
                    "true"); // flag for delete cases, we need another check than the status
            }
            else
            {
                response = await HttpClient.SendAsync(message, HttpCompletionOption.ResponseContentRead,
                    new CancellationToken());
            }

            if (_verbose)
            {
                var responseBody = await response.Content.ReadAsStringAsync() ?? "";
                response.Content = new StringContent(responseBody,
                    response.Content.Headers.ContentType ??
                    new MediaTypeHeaderValue(contentType ?? "application/json") { CharSet = "UTF-8" });
                _logger.LogInformation(
                    "{Method} {Uri}{RequestHeaders}\n\n{Payload}HTTP/{Version} {Status} {StatusReason}{ResponseHeaders}\n\n{ResponsePayload}",
                    method, relativeUri, _FormatHeaders(message.Headers), content?.Length > 0 ? content + "\n\n" : "",
                    response.Version, (int)response.StatusCode, response.StatusCode, _FormatHeaders(response.Headers),
                    responseBody);
            }

            return response;
        }

        private string _FormatHeaders(HttpHeaders headers)
        {
            if (!headers.Any())
            {
                return "";
            }

            return "\n" + string.Join("\n",
                headers.OrderBy(it => it.Key).Select(it => $"{it.Key}: {string.Join(',', it.Value)}"));
        }

        public async Task<string> ToBaseUri(JsonObject prepared)
        {
            var kindLowerCased = prepared["kind"]!.ToString().ToLowerInvariant() + 's';
            var metadata = prepared["metadata"]!.AsObject();
            var namespaceValue =
                (metadata.TryGetPropertyValue("namespace", out var ns) && ns is not null
                    ? ns.ToString()
                    : DefaultNamespace) ?? "default";

            await _apiPreloader.EnsureResourceSpec(prepared, kindLowerCased);
            var specPrefix = _apiPreloader[kindLowerCased];
            if (specPrefix is not null)
            {
                specPrefix =
                    (specPrefix.StartsWith('/') ? specPrefix[1..] : specPrefix).Replace("${namespace}", namespaceValue,
                        StringComparison.Ordinal);
                return $"{Base}{specPrefix}";
            }

            var nsSegment = _IsSkipNameSpace(kindLowerCased) ? "" : "/namespaces/" + namespaceValue + '/';
            var prefix = _FindApiPrefix(kindLowerCased, prepared);
            prefix = prefix.StartsWith('/') ? prefix[1..] : prefix;
            return $"{Base}{prefix}{nsSegment}{kindLowerCased}";
        }

        public async Task<IEnumerable<T>> ForDescriptor<T>(string descriptorContent, string extension,
            Func<DescriptorItem, Task<T>> handler)
        {
            try
            {
                var json = extension switch
                {
                    "json" => JsonSerializer.Deserialize<JsonNode>(descriptorContent, Jsons.Options),
                    _ => Jsons.FromYaml(descriptorContent)
                };
                switch (json?.GetValueKind())
                {
                    case JsonValueKind.Array:
                        {
                            var results = await Task.WhenAll(json.AsArray()
                                .GetValues<JsonObject>()
                                .Select(async it =>
                                {
                                    DescriptorItem sanitized = new(it, _SanitizeJson(it));
                                    return await handler(sanitized);
                                }));
                            return results.ToList();
                        }
                    case JsonValueKind.Object:
                        {
                            var value = json.AsObject();
                            DescriptorItem sanitized = new(value, _SanitizeJson(value));
                            var result = await handler(sanitized);
                            return [result];
                        }
                    default:
                        throw new InvalidOperationException($"Invalid descriptor {descriptorContent}");
                }
            }
            catch (Exception e)
            {
                _logger.LogError("Can't read\n{Descriptor}\n\n{Exception}", descriptorContent, e);
                throw;
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
                "deployments" or "statefulsets" or "daemonsets" or "replicasets" or "controllerrevisions" =>
                    "/apis/apps/v1",
                "cronjobs" => "/apis/batch/v1beta1",
                "apiservices" => "/apis/apiregistration.k8s.io/v1",
                "customresourcedefinitions" => "/apis/apiextensions.k8s.io/v1beta1",
                "mutatingwebhookconfigurations" or "validatingwebhookconfigurations" =>
                    "/apis/admissionregistration.k8s.io/v1",
                "roles" or "rolebindings" or "clusterroles" or "clusterrolebindings" => "/apis/" + desc["apiVersion"]!,
                _ => "/api/v1"
            };
        }

        public async Task WithRetry(DateTime expiration,
            LoadedDescriptor descriptor,
            string timeoutMarker,
            Func<Task<bool>> evaluator)
        {
            while (true)
            {
                _logger.LogTrace("waiting for {Descriptor}", descriptor);
                var result = false;
                try
                {
                    result = await evaluator();
                    if (result)
                    {
                        _logger.LogTrace("Awaited for {Descriptor}, succeeded", descriptor);
                        return;
                    }
                }
                catch (Exception e)
                {
                    _logger.LogTrace("waiting for {Descriptor}: {Error}", descriptor, e);
                }

                if (!result)
                {
                    if (DateTime.UtcNow > expiration)
                    {
                        throw new InvalidOperationException(
                            $"Timeout on condition: {descriptor.Configuration} reached: {timeoutMarker}");
                    }

                    _logger.LogTrace("Will retry awaiting for {Descriptor}", descriptor);
                    Thread.Sleep(500); // todo: configuration
                }
            }
        }

        private JsonObject _SanitizeJson(JsonObject value)
        {
            var result = value;
            foreach (var patch in _droppedAttributes)
            {
                try
                {
                    var applied = patch.Apply(result);
                    if (applied.IsSuccess && applied.Result is not null)
                    {
                        result = applied.Result.AsObject();
                    }
                }
                catch (Exception)
                {
                    // no-op
                }
            }

            return value;
        }

        private string? _InitFromKubeConfig(
            HttpClientHandler httpMessageHandler,
            HttpRequestHeaders requestHeaders)
        {
            if (KubeConfig is null || string.IsNullOrEmpty(KubeConfig.CurrentContext))
            {
                return null;
            }

            DefaultNamespace = KubeConfig.Namespace ?? "default";

            if (KubeConfig is not { CurrentContext: not null, Contexts: not null, Users: not null, Clusters: not null })
            {
                _logger.LogDebug("Skipping kubeconfig - empty");
                return null;
            }

            var context =
                KubeConfig.Contexts.FirstOrDefault(it => it?.Name == KubeConfig.CurrentContext, null) ??
                throw new InvalidOperationException("No kubeconfig context available");
            if (context is not { Name: not null, Context: { User: not null, Cluster: not null } })
            {
                _logger.LogDebug("Skipping kubeconfig - no data");
                return null;
            }

            if (context.Context.Namespace is not null)
            {
                DefaultNamespace = context.Context.Namespace;
            }

            var user = KubeConfig.Users
                .First(it => it.Name == context.Context.User);
            var cluster = KubeConfig.Clusters
                .First(it => it.Name == context.Context.Cluster);

            if (cluster.Cluster?.InsecureSkipTlsVerify is true)
            {
                _logger.LogDebug("Using unsafe SSL mode");
                HttpMessageHandler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
            }

            switch (user.User)
            {
                case { ClientCertificate: { } c, ClientKey: { } k }:
                    {
                        _logger.LogDebug("Using in mtls authentication");
                        var pem = X509Certificate2.CreateFromPemFile(c, k);
                        httpMessageHandler.ClientCertificates.Add(pem);
                        break;
                    }
                case { ClientCertificateData: { } c, ClientKeyData: { } k }:
                    {
                        _logger.LogDebug("Using in memory mtls authentication");
                        var pem = X509Certificate2.CreateFromPem(
                            Encoding.ASCII.GetString(Convert.FromBase64String(c)),
                            Encoding.ASCII.GetString(Convert.FromBase64String(k)));
                        httpMessageHandler.ClientCertificates.Add(pem);
                        break;
                    }
                default:
                    {
                        if (user.User?.Token is not null)
                        {
                            _logger.LogDebug("Using token authentication");
                            requestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", user.User.Token);
                        }
                        else if (user.User?.TokenFile is not null)
                        {
                            _InitTokenCallback(httpMessageHandler, requestHeaders, user.User.TokenFile);
                        }
                        else if (user.User is { Username: not null, Password: not null })
                        {
                            _logger.LogDebug("Using username/password authentication");
                            var value = Convert.ToBase64String(
                                Encoding.ASCII.GetBytes($"{user.User.Username}:{user.User.Password}"));
                            requestHeaders.Authorization = new AuthenticationHeaderValue("Basic", value);
                        }

                        break;
                    }
            }

            if (cluster.Cluster is not null)
            {
                _TryAddingClusterCertificate(httpMessageHandler, cluster.Cluster);
            }

            return cluster.Cluster?.Server;
        }

        private void _InitTokenCallback(HttpClientHandler httpMessageHandler, HttpRequestHeaders requestHeaders,
            string tokenFile)
        {
            _logger.LogDebug("Using token file authentication");
            var validUntil = DateTime.Now.AddMinutes(1);
            _refreshAuth = () =>
            {
                if (File.Exists(tokenFile) &&
                    (requestHeaders.Authorization is null || validUntil < DateTime.Now))
                {
                    lock (httpMessageHandler)
                    {
                        if (!File.Exists(tokenFile) ||
                            (requestHeaders.Authorization is not null && validUntil >= DateTime.Now))
                        {
                            return;
                        }

                        requestHeaders.Authorization =
                            new AuthenticationHeaderValue("Bearer", File.ReadAllText(tokenFile).Trim());
                        validUntil = DateTime.Now.AddMinutes(1);
                    }

                    _logger.LogDebug("Refreshed token using '{TokenPath}' file", tokenFile);
                }
            };
        }

        private void _TryAddingClusterCertificate(HttpClientHandler httpMessageHandler, KubeConfig.Cluster cluster)
        {
            if (cluster.CertificateAuthorityData is not null)
            {
                var pem = Encoding.ASCII.GetString(Convert.FromBase64String(cluster.CertificateAuthorityData));
                httpMessageHandler.ClientCertificates.Add(X509Certificate2.CreateFromPem(pem));
            }
            else if (cluster.CertificateAuthority is not null && File.Exists(cluster.CertificateAuthority))
            {
                X509Certificate2 x509 = new(cluster.CertificateAuthority);
                httpMessageHandler.ClientCertificates.Add(x509);
            }
        }

        private KubeConfig? _LoadFromKubeConfig(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using FileStream stream = new(path, FileMode.Open, FileAccess.Read);
            if (path.EndsWith(".json"))
            {
                return JsonSerializer.Deserialize<KubeConfig>(stream, new JsonSerializerOptions()) ??
                       new KubeConfig();
            }

            // assume yaml
            return _LoadKubeConfigFromYaml(stream);
        }

        private KubeConfig _LoadKubeConfigFromYaml(FileStream stream)
        {
            object untypedConfig;
            using (StreamReader reader = new(stream))
            {
                untypedConfig = new LightYamlParser().Parse(reader);
            }

            if (untypedConfig is not IDictionary<string, object> obj)
            {
                throw new ArgumentException(
                    $"Unsupported kubeconfig type - only objects are supported: {untypedConfig}");
            }

            // custom mapping until we impl the light parser with reflection reusing json model/typeinfo?
            KubeConfig config = new();

            if (obj.TryGetValue("namespace", out var ns))
            {
                config.Namespace = ns as string;
            }

            if (obj.TryGetValue("current-context", out var cc))
            {
                config.CurrentContext = cc as string;
            }

            if (obj.TryGetValue("skip-tls-verify", out var stv))
            {
                config.SkipTlsVerify = bool.Parse(stv as string ?? "false");
            }

            if (obj.TryGetValue("clusters", out var clusters))
            {
                config.Clusters = (clusters as IEnumerable<object> ?? [])
                    .Cast<IDictionary<string, object>>()
                    .Select(it =>
                    {
                        var clusterData = it["cluster"] as IDictionary<string, object> ??
                                          ImmutableDictionary<string, object>.Empty;
                        return new KubeConfig.NamedCluster
                        {
                            Name = it["name"] as string,
                            Cluster = new KubeConfig.Cluster
                            {
                                Server =
                                    clusterData.TryGetValue("server", out var server) ? server as string : Base,
                                InsecureSkipTlsVerify =
                                    clusterData.TryGetValue("insecure-skip-tls-verify", out var istv) &&
                                    bool.Parse(istv as string ?? "false"),
                                CertificateAuthorityData =
                                    clusterData.TryGetValue("certificate-authority-data", out var cad)
                                        ? cad as string
                                        : null,
                                CertificateAuthority = clusterData.TryGetValue("certificate-authority", out var ca)
                                    ? ca as string
                                    : null
                            }
                        };
                    })
                    .ToList();
            }

            if (obj.TryGetValue("users", out var users))
            {
                config.Users = (users as IEnumerable<object> ?? [])
                    .Cast<IDictionary<string, object>>()
                    .Select(it =>
                    {
                        var user = it["user"] as IDictionary<string, object> ??
                                   ImmutableDictionary<string, object>.Empty;
                        return new KubeConfig.NamedUser
                        {
                            Name = it["name"] as string,
                            User = new KubeConfig.User
                            {
                                Token = user.TryGetValue("token", out var token) ? token as string : null,
                                TokenFile =
                                    user.TryGetValue("token", out var tokenFile) ? tokenFile as string : null,
                                ClientCertificateData = user.TryGetValue("client-certificate-data", out var ccd)
                                    ? ccd as string
                                    : null,
                                ClientKeyData =
                                    user.TryGetValue("client-key-data", out var ckd) ? ckd as string : null,
                                ClientCertificate =
                                    user.TryGetValue("client-certificate", out var clientCertificate)
                                        ? clientCertificate as string
                                        : null,
                                ClientKey =
                                    user.TryGetValue("client-key", out var clientKey) ? clientKey as string : null,
                                Username =
                                    user.TryGetValue("username", out var username) ? username as string : null,
                                Password = user.TryGetValue("username", out var password)
                                    ? password as string
                                    : null
                            }
                        };
                    })
                    .ToList();
            }

            if (obj.TryGetValue("contexts", out var contexts))
            {
                config.Contexts = (contexts as IEnumerable<object> ?? [])
                    .Cast<IDictionary<string, object>>()
                    .Select(it =>
                    {
                        var contextData = it["context"] as IDictionary<string, object> ??
                                          ImmutableDictionary<string, object>.Empty;
                        return new KubeConfig.NamedContext
                        {
                            Name = it["name"] as string,
                            Context = new KubeConfig.Context
                            {
                                Cluster = contextData["cluster"] as string,
                                User = contextData["user"] as string,
                                Namespace = contextData.TryGetValue("namespace", out var clusterNs)
                                    ? clusterNs as string
                                    : DefaultNamespace
                            }
                        };
                    })
                    .ToList();
            }

            return config;
        }
    }

    public record DescriptorItem(JsonObject Raw, JsonObject Prepared)
    {
    }
}