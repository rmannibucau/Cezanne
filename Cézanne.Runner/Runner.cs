// See https://aka.ms/new-console-template for more information

using Cézanne.Core.K8s;
using Microsoft.Extensions.Logging;

namespace Cézanne.Runner
{
    public static class Runner
    {
        private static async Task Main(string[] args)
        {
            // todo: rewrite, this was to test minikube
            using K8SClient client = new(
                new K8SClientConfiguration
                {
                    Kubeconfig = Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "/root", ".kube/config")
                },
                LoggerFactory.Create(b => b.AddConsole()));
            HttpResponseMessage api = await client.SendAsync(HttpMethod.Get, "api/v1");
            string apiResponse = await api.Content.ReadAsStringAsync();
            Console.WriteLine(api.StatusCode);
            Console.WriteLine(api.Headers);
            Console.WriteLine(apiResponse);
        }
    }
}