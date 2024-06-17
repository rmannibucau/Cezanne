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
            string baseDir = Temp!;
            string bundlebee = Path.Combine(baseDir, "bundlebee");
            string kubernetes = Path.Combine(bundlebee, "kubernetes");
            string manifest = Path.Combine(bundlebee, "manifest.json");
            Directory.CreateDirectory(kubernetes);
            File.WriteAllText(manifest, """
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
            File.WriteAllText(Path.Combine(kubernetes, "descriptor.yaml"), """
                                                                           {
                                                                               "kind": "ConfigMap",
                                                                               "apiVersion": "v1",
                                                                               "metadata": {
                                                                                   "name": "test"
                                                                               },
                                                                               "data": {}
                                                                           }
                                                                           """);

            HashSet<string> requests = new HashSet<string>();
            WebApplication mockServer = WebApplication.Create();
            mockServer.Urls.Add("http://127.0.0.1:0");
            mockServer.Run(async ctx =>
            {
                lock (requests)
                {
                    requests.Add($"{ctx.Request.Method} {ctx.Request.Path}");
                }

                await ctx.Response.WriteAsJsonAsync(ImmutableDictionary<string, string?>.Empty);
            });
            await mockServer.StartAsync();
            await using WebApplication s = mockServer;

            new Cezanne
            {
                K8SClientConfiguration =
                    new K8SClientConfiguration { Kubeconfig = "skip", Base = mockServer.Urls.First() },
                MavenConfiguration = new MavenConfiguration
                {
                    // disable for this test
                    EnableDownload = false, LocalRepository = Path.Combine(baseDir, "m2")
                }
            }.Run(["apply", "-a", "test", "-m", manifest]);

            Assert.That(requests,
                Is.EqualTo(new HashSet<string>
                {
                    "GET /api/v1/namespaces/default/configmaps/test",
                    "PATCH /api/v1/namespaces/default/configmaps/test"
                }));
        }
    }
}