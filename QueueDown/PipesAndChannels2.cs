using System.Buffers;
using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using System.Threading.Channels;

partial class Program
{
    public static void Pipes3(Pipe pipe, Counter<long> counter, List<Task> tasks, MemoryPool<byte> pool)
    {
        var channel = Channel.CreateBounded<Output>(new BoundedChannelOptions(50)
        {
            SingleReader = true
        });

        // Multiple producers
        for (int i = 0; i < 50; i++)
        {
            var producerPipe = new Pipe(new(pool));
            var output = new Output { Reader = producerPipe.Reader, Id = i.ToString() };
            var b = (byte)i;

            var producer1 = Task.Run(async () =>
            {
                // Write to the pipe
                while (true)
                {
                    var buffer = producerPipe.Writer.GetMemory();
                    buffer.Span.Fill(b);
                    producerPipe.Writer.Advance(buffer.Length);
                    // Enqueue before flushing to make sure that it's impossible to 
                    // underflow the consumed bytes (it's visible to the reader as soon as we flush).
                    var enqueue = output.Enqueue(buffer.Length);
                    var flushTask = producerPipe.Writer.FlushAsync();

                    if (enqueue)
                    {
                        // This should never fail
                        channel.Writer.TryWrite(output);
                        // Queue the reader itself
                    }

                    // Wait for it to yield
                    await flushTask;
                }
            });

            tasks.Add(producer1);
        }

        var producer = Task.Run(async () =>
        {
            var writer = pipe.Writer;

            await foreach (var output in channel.Reader.ReadAllAsync())
            {
                var reader = output.Reader;
                // There should always be a result
                reader.TryRead(out var result);
                var buffer = result.Buffer;

                foreach (var m in buffer)
                {
                    writer.Write(m.Span);
                }
                await writer.FlushAsync();

                if (output.Dequeue(buffer.Length))
                {
                    // Add this stream to the back of the queue if data needs to be written
                    channel.Writer.TryWrite(output);
                }

                reader.AdvanceTo(buffer.End);
                counter.Add(buffer.Length / 1024, KeyValuePair.Create("Stream", (object?)output.Id));
            }
        });

        tasks.Add(producer);
    }

    class Output
    {
        private long _unconsumedBytes;

        public string Id { get; init; } = default!;

        public PipeReader Reader { get; init; } = default!;

        public bool Enqueue(long bytes)
        {
            return Interlocked.Add(ref _unconsumedBytes, bytes) == bytes;
        }

        public bool Dequeue(long bytes)
        {
            return Interlocked.Add(ref _unconsumedBytes, -bytes) > 0;
        }
    }
}
