using Cézanne.Core.Cli;
using Cézanne.Core.K8s;
using Cézanne.Core.Maven;
using Cézanne.Core.Tests.Rule;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Collections.Immutable;

namespace Cézanne.Core.Tests.Cli
{
    public class CezanneTests : ITempFolder
    {
        public string? Temp { get; set; }

        [Test]
        [TempFolder]
        public async Task Apply()
        {
            (string baseDir, string manifest, string kubernetes) = _PrepareLayout();
            _WriteSimpleRecipe(manifest, kubernetes);

            (WebApplication mockServer, ISet<string> requests) = await _MockK8s();
            await using WebApplication _ = mockServer;

            _Cezanne(baseDir, mockServer).Run(["apply", "-a", "test", "-m", manifest]);

            Assert.That(requests,
                Is.EqualTo(new HashSet<string>
                {
                    "GET /api/v1",
                    "GET /api/v1/namespaces/default/configmaps/test",
                    "PATCH /api/v1/namespaces/default/configmaps/test"
                }));
        }

        [Test]
        [TempFolder]
        public async Task Delete()
        {
            (string baseDir, string manifest, string kubernetes) = _PrepareLayout();
            _WriteSimpleRecipe(manifest, kubernetes);

            (WebApplication mockServer, ISet<string> requests) = await _MockK8s();
            await using WebApplication _ = mockServer;

            _Cezanne(baseDir, mockServer).Run(["delete", "-a", "test", "-m", manifest]);

            Assert.That(requests,
                Is.EqualTo(new HashSet<string>
                {
                    "GET /api/v1",
                    "DELETE /api/v1/namespaces/default/configmaps/test",
                    "GET /api/v1/namespaces/default/configmaps/test"
                }));
        }

        private void _WriteSimpleRecipe(string manifest, string kubernetes)
        {
            Directory.CreateDirectory(kubernetes);
            File.WriteAllText(
                manifest,
                """
                {
                    "recipes": [
                        {
                            "name": "test",
                            "descriptors": [
                                {
                                    "name": "descriptor.yaml"
                                }
                            ]
                        }
                    ]
                }
                """);
            File.WriteAllText(
                Path.Combine(kubernetes, "descriptor.yaml"),
                """
                {
                    "kind": "ConfigMap",
                    "apiVersion": "v1",
                    "metadata": {
                        "name": "test"
                    },
                    "data": {}
                }
                """);
        }

        private async Task<(WebApplication, ISet<string>)> _MockK8s()
        {
            HashSet<string> requests = new();
            WebApplication mockServer = WebApplication.Create();
            mockServer.Urls.Add("http://127.0.0.1:0");
            mockServer.Run(async ctx =>
            {
                string current = $"{ctx.Request.Method} {ctx.Request.Path}";
                lock (requests)
                {
                    requests.Add(current);
                }

                if (current.StartsWith("GET "))
                {
                    bool return404;
                    lock (requests)
                    {
                        return404 = requests.Contains($"DELETE {ctx.Request.Path}");
                    }

                    if (return404)
                    {
                        ctx.Response.StatusCode = 404;
                    }
                }

                await ctx.Response.WriteAsJsonAsync(ImmutableDictionary<string, string?>.Empty);
            });
            await mockServer.StartAsync();
            return (mockServer, requests);
        }

        private (string, string, string) _PrepareLayout()
        {
            string baseDir = Temp!;
            string bundlebee = Path.Combine(baseDir, "bundlebee");
            string kubernetes = Path.Combine(bundlebee, "kubernetes");
            string manifest = Path.Combine(bundlebee, "manifest.json");
            return (baseDir, manifest, kubernetes);
        }

        private Cezanne _Cezanne(string baseDir, WebApplication mockServer)
        {
            return new Cezanne
            {
                K8SClientConfiguration =
                    new K8SClientConfiguration { Kubeconfig = "skip", Base = mockServer.Urls.First() },
                MavenConfiguration = new MavenConfiguration
                {
                    // disable for this test
                    EnableDownload = false, LocalRepository = Path.Combine(baseDir, "m2")
                }
            };
        }
    }
}