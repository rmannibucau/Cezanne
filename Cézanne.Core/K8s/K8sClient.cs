using Microsoft.Extensions.Logging;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Cézanne.Core.K8s
{
    public class K8SClient : IDisposable
    {
        private readonly ILogger _logger;

        private Action? _refreshAuth;

        public K8SClient(K8SClientConfiguration configuration, ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger(typeof(K8SClient));

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
            HttpClient = new HttpClient(HttpMessageHandler) { Timeout = new TimeSpan(0, 0, configuration.Timeout) };
            HttpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json") { CharSet = "UTF-8" });

            string? baseUrl = "http://localhost:8080";
            if (configuration.Base.Length > 0)
            {
                baseUrl = configuration.Base;
            }

            if (configuration.Kubeconfig != null)
            {
                KubeConfig = _LoadFromKubeConfig(configuration.Kubeconfig);
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
        }

        public KubeConfig? KubeConfig { get; }
        public string? DefaultNamespace { get; private set; } = "default";
        public string? Base => HttpClient.BaseAddress?.ToString();

        public HttpClient HttpClient { get; }

        public HttpClientHandler HttpMessageHandler { get; }

        public void Dispose()
        {
            HttpClient.Dispose();
            foreach (X509Certificate clientCertificate in HttpMessageHandler.ClientCertificates)
            {
                clientCertificate.Dispose();
            }
        }

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relativeUri, string? content = null,
            string? contentType = null)
        {
            _refreshAuth?.Invoke();

            HttpRequestMessage message = new(method, relativeUri);
            if (content is not null)
            {
                message.Content =
                    new StringContent(content, Encoding.UTF8, HttpClient.DefaultRequestHeaders.Accept.First());
                message.Content.Headers.ContentType =
                    new MediaTypeHeaderValue(contentType ?? "application/json") { CharSet = "UTF-8" };
            }

            return await HttpClient.SendAsync(message, HttpCompletionOption.ResponseContentRead,
                new CancellationToken());
        }

        private string? _InitFromKubeConfig(
            HttpClientHandler httpMessageHandler,
            HttpRequestHeaders requestHeaders)
        {
            if (KubeConfig is null)
            {
                return null;
            }

            DefaultNamespace = KubeConfig.Namespace ?? "default";

            if (KubeConfig is not { CurrentContext: not null, Contexts: not null, Users: not null, Clusters: not null })
            {
                _logger.LogDebug("Skipping kubeconfig - empty");
                return null;
            }

            KubeConfig.NamedContext context = KubeConfig.Contexts.First(it => it.Name == KubeConfig.CurrentContext);
            if (context is not { Name: not null, Context: { User: not null, Cluster: not null } })
            {
                _logger.LogDebug("Skipping kubeconfig - no data");
                return null;
            }

            if (context.Context.Namespace is not null)
            {
                DefaultNamespace = context.Context.Namespace;
            }

            KubeConfig.NamedUser user = KubeConfig.Users
                .First(it => it.Name == context.Context.User);
            KubeConfig.NamedCluster cluster = KubeConfig.Clusters
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
                        X509Certificate2 pem = X509Certificate2.CreateFromPemFile(c, k);
                        httpMessageHandler.ClientCertificates.Add(pem);
                        break;
                    }
                case { ClientCertificateData: { } c, ClientKeyData: { } k }:
                    {
                        _logger.LogDebug("Using in memory mtls authentication");
                        X509Certificate2 pem = X509Certificate2.CreateFromPem(
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
                            string value = Convert.ToBase64String(
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
            DateTime validUntil = DateTime.Now.AddMinutes(1);
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
                string pem = Encoding.ASCII.GetString(Convert.FromBase64String(cluster.CertificateAuthorityData));
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

            if (obj.TryGetValue("namespace", out object? ns))
            {
                config.Namespace = ns as string;
            }

            if (obj.TryGetValue("current-context", out object? cc))
            {
                config.CurrentContext = cc as string;
            }

            if (obj.TryGetValue("skip-tls-verify", out object? stv))
            {
                config.SkipTlsVerify = bool.Parse(stv as string ?? "false");
            }

            if (obj.TryGetValue("clusters", out object? clusters))
            {
                config.Clusters = (clusters as IEnumerable<object> ?? [])
                    .Cast<IDictionary<string, object>>()
                    .Select(it =>
                    {
                        IDictionary<string, object> clusterData = it["cluster"] as IDictionary<string, object> ??
                                                                  ImmutableDictionary<string, object>.Empty;
                        return new KubeConfig.NamedCluster
                        {
                            Name = it["name"] as string,
                            Cluster = new KubeConfig.Cluster
                            {
                                Server =
                                    clusterData.TryGetValue("server", out object? server) ? server as string : Base,
                                InsecureSkipTlsVerify =
                                    clusterData.TryGetValue("insecure-skip-tls-verify", out object? istv) &&
                                    bool.Parse(istv as string ?? "false"),
                                CertificateAuthorityData =
                                    clusterData.TryGetValue("certificate-authority-data", out object? cad)
                                        ? cad as string
                                        : null,
                                CertificateAuthority = clusterData.TryGetValue("certificate-authority", out object? ca)
                                    ? ca as string
                                    : null
                            }
                        };
                    })
                    .ToList();
            }

            if (obj.TryGetValue("users", out object? users))
            {
                config.Users = (users as IEnumerable<object> ?? [])
                    .Cast<IDictionary<string, object>>()
                    .Select(it =>
                    {
                        IDictionary<string, object> user = it["user"] as IDictionary<string, object> ??
                                                           ImmutableDictionary<string, object>.Empty;
                        return new KubeConfig.NamedUser
                        {
                            Name = it["name"] as string,
                            User = new KubeConfig.User
                            {
                                Token = user.TryGetValue("token", out object? token) ? token as string : null,
                                TokenFile =
                                    user.TryGetValue("token", out object? tokenFile) ? tokenFile as string : null,
                                ClientCertificateData = user.TryGetValue("client-certificate-data", out object? ccd)
                                    ? ccd as string
                                    : null,
                                ClientKeyData =
                                    user.TryGetValue("client-key-data", out object? ckd) ? ckd as string : null,
                                ClientCertificate =
                                    user.TryGetValue("client-certificate", out object? clientCertificate)
                                        ? clientCertificate as string
                                        : null,
                                ClientKey =
                                    user.TryGetValue("client-key", out object? clientKey) ? clientKey as string : null,
                                Username =
                                    user.TryGetValue("username", out object? username) ? username as string : null,
                                Password = user.TryGetValue("username", out object? password)
                                    ? password as string
                                    : null
                            }
                        };
                    })
                    .ToList();
            }

            if (obj.TryGetValue("contexts", out object? contexts))
            {
                config.Contexts = (contexts as IEnumerable<object> ?? [])
                    .Cast<IDictionary<string, object>>()
                    .Select(it =>
                    {
                        IDictionary<string, object> contextData = it["context"] as IDictionary<string, object> ??
                                                                  ImmutableDictionary<string, object>.Empty;
                        return new KubeConfig.NamedContext
                        {
                            Name = it["name"] as string,
                            Context = new KubeConfig.Context
                            {
                                Cluster = contextData["cluster"] as string,
                                User = contextData["user"] as string,
                                Namespace = contextData.TryGetValue("namespace", out object? clusterNs)
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
}