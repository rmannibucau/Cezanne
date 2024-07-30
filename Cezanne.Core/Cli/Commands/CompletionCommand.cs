using System.Collections.Immutable;
using System.ComponentModel;
using System.Reflection;
using System.Text;
using Cezanne.Core.Cli.Completable;
using Cezanne.Core.Service;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace Cezanne.Core.Cli.Command
{
    public class CompletionCommand<S>(
        IServiceCollection ioc,
        IServiceProvider provider,
        RecipeHandler handler
    ) : AsyncCommand<S>
        where S : CompletionCommand<S>.Settings
    {
        public override async Task<int> ExecuteAsync(CommandContext context, S settings)
        {
            if (settings.Position <= 0 || settings.Line is null or "")
            {
                return await Task.FromResult(0);
            }

            var parsed = _Parse(settings);
            if (parsed.Args.Count == 0) // commands
            {
                _ListCommands();
                return await Task.FromResult(0);
            }

            var commandName = parsed.Args[0];
            var command = _FindCommand(commandName);
            if (command is null)
            {
                _ListCommands();
                return await Task.FromResult(0);
            }

            var listOptions = parsed.CurrentWordIndex < 0 && parsed.Args.Count == 1;
            if (
                listOptions
                || (
                    parsed.CurrentWordIndex >= 0
                    && parsed.Args[parsed.CurrentWordIndex].StartsWith('-')
                )
                || (
                    parsed.CurrentWordIndex > 0
                    && !parsed.Args[parsed.CurrentWordIndex - 1].StartsWith('-')
                )
            )
            {
                foreach (
                    var opt in _FindSettings(command)
                        .GetProperties()
                        .Where(prop =>
                        {
                            var opt = prop.GetCustomAttribute<CommandOptionAttribute>();
                            return opt is not null && !opt.IsHidden;
                        })
                        .SelectMany<PropertyInfo, string>(prop =>
                        {
                            var opt = prop.GetCustomAttribute<CommandOptionAttribute>()!;
                            return
                            [
                                .. opt.LongNames.Select(it => $"--{it}"),
                                .. opt.ShortNames.Select(it => $"-{it}")
                            ];
                        })
                        .Order()
                )
                {
                    Console.WriteLine(opt);
                }
                return await Task.FromResult(0);
            }

            if (
                parsed.CurrentWordIndex > 0
                || (
                    parsed.CurrentWordIndex < 0
                    && parsed.Args.Count > 1
                    && parsed.Args[parsed.Args.Count - 1].StartsWith('-')
                )
            ) // value of an option
            {
                var option = parsed.Args[
                    parsed.CurrentWordIndex < 0
                        ? parsed.Args.Count - 1
                        : parsed.CurrentWordIndex - 1
                ];
                if (option.StartsWith("--"))
                {
                    option = option[2..];
                }
                else if (option.StartsWith('-'))
                {
                    option = option[1..];
                }

                // common options handled transversally
                var setting = _FindSettings(command)
                    .GetProperties()
                    .Where(prop =>
                    {
                        var opt = prop.GetCustomAttribute<CommandOptionAttribute>();
                        return opt is not null
                            && !opt.IsHidden
                            && (opt.LongNames.Contains(option) || opt.ShortNames.Contains(option));
                    })
                    .FirstOrDefault((PropertyInfo?)null);
                if (setting is not null)
                {
                    if (
                        setting.PropertyType == typeof(bool)
                        || setting.PropertyType == typeof(bool?)
                    )
                    {
                        Console.WriteLine("true");
                        Console.WriteLine("false");
                    }

                    if (setting.PropertyType.IsEnum)
                    {
                        foreach (var name in Enum.GetNames(setting.PropertyType).Order())
                        {
                            Console.WriteLine(name);
                        }
                    }

                    if (option is "a" or "alveolus" or "r" or "recipe")
                    {
                        return await _FindRecipes(parsed.Args);
                    }

                    return await Task.FromResult(0);
                }

                // else just request the command
                foreach (
                    var proposal in (
                        await command.CompleteOptionAsync(
                            option,
                            parsed.Args,
                            parsed.CurrentWordIndex
                        )
                    ).Order()
                )
                {
                    Console.WriteLine(proposal);
                }
            }

            return await Task.FromResult(0);
        }

        private async Task<int> _FindRecipes(IList<string> args)
        {
            var manifestIndex = Math.Max(args.IndexOf("-m"), args.IndexOf("--manifest"));
            var hasManifest = manifestIndex > 0 && manifestIndex < (args.Count - 1);
            var fromIndex = Math.Max(args.IndexOf("-f"), args.IndexOf("--from"));
            var hasFrom = fromIndex > 0 && fromIndex < (args.Count - 1);
            if (!hasManifest && !hasFrom)
            {
                return await Task.FromResult(0);
            }

            var manifest = await handler.FindManifest(
                fromIndex > 0 ? args[fromIndex + 1] : null,
                manifestIndex > 0 ? args[manifestIndex + 1] : null,
                null,
                null
            );
            if (manifest is null)
            {
                return await Task.FromResult(0);
            }

            foreach (var name in manifest.Recipes.Select(it => it.Name).Order())
            {
                Console.WriteLine(name);
            }
            return 0;
        }

        private Type _FindSettings(ICompletable command)
        {
            var baseType = command.GetType().BaseType!;
            var args = baseType.GetGenericArguments();
            return args.Last(); // CollectingCommand has 2 generics so we can't use single
        }

        private void _ListCommands()
        {
            foreach (
                var cmd in ioc.Where(it =>
                    {
                        if (!it.ServiceType.Name.EndsWith("Command"))
                        {
                            return false;
                        }
                        if (
                            it.ServiceType.BaseType is null
                            || !it.ServiceType.BaseType.IsGenericType
                        )
                        {
                            return false;
                        }
                        var baseType = it.ServiceType.BaseType?.GetGenericTypeDefinition();
                        return baseType is not null
                            && (
                                baseType == typeof(AsyncCommand<>)
                                || baseType == typeof(CollectingCommand<,>)
                            );
                    })
                    .Select(it => // convert using our class -> command convention
                    {
                        var name = it.ServiceType!.Name[
                            0..(it.ServiceType.Name.Length - "Command".Length)
                        ];
                        var builder = new StringBuilder(name.Length + 2);
                        for (int i = 0; i < name.Length; i++)
                        {
                            var c = name[i];
                            if (i > 0 && char.IsUpper(c))
                            {
                                builder.Append('-');
                            }
                            builder.Append(char.ToLower(c));
                        }
                        return builder.ToString();
                    })
                    .Distinct()
                    .Order()
            )
            {
                Console.WriteLine(cmd);
            }
        }

        // not the best way but Spectre.Console made all private (CommandModel and friends)
        private ICompletable? _FindCommand(string commandName)
        {
            var expected = $"{commandName.ToLowerInvariant().Replace("-", "")}Command";
            var matching = ioc.FirstOrDefault(
                it =>
                    it!.ServiceType.Name.Equals(
                        expected,
                        StringComparison.InvariantCultureIgnoreCase
                    ) && typeof(ICompletable).IsAssignableFrom(it.ServiceType),
                null
            );
            return (ICompletable?)(
                matching is null ? null : provider.GetService(matching!.ServiceType)
            );
        }

        // copied/inspired from https://github.com/dotnet/command-line-api/blob/main/src/System.CommandLine/Parsing/CliParser.cs
        protected ParsedLine _Parse(Settings settings)
        {
            var memory = settings.Line.AsMemory();

            var startTokenIndex = 0;

            var pos = 0;

            var seeking = Boundary.TokenStart;
            var seekingQuote = Boundary.QuoteStart;

            var result = new List<string>();
            int currentIndex = -1;
            int currentWord = -1;

            void DoAdd()
            {
                if (
                    startTokenIndex <= settings.Position
                    && (
                        pos > settings.Position || pos == settings.Line!.Length /*assume it is generally a space*/
                    )
                )
                {
                    currentWord = result.Count;
                    currentIndex = (settings.Position ?? pos) - startTokenIndex;
                }
                result.Add(memory[startTokenIndex..pos].ToString().Replace("\"", ""));
            }

            while (pos < memory.Length)
            {
                var c = memory.Span[pos];

                if (char.IsWhiteSpace(c))
                {
                    if (seekingQuote == Boundary.QuoteStart)
                    {
                        switch (seeking)
                        {
                            case Boundary.WordEnd:
                                DoAdd();
                                startTokenIndex = pos;
                                seeking = Boundary.TokenStart;
                                break;

                            case Boundary.TokenStart:
                                startTokenIndex = pos;
                                break;
                        }
                    }
                }
                else if (c == '\"')
                {
                    if (seeking == Boundary.TokenStart)
                    {
                        switch (seekingQuote)
                        {
                            case Boundary.QuoteEnd:
                                DoAdd();
                                startTokenIndex = pos;
                                seekingQuote = Boundary.QuoteStart;
                                break;

                            case Boundary.QuoteStart:
                                startTokenIndex = pos + 1;
                                seekingQuote = Boundary.QuoteEnd;
                                break;
                        }
                    }
                    else
                    {
                        switch (seekingQuote)
                        {
                            case Boundary.QuoteEnd:
                                seekingQuote = Boundary.QuoteStart;
                                break;

                            case Boundary.QuoteStart:
                                seekingQuote = Boundary.QuoteEnd;
                                break;
                        }
                    }
                }
                else if (seeking == Boundary.TokenStart && seekingQuote == Boundary.QuoteStart)
                {
                    seeking = Boundary.WordEnd;
                    startTokenIndex = pos;
                }

                pos++;

                if (pos == memory.Length)
                {
                    switch (seeking)
                    {
                        case Boundary.TokenStart:
                            break;
                        default:
                            DoAdd();
                            break;
                    }
                }
            }

            // dotnet can't be cezanne so we drop it if installed as a tool ("dotnet cezanne ...")
            if (result.Count > 0 && "dotnet" == result[0])
            {
                result.RemoveAt(0);
                currentWord--;
            }

            // first name is the binary - likely cezanne - so pointless for completion, just drop it
            // note: we can do it cause if not matched we don't have completion since we make it completing cezanne in the bash script
            if (result.Count > 0)
            {
                result.RemoveAt(0);
                currentWord--;
            }

            return new ParsedLine(result, currentWord, currentIndex);
        }

        private enum Boundary
        {
            TokenStart,
            WordEnd,
            QuoteStart,
            QuoteEnd
        }

        public class Settings : CommandSettings
        {
            [CommandArgument(0, "[command_line]")]
            public string? Line { get; set; }

            [CommandOption("--position")]
            [DefaultValue(-1)]
            public int? Position { get; set; }
        }
    }

    public record ParsedLine(
        IList<string> Args,
        int CurrentWordIndex,
        int CurrentCharIndexInCurrentWord
    ) { }

    public class BashCompletionCommand(
        IServiceCollection ioc,
        IServiceProvider provider,
        RecipeHandler handler
    ) : CompletionCommand<BashCompletionCommand.BashSettings>(ioc, provider, handler)
    {
        public override async Task<int> ExecuteAsync(CommandContext context, BashSettings settings)
        {
            return await base.ExecuteAsync(context, settings);
        }

        public class BashSettings : Settings { }
    }

    public class PowershellCompletionCommand(
        IServiceCollection ioc,
        IServiceProvider provider,
        RecipeHandler handler
    ) : CompletionCommand<PowershellCompletionCommand.PowershellSettings>(ioc, provider, handler)
    {
        public override async Task<int> ExecuteAsync(
            CommandContext context,
            PowershellSettings settings
        )
        {
            return await base.ExecuteAsync(context, settings);
        }

        public class PowershellSettings : Settings { }
    }
}
