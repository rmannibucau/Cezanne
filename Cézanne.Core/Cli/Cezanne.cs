using Cézanne.Core.Cli.Command;
using Cézanne.Core.Interpolation;
using Cézanne.Core.K8s;
using Cézanne.Core.Maven;
using Cézanne.Core.Service;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;

namespace Cézanne.Core.Cli
{
    public class Cezanne
    {
        public K8SClientConfiguration? K8SClientConfiguration { private get; init; }
        public MavenConfiguration? MavenConfiguration { private get; init; }

        public int Run(string[] args)
        {
            return Run(args, (app, _) => app.Run(args));
        }

        public int Run(string[] args, Func<CommandApp, IServiceCollection, int> runner)
        {
            var (container, collection) = _CreateContainer(args);
            using (container)
            {
                return runner(_Cli(container, args), collection);
            }
        }


        private CommandApp _Cli(ITypeRegistrar registrar, string[] args)
        {
            var app = new CommandApp(registrar);
            app.Configure(config =>
            {
#if DEBUG
                config.PropagateExceptions();
                config.ValidateExamples();
#endif

                config.UseAssemblyInformationalVersion().SetApplicationName("cezanne");

                config.AddCommand<ApplyCommand>("apply")
                    .WithDescription("Apply/deploy a set of descriptors from a root one.");
                config.AddCommand<DeleteCommand>("delete")
                    .WithDescription("Delete an alveolus deployment by deleting all related descriptors.");
            });
            return app;
        }


        private (Registrar, IServiceCollection) _CreateContainer(string[] args)
        {
            ServiceCollection services = new();

            var level =
                Enum.Parse<LogLevel>(Environment.GetEnvironmentVariable("CEZANNE_LOG_LEVEL") ?? "Information");
            services.AddLogging(config => config
                .AddProvider(new SpectreLoggerProvider(level))
                .SetMinimumLevel(level));

            // shared services configuration
            var binder = new ConfigurationBuilder()
                .AddJsonFile("cezanne.json", true)
                .AddEnvironmentVariables()
                .AddCommandLine(args)
                .Build();
            var cezanneSection = binder.GetSection("cezanne");

            var k8s = K8SClientConfiguration ?? new K8SClientConfiguration();
            if (K8SClientConfiguration is null)
            {
                cezanneSection.GetSection("kubernetes").Bind(k8s);
            }

            var maven = MavenConfiguration ?? new MavenConfiguration();
            if (MavenConfiguration is null)
            {
                cezanneSection.GetSection("maven").Bind(maven);
            }

            static string? placeholders(string name, string defaultValue)
            {
                return Environment.GetEnvironmentVariable(name) ??
                       Environment.GetEnvironmentVariable(name.ToUpperInvariant().Replace('.', '_')) ??
                       defaultValue;
            }

            // services
            services.AddKeyedSingleton("cezannePlaceholderLookupCallback", placeholders);
            services.AddSingleton(k8s);
            services.AddSingleton(maven);
            services.AddSingleton<K8SClient>();
            services.AddSingleton<ApiPreloader>();
            services.AddSingleton<Substitutor>();
            services.AddSingleton<MavenService>();
            services.AddSingleton<ConditionEvaluator>();
            services.AddSingleton<ConditionJsonEvaluator>();
            services.AddSingleton<ConditionAwaiter>();
            services.AddSingleton<RequirementService>();
            services.AddSingleton<ManifestReader>();
            services.AddSingleton<ArchiveReader>();
            services.AddSingleton<ContainerSanitizer>();
            services.AddSingleton<RecipeHandler>();

            // commands
            services.AddSingleton<ApplyCommand>();
            services.AddSingleton<DeleteCommand>();

            return (new Registrar(services), services);
        }

        internal sealed class Registrar(IServiceCollection services) : ITypeRegistrar, IDisposable

        {
            private ServiceProvider? _container;

            public void Dispose()
            {
                _container?.Dispose();
            }


            public ITypeResolver Build()
            {
                _container ??= services.BuildServiceProvider();
                return new TypeResolver(_container);
            }

            public void Register(Type service, Type implementation)
            {
                services.AddSingleton(service, implementation);
            }

            public void RegisterInstance(Type service, object implementation)
            {
                services.AddSingleton(service, implementation);
            }

            public void RegisterLazy(Type service, Func<object> factory)
            {
                services.AddActivatedKeyedSingleton(service, service, (p, k) => factory());
            }
        }

        internal sealed class TypeResolver(IServiceProvider provider) : ITypeResolver
        {
            public object? Resolve(Type? type)
            {
                return provider.GetRequiredService(type ?? typeof(object));
            }
        }
    }
}