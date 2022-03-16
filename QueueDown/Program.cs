using System.Buffers;
using System.Diagnostics.Tracing;
using System.IO.Pipelines;

namespace QueueDown;

partial class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("Running...");
        
        // This is the memory pool from Kestrel
        var pool = new PinnedBlockMemoryPool();
        var pipe = new Pipe(new(pool));
        var tasks = new List<Task>();
        
        MultiStagePipes.Run(pipe, pool);

        Pipes4(pipe, tasks, pool);
        // Pipes3(pipe, tasks, pool);
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
                ServiceEventSource.Log.ConsumeBytes(buffer.Length);
                reader.AdvanceTo(buffer.End);
            }
        });

        tasks.Add(consumer);

        await Task.WhenAll(tasks);
    }
}

public class ServiceEventSource : EventSource
{
    public static ServiceEventSource Log = new ServiceEventSource();
    private IncrementingPollingCounter? _invocationRateCounter;

    public long _bytesConsumed;

    public ServiceEventSource() : base("QueueDown")
    {

    }

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            _invocationRateCounter = new IncrementingPollingCounter("transfer-rate", this, () => Volatile.Read(ref _bytesConsumed) / (1024 * 1024))
            {
                DisplayRateTimeScale = TimeSpan.FromSeconds(1),
                DisplayUnits = "MB"
            };
        }
    }

    internal void ConsumeBytes(long bytesConsumed)
    {
        Interlocked.Add(ref _bytesConsumed, bytesConsumed);
    }
}
