using Cézanne.Core.Cli.Command;
using Cézanne.Core.Interpolation;
using Cézanne.Core.K8s;
using Cézanne.Core.Maven;
using Cézanne.Core.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;

namespace Cézanne.Core.Cli;

public class Cezanne
{
    static int Main(string[] args)
    {
        return new Cezanne().Run(args);
    }

    public int Run(string[] args)
    {
        using var container = _CreateContainer();
        return _Cli(container, args);
    }


    private int _Cli(ITypeRegistrar registrar, string[] args)
    {
        var app = new CommandApp(registrar);
        app.Configure(config =>
        {
#if DEBUG
            config.PropagateExceptions();
            config.ValidateExamples();
#endif

            config.SetApplicationName("cezanne");

            config.AddCommand<ApplyCommand>("apply");
        });
        return app.Run(args);
    }


    private Registrar _CreateContainer()
    {
        var services = new ServiceCollection();

        var level = Enum.Parse<LogLevel>(Environment.GetEnvironmentVariable("CEZANNE_LOG_LEVEL") ?? "Information");
        services.AddLogging(config => config
            .AddProvider(new SpectreLoggerProvider(level))
            .SetMinimumLevel(level));

        // services
        services.AddKeyedSingleton("cezannePlaceholderLookupCallback", _PlaceholderLookup);
        services.AddSingleton(typeof(K8SClientConfiguration), new K8SClientConfiguration()); // todo
        services.AddSingleton(typeof(MavenConfiguration), new MavenConfiguration()); // todo
        services.AddSingleton<K8SClient>();
        services.AddSingleton<Substitutor>();
        services.AddSingleton<MavenService>();
        services.AddSingleton<ConditionEvaluator>();
        services.AddSingleton<ConditionJsonEvaluator>();
        services.AddSingleton<RequirementService>();
        services.AddSingleton<ManifestReader>();
        services.AddSingleton<ArchiveReader>();
        services.AddSingleton<RecipeHandler>();

        // commands
        services.AddSingleton<ApplyCommand>();

        return new Registrar(services);
    }

    private string? _PlaceholderLookup(string name, string? orElse)
    {
        return Environment.GetEnvironmentVariable(name) ??
            Environment.GetEnvironmentVariable(name.ToUpperInvariant().Replace('.', '_')) ??
            orElse;
    }

    internal sealed class Registrar(IServiceCollection services) : ITypeRegistrar, IDisposable

    {
        private ServiceProvider? _container;


        public ITypeResolver Build()
        {
            _container ??= services.BuildServiceProvider();
            return new TypeResolver(_container);
        }

        public void Dispose()
        {
            _container?.Dispose();
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
