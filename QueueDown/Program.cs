using System.Buffers;
using System.Diagnostics.Metrics;
using System.IO.Pipelines;

using var meter = new Meter("QueueDown");
var counter = meter.CreateCounter<long>("transfer-rate", "KiB");


var tasks = new List<Task>();

// Pipes4(pipe, tasks, pool);
//var reader = Pipes3(counter, tasks);
var reader = Pipes5(counter, tasks);
// Pipes2(pipe, tasks, pool);
// Pipes(pipe, tasks, pool);
// Semaphores(pipe, tasks);
// Channels(pipe, tasks);

var consumer = Task.Run(async () =>
{
    while (true)
    {
        var result = await reader.ReadAsync();
        var buffer = result.Buffer;
        counter.Add(buffer.Length / 1024);
        reader.AdvanceTo(buffer.End);
    }
});

tasks.Add(consumer);

await Task.WhenAll(tasks);