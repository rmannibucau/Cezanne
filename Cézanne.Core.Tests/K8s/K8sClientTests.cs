using Cézanne.Core.K8s;
using Cézanne.Core.Service;
using Cézanne.Core.Tests.Rule;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace Cézanne.Core.Tests.K8s
{
    public class K8SClientTests : ITempFolder
    {
        public string? Temp { get; set; }

        [Test]
        public async Task CallApi()
        {
            var mockServer = WebApplication.Create();
            mockServer.Urls.Add("http://127.0.0.1:0");
            mockServer.MapGet("/test", () => "mock");
            mockServer.Start();
            using NullLoggerFactory loggerFactory = new();
            await using (mockServer)
            {
                await using K8SClient client = new(
                    new K8SClientConfiguration { Base = mockServer.Urls.First(), Kubeconfig = "skip" },
                    new Logger<K8SClient>(loggerFactory), new Logger<ApiPreloader>(loggerFactory));
                var response = await client.SendAsync(HttpMethod.Get, "/test");
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            }
        }

        [Test]
        public async Task DryRun()
        {
            var logProvider = new InMemoryLogProvider();
            using ILoggerFactory loggerFactory = new LoggerFactory([logProvider]);

            var mockServer = WebApplication.Create();
            mockServer.Urls.Add("http://127.0.0.1:0");
            mockServer.Start();

            await using (mockServer)
            {
                await using K8SClient client = new(
                    new K8SClientConfiguration
                    {
                        Base = mockServer.Urls.First(), Kubeconfig = "skip", DryRun = true, Verbose = true
                    },
                    new Logger<K8SClient>(loggerFactory), new Logger<ApiPreloader>(loggerFactory));
                var response = await client.SendAsync(HttpMethod.Get, "/foo");
                Assert.Multiple(async () =>
                {
                    Assert.That(logProvider.Logs,
                        Is.EqualTo(new List<string>
                        {
                            $"[Cézanne.Core.K8s.K8SClient][Debug] Using base url 'http://127.0.0.1:{new Uri(mockServer.Urls.First()).Port}'",
                            "[Cézanne.Core.K8s.K8SClient][Information] Execution will use dry-run mode",
                            "[Cézanne.Core.K8s.K8SClient][Information] GET /foo\n\nHTTP/1.1 200 OK\nx-dry-run: true\n\n{}"
                        }));
                    Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("{}"));
                });
            }
        }

        [Test]
        [TempFolder]
        public async Task YamlKubeConfig()
        {
            var kubeConfig = Path.Combine(_TempOrFail(), "kubeconfig");
            File.WriteAllText(
                kubeConfig,
                """
                apiVersion: v1
                clusters:
                - cluster:
                    certificate-authority-data: LS0tLS1CRUdJTiBDRVJUSUZJQ0FURS0tLS0tCk1JSURCakNDQWU2Z0F3SUJBZ0lCQVRBTkJna3Foa2lHOXcwQkFRc0ZBREFWTVJNd0VRWURWUVFERXdwdGFXNXAKYTNWaVpVTkJNQjRYRFRJeE1EUXlNREEyTURBek1sb1hEVE14TURReE9UQTJNREF6TWxvd0ZURVRNQkVHQTFVRQpBeE1LYldsdWFXdDFZbVZEUVRDQ0FTSXdEUVlKS29aSWh2Y05BUUVCQlFBRGdnRVBBRENDQVFvQ2dnRUJBTyttClJEM0t6aUQxZllKdDRkZnA5d2Y1dTQzUnlBVk55WVdNN1BnVDlZY250VjIzejlJREwvQ0wxT2hseXhMUzRjV3UKaTMwU0EyTTRJRHNEVGJURHFYU04wbmZZQ0FDcDZLdjAwVnR5RCtpV0pOd2UwcElTdm1WTjFSU2kxK0twK3pJYwpTd0xMMUtESTFlOFFhVXFoK0NLVzdsNWZjcSsyMXhPZ3JneExEN2pNbEJ1RnpYNmVhaU1LbWF3MXdSdk5KS25ICnM4N29GSXBBOHc0OXpsUnVxZVk1VGxXek85RVk4T0dGeVkyL2JuQ0FubWprZnRiSllkc0pldHAzRkZ2WmVxQ3cKSy9jUjhNR0crcks0aDJGMHRDZlQra2hFOVhhc0JMeTVjNGZqSU9KcnJhQlJ6SEcwNlhJaXNtZjM3NGkxczNSWQpnRHdCdkxMS2xDbmtnK0RvWXhVQ0F3RUFBYU5oTUY4d0RnWURWUjBQQVFIL0JBUURBZ0trTUIwR0ExVWRKUVFXCk1CUUdDQ3NHQVFVRkJ3TUNCZ2dyQmdFRkJRY0RBVEFQQmdOVkhSTUJBZjhFQlRBREFRSC9NQjBHQTFVZERnUVcKQkJRMFRCeUhqazJsZVpSb3RtdDdvSkRGbnc0NzBEQU5CZ2txaGtpRzl3MEJBUXNGQUFPQ0FRRUFyazA3ZzlmTwovelFLcUN3WkIyU21pREFocHlnSUlLQzhQRUpGcVRHQzNvT0hTVDI0dW1TSm9FRTVoci9TdTZpWWtGaUNpRTdHClZjeExlcFdReWtvc2NmTVorVEsxVWdJT1BkdW9DU1VmODFZU2Z0WmNFc2ZXSUJ6cEprZFZqZTRUeUFQRmduWW4KYW9IbXJFcStnQVpVMTdyVS8wcU9UZXVHVldwaGxrV1JuU1hETXRMK0l4N0hVSThUN0FOL2lCVzl4NjVHS0RiZgo1ZjFjSDZxSDBiTFg2SVMxdlRoSVQ1Umh1bVNuR2w2dkZCU2JMSlZSZFNuK0pSU0NVbVJ5ZmNoYlpLaDFWYlZ3Ci9XVDNRN3hWeGQxQnZlSVpPUHMzUnBGNS9UZkZrYzZhSzNYL1YrVVR1ZzFidVlnbWF5WGVFZWNjdFdoQ2V1aE8KTXl5K1hLSy9LTkJ3dlE9PQotLS0tLUVORCBDRVJUSUZJQ0FURS0tLS0tCg==
                    extensions:
                    - extension:
                        last-update: Tue, 04 Jun 2024 12:14:38 CEST
                        provider: minikube.sigs.k8s.io
                        version: v1.33.1
                      name: cluster_info
                    server: https://192.168.49.2:8443
                  name: minikube
                contexts:
                - context:
                    cluster: minikube
                    extensions:
                    - extension:
                        last-update: Tue, 04 Jun 2024 12:14:38 CEST
                        provider: minikube.sigs.k8s.io
                        version: v1.33.1
                      name: context_info
                    namespace: test
                    user: minikube
                  name: minikube
                current-context: minikube
                kind: Config
                preferences: {}
                users:
                - name: minikube
                  user:
                    client-certificate-data: LS0tLS1CRUdJTiBDRVJUSUZJQ0FURS0tLS0tCk1JSURJVENDQWdtZ0F3SUJBZ0lCQWpBTkJna3Foa2lHOXcwQkFRc0ZBREFWTVJNd0VRWURWUVFERXdwdGFXNXAKYTNWaVpVTkJNQjRYRFRJME1EWXdNekV3TVRReU9Gb1hEVEkzTURZd05ERXdNVFF5T0Zvd01URVhNQlVHQTFVRQpDaE1PYzNsemRHVnRPbTFoYzNSbGNuTXhGakFVQmdOVkJBTVREVzFwYm1scmRXSmxMWFZ6WlhJd2dnRWlNQTBHCkNTcUdTSWIzRFFFQkFRVUFBNElCRHdBd2dnRUtBb0lCQVFETG04dUxpTHltT2xkV1A4aGlnUnB5V3dSR3kvRE0KS1FkRTFSUERCVXRFd3ZmQWFsYVZEYy83VFBEV2k3ek1DVzgrcmlQaFJ0R0piaHd3ajRNNkRCcjVTSk1Xayt3MwpWZUZRcGZtVUFnano1TVYreU8veFFwa1pmbnlhSXRwdlJyTURCUVRmM2ZSZnF3NTZheGRueW9NMVdMODNFUFZxCmFIN2lNSUVIQUZXU3VsbXpGZDhEWnJUNHpNS1Uwa284S2FkZmZ4eldKZWtYMUN1Z3p1LzVJTnRZRUN0UnNpSkIKb2lMSmlxQmVSVlNhcUxtbUN3cldTb29xc214VXA0MUJDS3N2ZElKeFJZbU1RTkNCUEszSDVONlkxSlJGR01aSApGVnVLY0w5ZCtQblNrcy94a0JlRHBhaDBJVHg0RXBuVTZiNGY4czdBcWQwdlB2cjdacUdLMXVpNUFnTUJBQUdqCllEQmVNQTRHQTFVZER3RUIvd1FFQXdJRm9EQWRCZ05WSFNVRUZqQVVCZ2dyQmdFRkJRY0RBUVlJS3dZQkJRVUgKQXdJd0RBWURWUjBUQVFIL0JBSXdBREFmQmdOVkhTTUVHREFXZ0JRMFRCeUhqazJsZVpSb3RtdDdvSkRGbnc0NwowREFOQmdrcWhraUc5dzBCQVFzRkFBT0NBUUVBSi9TQlFnSVVvNWdNMlQ4dCszeEJ5L05mZnY2N1FtRXBQUUFVCmZLN0pBVDU4Y3QyZFowTWMrWnJmMUN3RGh6OXQrdTRidzN0ZGpIL0gyMjM5MVIxQzdTYmsxM1V5VWZzMEtLZU8KbytzbUdJYTRTUXY0QmRkZTBQZFgra0VzTTRLOUJoZ2JvOTZUSXkyNkJ2UjFnWEJUZ1IvMVQ3dmRmcmxlN2xvUQpCMzA1NXN0b01vZ3FwakJTcHY4eHlkUysyWFBwdEtENVcxN2lIUnErb0NXNHJtd01JVXB4OWF1cWNwT2VZUzdtCmlpazN5bVBaeGRSbmV1WVQ4Y3lOYnBOY2NmMlFBMGxuYncxNGxZR3o4MTBMdHE0aFFxZm9DeWtDLzlqdEo1WEMKdU94QjVkeGdEVnRXY0dZMDlPcnppOVl6SVF3TTdUQlR5cU43Z3I0SlNGcGQ2WC94MlE9PQotLS0tLUVORCBDRVJUSUZJQ0FURS0tLS0tCg==
                    client-key-data: LS0tLS1CRUdJTiBSU0EgUFJJVkFURSBLRVktLS0tLQpNSUlFcFFJQkFBS0NBUUVBeTV2TGk0aThwanBYVmovSVlvRWFjbHNFUnN2d3pDa0hSTlVUd3dWTFJNTDN3R3BXCmxRM1ArMHp3MW91OHpBbHZQcTRqNFViUmlXNGNNSStET2d3YStVaVRGcFBzTjFYaFVLWDVsQUlJOCtURmZzanYKOFVLWkdYNThtaUxhYjBhekF3VUUzOTMwWDZzT2Vtc1haOHFETlZpL054RDFhbWgrNGpDQkJ3QlZrcnBac3hYZgpBMmEwK016Q2xOSktQQ21uWDM4YzFpWHBGOVFyb003ditTRGJXQkFyVWJJaVFhSWl5WXFnWGtWVW1xaTVwZ3NLCjFrcUtLckpzVktlTlFRaXJMM1NDY1VXSmpFRFFnVHl0eCtUZW1OU1VSUmpHUnhWYmluQy9YZmo1MHBMUDhaQVgKZzZXb2RDRThlQktaMU9tK0gvTE93S25kTHo3NisyYWhpdGJvdVFJREFRQUJBb0lCQVFDak9IUjJtaG5wQTlIcApzVjM1SVZmTEhvMlNGNEVrbVN0YmtaaXkrUFo2Mi9UeVNneTRsb2NKQklmNDViSm11cFYwWVBNZ2I3NGY5cVlnCmc1dUdHQmd6aUd0cGFSR3UxbWkyVnlkNDhCeXZMOURtcnp3eVl0b0twdXhLUC9CdHpmWkpVR2UwOHVBcEpSNkoKSW5wejJOTlFHNkhHQ2hGQ3lSd1dSUjNhTi9saGtoV2Q0ZElHSnNrUWtPRUNWZGFuR2VSNHFmV1ZCSG9qQmJKaQoveU5ZUVF0OHBNTVo2YVhjSk4xQlBUQXJuSGVScEZCQmZjc1Vva0J0eUZTTDFPZUFQOE4vc2REdXRUOG94SldnCmV1aEVxU011cVV1UHBTOEl3NGMwNGZkMmlOcm8rRHlNVWN6WFIyUEFoYnlQWFlTMzZRSFB4MkJqQnM3ZHVvSGIKQjVuSkJza0ZBb0dCQU9sdndCcjVpZmhyNm1QTDhqS3hjUXA4Sm5PTm1YR0JoZUZIVFRUYU9VTzNHb2NobXI3TwpxODJ0bjlETFFlcWFsUEREK21ES25HZ2JNcEtub0hQclAvUndXdTJXanhlWVVPYUFDczJCQWRDdXZnR0Q4OHg3ClpicnFvZ3M2OGxIK0tIMXJOaE4xTDhFR1oyY2hIS01SQWRyRndDTGJZODhxRWovYVo4MVlET2R6QW9HQkFOOUoKK0RiVXdBanhsYjBOVGVGTnM2dEl5NGFLYnRYTXFFaUxHQ0I4bkZrMGxCY0xrUCs3NTJRQkt5T1Rva29XalE3dwppL094L0ZybjdJOVUybDdNNUhTbnNjMm1EVXI4UVpiY1NuU2JEOU4wK1ZtcVlWSlFmRU9NTjlZcExJS0dWK0xlCkpiSS9WVXVlSjJNYmIxQ01iNG1JcXlLTGxiSVVGQ1BqK2Y3N3Bxd2pBb0dCQUtYcDIyeFF1Qk50QUNiMkthcUcKRzRZTVAzZ1p5Rm00YVdONHZoTTJsMFRkdTJrWUpWaEFwRE9IbC9OYXcvcnU2N1ZFVll5OTlQUzVmL1JrVjlLTAovZVRLaHBZZlVJekFvWjl5bWpyOTJrQnNNbmY1UlNxcytkMGtMeEEwVVU2ZGlrRzZGYkUydFQ1SVF1NDF4cGpQCjJiV1luN3NtbTRYK3JRSGRSYkhaUnpLcEFvR0JBTjhaSURsZ3Z2THd0dVVxeXRySGNUSTl6S1VENGhRYXBUVysKVElBQklaSjcxMDlqVGlCRzFiNTA4RzVlanpPcGJvMHp5UkhYajBZaEhwcGpkTUJ0eGdITW4vblM1TXM4V2locQp1TFhqVEovQjYzWXNwNHJBUWppWGZCNnVDdnZyVVJxclRVell2TmVPRU5xVVNkZFlTZ1ZJR1gydHJBYyt5cFRGCnJ6NldvQVN6QW9HQUVyZUsvd2lGdm5Ib0hDTzJVaFNZWG5ZUm9TLzBMeGpnVG81UWY1OTh0Z0hiNU9lU3I0T3YKcytYMkVrVEwydm5PeFo2Ym52dGtEcjJSZ3dGUlBqRTM5aUtTYWl2dEZyQlFZckh0ZlZ2ZmZ3NXRJbEpHZG1QSgpUMnB2eFZtZVdwQ2g2Y0h5UmNBblUwZFloVitrNm1rQk1sckpUbW5jRjZOZGNxZlVac0NqOHJvPQotLS0tLUVORCBSU0EgUFJJVkFURSBLRVktLS0tLQo=
                """);
            using NullLoggerFactory loggerFactory = new();
            await using K8SClient client = new(
                new K8SClientConfiguration { Kubeconfig = kubeConfig },
                new Logger<K8SClient>(loggerFactory), new Logger<ApiPreloader>(loggerFactory));
            Assert.Multiple(() =>
            {
                Assert.That(client.Base, Is.EqualTo("https://192.168.49.2:8443/"));
                Assert.That(client.HttpClient.BaseAddress,
                    Is.EqualTo(new Uri(client.Base ?? throw new ArgumentException("no client.Base"))));
                Assert.That(client.DefaultNamespace, Is.EqualTo("test"));
                Assert.That(client.HttpMessageHandler.ClientCertificates.Count, Is.EqualTo(2));
                Assert.That(
                    from X509Certificate cert in client.HttpMessageHandler.ClientCertificates
                    select cert.Subject,
                    Is.EqualTo(ImmutableList.Create("CN=minikube-user, O=system:masters", "CN=minikubeCA")));
            });
        }

        private string _TempOrFail()
        {
            return Temp ?? throw new ArgumentException("No temp folder");
        }
    }

    internal class InMemoryLogger(string name, IList<string> logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return NoopDisposable.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel != LogLevel.None;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var log = $"[{name}][{logLevel}] {formatter(state, exception)}";
            lock (logs)
            {
                logs.Add(log);
            }
        }
    }

    internal class NoopDisposable : IDisposable
    {
        internal static readonly IDisposable Instance = new NoopDisposable();

        public void Dispose()
        {
            // no-op
        }
    }

    internal class InMemoryLogProvider : ILoggerProvider
    {
        internal readonly IList<string> Logs = new List<string>();

        public ILogger CreateLogger(string categoryName)
        {
            return new InMemoryLogger(categoryName, Logs);
        }

        public void Dispose()
        {
            // no-op
        }
    }
}