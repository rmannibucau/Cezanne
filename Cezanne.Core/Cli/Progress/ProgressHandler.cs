using System.Collections.Concurrent;
using Spectre.Console;

namespace Cezanne.Core.Cli.Progress
{
    public class ProgressHandler(ProgressContext ctx)
    {
        private readonly ConcurrentDictionary<string, ProgressTask> _pending = new();

        public void OnProgress(string artifact, double percent)
        {
            if (percent == 100)
            {
                if (_pending.Remove(artifact, out var progressTask))
                {
                    progressTask.Value = 100;
                }
                else
                {
                    // we still want to report it had been done even if insanely fast
                    ctx.AddTask(artifact).Value = 100;
                }
            }
            else
            {
                _pending.AddOrUpdate(
                    artifact,
                    key => ctx.AddTask(artifact),
                    (key, value) =>
                    {
                        value.Value = percent;
                        return value;
                    }
                );
            }
        }
    }

    internal class Pending
    {
        public double Current { get; set; } = 0;
    }
}
