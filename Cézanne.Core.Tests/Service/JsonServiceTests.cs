using Cézanne.Core.Service;

namespace Cézanne.Core.Tests.Service
{
    public class JsonServiceTests
    {
        [Test]
        public void FromYaml()
        {
            var yaml = """
                       apiVersion: apps/v1
                       kind: Deployment
                       metadata:
                       name: grafana-simple-deployment
                       labels:
                           app: grafana-simple
                       namespace: "default"
                       spec:
                       replicas: 1
                       selector:
                           matchLabels:
                           app: grafana-simple
                       template:
                           metadata:
                           labels:
                               app: grafana-simple
                           spec:
                             volumes:
                               - name: dashboard
                                 configMap:
                                   name: grafana-simple-dashboard-config
                               - name: config
                                 configMap:
                                   name: grafana-simple-config
                             containers:
                               - name: grafana
                                 image: grafana/grafana:9.0.6
                                 env:
                                   - name: GF_SECURITY_ADMIN_USER
                                     value: "admin"
                                   - name: GF_SECURITY_ADMIN_PASSWORD
                                     value: "admin"
                                 volumeMounts:
                                   - name: config
                                     mountPath: /etc/grafana/
                                   - name: dashboard
                                     mountPath: /etc/grafana/provisioning/datasources/
                                 ports:
                                   - containerPort: 3000
                                 livenessProbe:
                                   httpGet:
                                     path: /api/health
                                     port: 3000
                                   initialDelaySeconds: 5
                                   periodSeconds: 10
                                 readinessProbe:
                                   httpGet:
                                     path: /api/health
                                     port: 3000
                                   initialDelaySeconds: 5
                                   periodSeconds: 10
                                 resources:
                                   limits:
                                     cpu: 0.1
                                     memory: 128Mi
                                   requests:
                                     cpu: 0.1
                                     memory: 128Mi
                       """;
            var result = Jsons.FromYaml(yaml);
            Assert.That(result, Is.Not.Null); // just ensures it reads it for now
        }
    }
}