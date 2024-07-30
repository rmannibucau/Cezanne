using Cezanne.Core.K8s;

namespace Cezanne.Core.Tests.K8s
{
    public class LightYamlParserTests
    {
        [Test]
        public void Parse()
        {
            object? result;
            using (
                StringReader reader =
                    new(
                        """
                        apiVersion: v1
                        clusters:
                        - cluster:
                            certificate-authority-data: aaaaaaaaaaaa
                            server: https://1.2.3.4:8443
                          name: k0s
                        contexts:
                        - context:
                            cluster: k0s
                            namespace: dev
                            user: user
                          name: k0s
                        current-context: "k0s"
                        kind: Config
                        preferences: {}
                        users:
                        - name: user
                          user:
                            client-certificate-data: bbbbbbb
                            client-key-data: ccccccccccc
                        """
                    )
            )
            {
                result = new LightYamlParser().Parse(reader);
            }

            IDictionary<string, object> expected = new Dictionary<string, object>
            {
                { "apiVersion", "v1" },
                {
                    "clusters",
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            {
                                "cluster",
                                new Dictionary<string, object>
                                {
                                    { "certificate-authority-data", "aaaaaaaaaaaa" },
                                    { "server", "https://1.2.3.4:8443" }
                                }
                            },
                            { "name", "k0s" }
                        }
                    }
                },
                {
                    "contexts",
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            {
                                "context",
                                new Dictionary<string, object>
                                {
                                    { "cluster", "k0s" },
                                    { "namespace", "dev" },
                                    { "user", "user" }
                                }
                            },
                            { "name", "k0s" }
                        }
                    }
                },
                { "current-context", "k0s" },
                { "kind", "Config" },
                { "preferences", new Dictionary<string, object>() },
                {
                    "users",
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            {
                                "user",
                                new Dictionary<string, object>
                                {
                                    { "client-certificate-data", "bbbbbbb" },
                                    { "client-key-data", "ccccccccccc" }
                                }
                            },
                            { "name", "user" }
                        }
                    }
                }
            };
            Assert.That(result, Is.EqualTo(expected));
        }
    }
}
