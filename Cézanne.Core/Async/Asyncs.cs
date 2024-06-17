namespace Cézanne.Core.Cli.Async
{
    public sealed class Asyncs
    {
        public static async Task All(bool chain, IEnumerable<Func<Task>> list)
        {
            if (chain)
            {
                foreach (Func<Task> item in list)
                {
                    await item();
                }
            }
            else
            {
                await Task.WhenAll(list.Select(it => it()));
            }
        }
    }
}