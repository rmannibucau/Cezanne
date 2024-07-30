using System.Text.Json.Serialization;

namespace Cezanne.Core.K8s
{
    public class KubeConfig
    {
        public IEnumerable<NamedContext>? Contexts { get; set; }
        public IEnumerable<NamedCluster>? Clusters { get; set; }
        public IEnumerable<NamedUser>? Users { get; set; }

        [JsonPropertyName("current-context")]
        public string? CurrentContext { get; set; }
        public string? Namespace { get; set; }

        [JsonPropertyName("skip-tls-verify")]
        public bool? SkipTlsVerify { get; set; }

        public class Cluster
        {
            // bundlebee compatibility
            [JsonPropertyName("insecure-skip-tls-verify")]
            public bool? InsecureSkipTlsVerify { get; set; }

            [JsonPropertyName("certificate-authority-data")]
            public string? CertificateAuthorityData { get; set; }

            [JsonPropertyName("certificate-authority")]
            public string? CertificateAuthority { get; set; }

            public string? Server { get; set; }
        }

        public class NamedCluster
        {
            public Cluster? Cluster { get; set; }
            public string? Name { get; set; }
        }

        public class NamedUser
        {
            public User? User { get; set; }
            public string? Name { get; set; }
        }

        public class User
        {
            [JsonPropertyName("client-certificate")]
            public string? ClientCertificate { get; set; }

            [JsonPropertyName("client-key")]
            public string? ClientKey { get; set; }

            [JsonPropertyName("client-certificate-data")]
            public string? ClientCertificateData { get; set; }

            [JsonPropertyName("client-key-data")]
            public string? ClientKeyData { get; set; }
            public string? Token { get; set; }
            public string? TokenFile { get; set; }
            public string? Username { get; set; }
            public string? Password { get; set; }
        }

        public class Context
        {
            public string? Cluster { get; set; }
            public string? User { get; set; }
            public string? Namespace { get; set; }
        }

        public class NamedContext
        {
            public Context? Context { get; set; }
            public string? Name { get; set; }
        }
    }
}
