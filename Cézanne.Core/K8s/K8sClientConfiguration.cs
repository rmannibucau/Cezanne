namespace Cézanne.Core.K8s
{
    public record K8SClientConfiguration
    {
        public int Timeout { get; set; } = 60_000;

        public string Base { get; set; } =
            $"https://{Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST") ?? "localhost"}:{Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_PORT") ?? "443"}";

        public string Token { get; set; } = "/var/run/secrets/kubernetes.io/serviceaccount/token";

        public string Certificates { get; set; } = "/var/run/secrets/kubernetes.io/serviceaccount/ca.crt";

        public string? PrivateKey { get; set; }

        public string? PrivateKeyCertificate { get; set; }

        public bool SkipTls { get; set; } = false;

        public string? Kubeconfig { get; set; }
    }
}