using System.Collections.Immutable;

namespace Cézanne.Core.Cli.Completable
{
    /// <summary>
    /// When implemented by a <code>(Async)Command</code> it enables to get completion on its specific settings.
    /// It is inspired from <code>dotnet complete</code> command, see https://anthonysimmon.com/enable-dotnet-cli-tab-completion-terminal/.
    /// </summary>
    public interface ICompletable
    {
        public async Task<IEnumerable<string>> CompleteOptionAsync(
            string option,
            IList<string> args,
            int currentWord
        )
        {
            return await Task.FromResult(ImmutableList<string>.Empty);
        }
    }
}
