using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Cézanne.Core.Cli;
using Cézanne.Core.K8s;
using Cézanne.Core.Maven;
using Cézanne.Core.Tests.Rule;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Cézanne.Core.Tests.Cli
{
    public class CezanneTests : ITempFolder
    {
        public string? Temp { get; set; }

        [Test]
        [Sequential]
        [TempFolder]
        public void CompletionBash(
            [Values(
                [
                    "cezanne ",
                    "cezanne l",
                    "cezanne del",
                    "cezanne apply ",
                    "cezanne apply --chain ",
                    "cezanne apply --chain t",
                    "cezanne placeholder-extract --o",
                    "cezanne placeholder-extract --output-type "
                ]
            )]
                string command,
            [Values([8, 8, 10, 14, 21, 22, 30, 41])] int position,
            [Values(
                [
                    "apply\ndelete\ninspect\nlist-recipes\nplaceholder-extract\n",
                    "apply\ndelete\ninspect\nlist-recipes\nplaceholder-extract\n", // the completion script will filter but we completed the commands
                    "apply\ndelete\ninspect\nlist-recipes\nplaceholder-extract\n",
                    "--alveolus\n--await-timeout\n--chain\n--dry-run\n--excluded-descriptors\n--excluded-locations\n--field-validation\n--from\n--log-descriptors\n--manifest\n--recipe\n--update-statefulset-spec-attributes\n-a\n-f\n-m\n-r\n",
                    "true\nfalse\n",
                    "true\nfalse\n",
                    "--alveolus\n--asciidoc\n--completion\n--descriptions\n--descriptor\n--dump\n--fail-on-invalid\n--from\n--ignored-placeholders\n--json\n--manifest\n--markdown\n--output-type\n--properties\n--recipe\n-a\n-d\n-e\n-f\n-m\n-o\n-r\n-t\n-x\n",
                    "ARGOCD\nCONSOLE\nFILE\nLOG\n"
                ]
            )]
                string completion
        )
        {
            var (baseDir, _, _) = _PrepareLayout();
            var stdout = Console.Out;
            var output = new StringWriter();
            try
            {
                Console.SetOut(output);

                _Cezanne(baseDir)
                    .Run(["completion", "bash", "--position", position.ToString(), command]);

                output.Close();
                var result = output.ToString().Replace("\r\n", "\n");
                Assert.That(result, Is.EqualTo(completion));
            }
            finally
            {
                Console.SetOut(stdout);
            }
        }

        [Test]
        [TempFolder]
        public void PlaceholderExtract()
        {
            var (baseDir, manifest, kubernetes) = _PrepareLayout();
            _WriteSimpleRecipe(
                manifest,
                kubernetes,
                """
                {
                    "recipes": [
                        {
                            "name": "test",
                            "descriptors": [
                                {
                                    "interpolate": true,
                                    "name": "descriptor.yaml"
                                }
                            ]
                        }
                    ]
                }
                """,
                """
                {
                    "kind": "ConfigMap",
                    "apiVersion": "v1",
                    "metadata": {
                        "name": "test"
                    },
                    "data": {
                        "custom1":"{{my.custom.value}}",
                        "custom2":"{{other.value:-fallback}}"
                    }
                }
                """
            );

            var descriptions = $"{baseDir}/descriptions.properties";
            File.WriteAllText(
                descriptions,
                "# descriptions\n\nmy.custom.value=Main placeholder.\nother.value=Some other value.\n"
            );

            _Cezanne(baseDir)
                .Run(
                    [
                        "placeholder-extract",
                        "-m",
                        manifest,
                        "--descriptions",
                        descriptions,
                        "-t",
                        "FILE",
                        "-o",
                        baseDir
                    ]
                );

            _AssertFile(
                $"{baseDir}/placeholders.adoc",
                """
                `my.custom.value`*::
                Main placeholder.


                `other.value`::
                Some other value.
                Default: `fallback`.
                """.Replace("\r\n", "\n")
            );
            _AssertFile(
                $"{baseDir}/placeholders.md",
                """
                `my.custom.value`*
                :   Main placeholder.


                `other.value`
                :   Some other value.
                    
                    Default: `fallback`.

                """.Replace("\r\n", "\n")
            );
            _AssertFile(
                $"{baseDir}/placeholders.json",
                """{"items":[{"name":"my.custom.value","description":"Main placeholder.","required":true,"defaultValues":[]},{"name":"other.value","description":"Some other value.","required":false,"defaultValue":"fallback","defaultValues":["fallback"]}]}"""
            );
            _AssertFile(
                $"{baseDir}/placeholders.properties",
                """
                # HELP: Main placeholder.
                #my.custom.value = 

                # HELP: Some other value.
                #other.value = fallback

                """.Replace("\r\n", "\n")
            );
            _AssertFile(
                $"{baseDir}/placeholders.completion.properties",
                """
                my.custom.value = Main placeholder.

                other.value = Some other value.

                """.Replace("\r\n", "\n")
            );
        }

        [Test]
        [TempFolder]
        public void Inspect()
        {
            var (baseDir, manifest, kubernetes) = _PrepareLayout();
            _WriteSimpleRecipe(manifest, kubernetes);

            AnsiConsole.Record();
            _Cezanne(baseDir).Run(["inspect", "-m", manifest]);

            var result = AnsiConsole.ExportCustom(new Exporter());
            Assert.That(
                Regex.Replace(
                    Regex.Replace(result, "\\r?\\n[\\r?\\n]*", "\r\n", RegexOptions.Multiline),
                    @"├── From[^└]*",
                    "├── From -\r\n            ",
                    RegexOptions.Multiline
                ),
                Is.EqualTo(
                    """
                    [   info][      Cézanne.Core.Service.RecipeHandler] Inspecting 'test'
                    Inspection Result
                    └── test
                        └── Descriptors
                            └── descriptor.yaml
                                ├── From -
                                └── ╭─Content─────────────────╮
                                    │ {                       │
                                    │    "kind": "ConfigMap", │
                                    │    "apiVersion": "v1",  │
                                    │    "metadata": {        │
                                    │       "name": "test"    │
                                    │    },                   │
                                    │    "data": {            │
                                    │    }                    │
                                    │ }                       │
                                    ╰─────────────────────────╯
                    
                    """
                )
            );
        }

        [Test]
        [TempFolder]
        public void ListRecipes()
        {
            var (baseDir, manifest, kubernetes) = _PrepareLayout();
            _WriteSimpleRecipe(manifest, kubernetes);

            var output = $"{baseDir}/output.txt";
            _Cezanne(baseDir).Run(["list-recipes", "-o", output, "-m", manifest]);

            Assert.That(File.ReadAllText(output), Is.EqualTo("Found recipes:\n- test"));
        }

        [Test]
        [TempFolder]
        public async Task Apply()
        {
            var (baseDir, manifest, kubernetes) = _PrepareLayout();
            _WriteSimpleRecipe(manifest, kubernetes);

            var (mockServer, requests) = await _MockK8s();
            await using var _ = mockServer;

            _Cezanne(baseDir, mockServer).Run(["apply", "-a", "test", "-m", manifest]);

            Assert.That(
                requests,
                Is.EqualTo(
                    new HashSet<string>
                    {
                        "GET /api/v1",
                        "GET /api/v1/namespaces/default/configmaps/test",
                        "PATCH /api/v1/namespaces/default/configmaps/test"
                    }
                )
            );
        }

        [Test]
        [TempFolder]
        public async Task ApplyPatch()
        {
            var (baseDir, manifest, kubernetes) = _PrepareLayout();
            _WriteSimpleRecipe(
                manifest,
                kubernetes,
                """
                {
                    "recipes": [
                        {
                            "name": "test",
                            "descriptors": [
                                {
                                    "name": "descriptor.yaml"
                                }
                            ],
                             "patches": [
                                {
                                    "descriptorName": "descriptor.yaml",
                                    "patch": [
                                        {
                                            "op": "add",
                                            "path": "/metadata/labels",
                                            "value": {}
                                        },
                                        {
                                            "op": "add",
                                            "path": "/metadata/labels/patched",
                                            "value": "true"
                                        }
                                    ]
                                }
                            ]
                        }
                    ]
                }
                """
            );

            var (mockServer, requests) = await _MockK8s(true);
            await using var _ = mockServer;

            _Cezanne(baseDir, mockServer).Run(["apply", "-a", "test", "-m", manifest]);

            Assert.That(
                requests,
                Is.EqualTo(
                    new HashSet<string>
                    {
                        "GET /api/v1",
                        "GET /api/v1/namespaces/default/configmaps/test",
                        "PATCH /api/v1/namespaces/default/configmaps/test\n{\"kind\":\"ConfigMap\",\"apiVersion\":\"v1\",\"metadata\":{\"name\":\"test\",\"labels\":{\"patched\":\"true\"}},\"data\":{}}"
                    }
                )
            );
        }

        [Test]
        [TempFolder]
        public async Task ApplyHandleBars()
        {
            var (baseDir, manifest, kubernetes) = _PrepareLayout();
            _WriteSimpleRecipe(
                manifest,
                kubernetes,
                """
                {
                    "recipes": [
                        {
                            "name": "test",
                            "descriptors": [
                                {
                                    "name": "descriptor.yaml.hb"
                                }
                            ]
                        }
                    ]
                }
                """,
                """
                apiVersion: v1
                kind: Service
                metadata:
                  name: {{alveolus.name}}
                  namespace: {{bundlebee.kubernetes.namespace}}
                labels:
                  app: {{alveolus.name}}
                  label: {{#unless a.b.c}}notset{{/unless}}{{#if a.b.c}}{{a.b.c}}{{/if}}
                spec:
                  type: NodePort
                  ports:
                    - port: 1235
                      targetPort: 1235
                  selector:
                    app: {{alveolus.name}}
                """,
                "descriptor.yaml.hb"
            );

            var (mockServer, requests) = await _MockK8s(true);
            await using var _ = mockServer;

            _Cezanne(baseDir, mockServer).Run(["apply", "-a", "test", "-m", manifest]);

            Assert.That(
                requests,
                Is.EqualTo(
                    new HashSet<string>
                    {
                        "GET /api/v1",
                        "GET /api/v1/namespaces/default/services/test",
                        "PATCH /api/v1/namespaces/default/services/test\n{\"apiVersion\":\"v1\",\"kind\":\"Service\",\"metadata\":{\"name\":\"test\",\"namespace\":\"default\"},\"labels\":{\"app\":\"test\",\"label\":\"notset\"},\"spec\":{\"type\":\"NodePort\",\"ports\":[{\"port\":1235,\"targetPort\":1235}],\"selector\":{\"app\":\"test\"}}}"
                    }
                )
            );
        }

        [Test]
        [TempFolder]
        public async Task ApplyCS()
        {
            var (baseDir, manifest, kubernetes) = _PrepareLayout();
            _WriteSimpleRecipe(
                manifest,
                kubernetes,
                """
                {
                    "recipes": [
                        {
                            "name": "test",
                            "descriptors": [
                                {
                                    "name": "descriptor.yaml.cs"
                                }
                            ]
                        }
                    ]
                }
                """,
                "return $\"\"\"\n"
                    + """
                    apiVersion: v1
                    kind: Service
                    metadata:
                      name: {Recipe.Name}
                      namespace: {K8s.DefaultNamespace}
                    spec:
                      type: NodePort
                      ports:
                        - port: 1235
                          targetPort: 1235
                      selector:
                        app: {Recipe.Name}
                    """
                    + "\n\"\"\";",
                "descriptor.yaml.cs"
            );

            var (mockServer, requests) = await _MockK8s(true);
            await using var _ = mockServer;

            _Cezanne(baseDir, mockServer).Run(["apply", "-a", "test", "-m", manifest]);

            Assert.That(
                requests,
                Is.EqualTo(
                    new HashSet<string>
                    {
                        "GET /api/v1",
                        "GET /api/v1/namespaces/default/services/test",
                        "PATCH /api/v1/namespaces/default/services/test\n{\"apiVersion\":\"v1\",\"kind\":\"Service\",\"metadata\":{\"name\":\"test\",\"namespace\":\"default\"},\"spec\":{\"type\":\"NodePort\",\"ports\":[{\"port\":1235,\"targetPort\":1235}],\"selector\":{\"app\":\"test\"}}}"
                    }
                )
            );
        }

        [Test]
        [TempFolder]
        public async Task Delete()
        {
            var (baseDir, manifest, kubernetes) = _PrepareLayout();
            _WriteSimpleRecipe(manifest, kubernetes);

            var (mockServer, requests) = await _MockK8s();
            await using var _ = mockServer;

            _Cezanne(baseDir, mockServer).Run(["delete", "-a", "test", "-m", manifest]);

            Assert.That(
                requests,
                Is.EqualTo(
                    new HashSet<string>
                    {
                        "GET /api/v1",
                        "DELETE /api/v1/namespaces/default/configmaps/test",
                        "GET /api/v1/namespaces/default/configmaps/test"
                    }
                )
            );
        }

        private void _WriteSimpleRecipe(
            string manifest,
            string kubernetes,
            string manifestContent =
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
                    """,
            string descriptor =
                """
                    {
                        "kind": "ConfigMap",
                        "apiVersion": "v1",
                        "metadata": {
                            "name": "test"
                        },
                        "data": {}
                    }
                    """,
            string descriptorFileName = "descriptor.yaml"
        )
        {
            Directory.CreateDirectory(kubernetes);
            File.WriteAllText(manifest, manifestContent);
            File.WriteAllText(Path.Combine(kubernetes, descriptorFileName), descriptor);
        }

        private async Task<(WebApplication, ISet<string>)> _MockK8s(bool capturePayload = false)
        {
            HashSet<string> requests = [];
            var mockServerBuilder = WebApplication.CreateBuilder();
            mockServerBuilder.Logging.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.ColorBehavior = LoggerColorBehavior.Disabled;
            });
            var mockServer = mockServerBuilder.Build();
            mockServer.Urls.Add("http://127.0.0.1:0");
            mockServer.Run(async ctx =>
            {
                var current = $"{ctx.Request.Method} {ctx.Request.Path}";
                if (capturePayload)
                {
                    using var reader = new StreamReader(ctx.Request.Body);
                    var read = await reader.ReadToEndAsync();
                    current += '\n' + read;
                    current = current.Trim();
                }
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
            var baseDir = Temp!;
            var bundlebee = Path.Combine(baseDir, "bundlebee");
            var kubernetes = Path.Combine(bundlebee, "kubernetes");
            var manifest = Path.Combine(bundlebee, "manifest.json");
            return (baseDir, manifest, kubernetes);
        }

        private void _AssertFile(string location, string expected)
        {
            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(location), Is.True);
                Assert.That(File.ReadAllText(location), Is.EqualTo(expected));
            });
        }

        private Cezanne _Cezanne(string baseDir, WebApplication? mockServer = null)
        {
            return new Cezanne
            {
                K8SClientConfiguration = new K8SClientConfiguration
                {
                    Kubeconfig = "skip",
                    Base = mockServer?.Urls.First() ?? "http://dontuse.test.localhost:443"
                },
                MavenConfiguration = new MavenConfiguration
                {
                    // disable for this test
                    EnableDownload = false,
                    LocalRepository = Path.Combine(baseDir, "m2")
                }
            };
        }

        private class Exporter : IAnsiConsoleEncoder
        {
            public string Encode(IAnsiConsole console, IEnumerable<IRenderable> renderable)
            {
                var opts = RenderOptions.Create(console, new Capabilities());
                return string.Join(
                    "\n",
                    renderable.Select(it =>
                        string.Join("", it.Render(opts, 120).Select(it => it.Text))
                    )
                );
            }

            private class Capabilities : IReadOnlyCapabilities
            {
                public ColorSystem ColorSystem => ColorSystem.NoColors;

                public bool Ansi => false;

                public bool Links => true;

                public bool Legacy => false;

                public bool IsTerminal => false;

                public bool Interactive => false;

                public bool Unicode => true;
            }
        }
    }
}
