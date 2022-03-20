using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using System.Threading.Channels;

partial class Program
{
    public static PipeReader Pipes3(Counter<long> counter, List<Task> tasks)
    {
        // This is the memory pool from Kestrel
        var pool = new PinnedBlockMemoryPool();
        var pipe = new Pipe(new(pool));
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
                    var flushTask = producerPipe.Writer.FlushAsync();
                    // I think there is a bug here?
                    // As soon as FlushAsync runs the reader could read the data
                    // and underflow the unconsumedBytes. Pipes5 moved the Enqueue before
                    // FlushAsync() to avoid that
                    if (output.Enqueue(buffer.Length))
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
        return pipe.Reader;
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
