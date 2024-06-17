using Cézanne.Core.Cli;
using System.Diagnostics;

namespace Cézanne.Runner
{
    public static class Runner
    {
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