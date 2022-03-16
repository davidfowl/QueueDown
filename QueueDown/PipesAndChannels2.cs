using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Channels;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Infrastructure.PipeWriterHelpers;

namespace QueueDown;

partial class Program
{
    static void Pipes3(Pipe pipe, List<Task> tasks, MemoryPool<byte> pool)
    {
        var channel = Channel.CreateBounded<Output>(new BoundedChannelOptions(50)
        {
            SingleReader = true
        });

        // Multiple producers
        for (int i = 0; i < 50; i++)
        {
            var producer1 = Task.Run(async () =>
            {
                var producerPipe = new Pipe(new(pool));
                var output = new Output { Reader = producerPipe.Reader };

                // Write to the pipe
                while (true)
                {
                    var buffer = producerPipe.Writer.GetMemory();
                    buffer.Span.Fill((byte)i);
                    producerPipe.Writer.Advance(buffer.Length);
                    var flushTask = producerPipe.Writer.FlushAsync();

                    if (output.Enqueue())
                    {
                        // This should never fail
                        channel.Writer.TryWrite(output);
                        // Queue the reader itself
                    }

                    // Wait for it to yield
                    await flushTask;
                }
            });
        }

        var producer = Task.Run(async () =>
        {
            var @lock = new object();
            var writer = new ConcurrentPipeWriter(pipe.Writer, pool, @lock);

            await foreach (var output in channel.Reader.ReadAllAsync())
            {
                var reader = output.Reader;
                var result = await reader.ReadAsync();
                var buffer = result.Buffer;

                foreach (var m in buffer)
                {
                    writer.Write(m.Span);
                }
                await writer.FlushAsync();
                output.Dequeue();
                reader.AdvanceTo(buffer.End);
            }
        });

        tasks.Add(producer);
    }

    class Output
    {
        private int _state;

        public PipeReader Reader { get; init; } = default!;

        public bool Enqueue() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;

        public bool Dequeue() => Interlocked.Exchange(ref _state, 0) == 1;
    }
}
