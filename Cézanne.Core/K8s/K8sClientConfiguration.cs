using Cézanne.Core.Lang;
using System.ComponentModel;

namespace Cézanne.Core.K8s
{
    [ConfigurationPrefix("kubernetes")]
    [Description("Configuration of the Kubernetes client.")]
    public record K8SClientConfiguration
    {
        [Description("Kubernetes HTTP client timeout in milliseconds.")]
        public int Timeout { get; set; } = 60_000;

        [Description("Kubernetes HTTP API base url. It is auto-discovered from within a cluster.")]
        public string Base { get; set; } =
            $"https://{Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST") ?? "localhost"}:{Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_PORT") ?? "443"}";

        [Description("Kubernetes HTTP client token file location.")]
        public string Token { get; set; } = "/var/run/secrets/kubernetes.io/serviceaccount/token";

        [Description("Kubernetes HTTP client certificates.")]
        public string Certificates { get; set; } = "/var/run/secrets/kubernetes.io/serviceaccount/ca.crt";

        [Description("Kubernetes HTTP client private key (for mTLS when token is not set).")]
        public string? PrivateKey { get; set; }

        [Description("Kubernetes HTTP client private key certificate (for mTLS).")]
        public string? PrivateKeyCertificate { get; set; }

        [Description("Should TLS check be ignored.")]
        public bool SkipTls { get; set; } = false;

        [Description("Location or inline kubeconfig (default context being the one used).")]
        public string? Kubeconfig { get; set; }

        [Description(
            "JSON-Pointers of dropped JSON nodes from the descriptors (enables to inject documentation for example or schema for completion).")]
        public IEnumerable<string>? ImplicitlyDroppedAttributes { get; set; } =
            ["/$schema", "/$bundlebeeIgnoredLintingRules"];

        [Description("Should API calls be skipped and mocked with a HTTP 200.")]
        public bool DryRun { get; set; } = false;

        [Description("Should API calls be logged.")]
        public bool Verbose { get; set; } = false;
    }
}