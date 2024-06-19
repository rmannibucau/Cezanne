using Humanizer;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Cézanne.Core.Cli.Command
{
    public class SpectreLoggerProvider(LogLevel logLevel) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName)
        {
            return new SpectreLogger(logLevel, categoryName);
        }

        public void Dispose()
        {
            // no-op
        }

        internal class SpectreLogger(LogLevel level, string categoryName) : ILogger
        {
            private static readonly IDictionary<LogLevel, string> LevelColors = new Dictionary<LogLevel, string>
            {
                { LogLevel.Critical, "red" },
                { LogLevel.Error, "orangered1" },
                { LogLevel.Warning, "yellow" },
                { LogLevel.Information, "green" },
                { LogLevel.Debug, "dodgerblue1" },
                { LogLevel.Trace, "default" }
            };

            private static readonly IDictionary<LogLevel, string> LevelNames = new Dictionary<LogLevel, string>
            {
                { LogLevel.Critical, " critic" },
                { LogLevel.Error, "  error" },
                { LogLevel.Warning, "warning" },
                { LogLevel.Information, "   info" },
                { LogLevel.Debug, "  debug" },
                { LogLevel.Trace, "  trace" }
            };

            private readonly string loggerName = categoryName.PadLeft(40, ' ').Truncate(40);

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    var color = LevelColors[logLevel];
                    var levelName = LevelNames[logLevel];
                    var message = formatter(state, exception);
                    AnsiConsole.MarkupLineInterpolated($"[[[{color}]{levelName}[/]]][[{loggerName}]] {message}");
                }
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return level != LogLevel.None && logLevel >= level;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                return NullScope.Instance;
            }
        }

        internal sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}