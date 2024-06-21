using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Cézanne.Core.Cli;
using Cézanne.Core.Cli.Command;

namespace Cézanne.Runner
{
    public static class Runner
    {
        // register for spectre the needed reflection (settings mainly and hidden classes)
        [DynamicDependency(
            DynamicallyAccessedMemberTypes.All,
            "Spectre.Console.Cli.ExplainCommand",
            "Spectre.Console.Cli"
        )]
        [DynamicDependency(
            DynamicallyAccessedMemberTypes.All,
            "Spectre.Console.Cli.VersionCommand",
            "Spectre.Console.Cli"
        )]
        [DynamicDependency(
            DynamicallyAccessedMemberTypes.All,
            "Spectre.Console.Cli.XmlDocCommand",
            "Spectre.Console.Cli"
        )]
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ApplyCommand.Settings))]
        [DynamicDependency(
            DynamicallyAccessedMemberTypes.All,
            typeof(CollectingCommand<,>.CollectorSettings)
        )]
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(DeleteCommand.Settings))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(InspectCommand.Settings))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ListRecipesCommand.Settings))]
        [DynamicDependency(
            DynamicallyAccessedMemberTypes.All,
            typeof(PlaceholderExtractorCommand.Settings)
        )]
        private static int Main(string[] args)
        {
            _BreakIfDebug();
            return new Cezanne().Run(args);
        }

        private static void _BreakIfDebug()
        {
            if ((Environment.GetEnvironmentVariable("CEZANNE_DEBUG") ?? "false") == "true")
            {
                while (!Debugger.IsAttached)
                {
                    Thread.Sleep(1_000);
                }

                Debugger.Break();
            }
        }
    }
}
