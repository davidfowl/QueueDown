using System.Buffers;
using System.Diagnostics.Metrics;
using System.IO.Pipelines;

namespace QueueDown;

partial class Program
{
    static async Task Main(string[] args)
    {
        using var meter = new Meter("QueueDown");
        var counter = meter.CreateCounter<long>("transfer-rate", "B");

        // This is the memory pool from Kestrel
        var pool = new PinnedBlockMemoryPool();
        var pipe = new Pipe(new(pool));
        var tasks = new List<Task>();

        // Pipes4(pipe, tasks, pool);
        Pipes3(pipe, tasks, pool);
        // Pipes2(pipe, tasks, pool);
        // Pipes(pipe, tasks, pool);
        // Semaphores(pipe, tasks);
        // Channels(pipe, tasks);

        var consumer = Task.Run(async () =>
        {
            var reader = pipe.Reader;
            while (true)
            {
                var result = await reader.ReadAsync();
                var buffer = result.Buffer;
                counter.Add(buffer.Length);
                reader.AdvanceTo(buffer.End);
            }
        });

        tasks.Add(consumer);

        await Task.WhenAll(tasks);
    }
}