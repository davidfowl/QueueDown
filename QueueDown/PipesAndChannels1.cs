using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Channels;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Infrastructure.PipeWriterHelpers;

partial class Program
{
    public static void Pipes2(Pipe pipe, List<Task> tasks, MemoryPool<byte> pool)
    {
        var channel = Channel.CreateBounded<PipeReader>(new BoundedChannelOptions(50)
        {
            SingleReader = true
        });

        // Multiple producers
        for (int i = 0; i < 50; i++)
        {
            var producer1 = Task.Run(async () =>
            {
                var producerPipe = new Pipe(new(pool, pauseWriterThreshold: 1, resumeWriterThreshold: 1, readerScheduler: PipeScheduler.Inline));

                // Write to the pipe
                while (true)
                {
                    var buffer = producerPipe.Writer.GetMemory();
                    buffer.Span.Fill((byte)i);
                    producerPipe.Writer.Advance(buffer.Length);
                    var flushTask = producerPipe.Writer.FlushAsync();

                    // Queue the reader itself
                    channel.Writer.TryWrite(producerPipe.Reader);

                    // Wait for it to yield
                    await flushTask;
                }
            });
        }

        var producer = Task.Run(async () =>
        {
            var @lock = new object();
            var writer = new ConcurrentPipeWriter(pipe.Writer, pool, @lock);

            await foreach (var reader in channel.Reader.ReadAllAsync())
            {
                var result = await reader.ReadAsync();
                var buffer = result.Buffer;
                foreach (var m in buffer)
                {
                    writer.Write(m.Span);
                }
                await writer.FlushAsync();
                reader.AdvanceTo(buffer.End);
            }
        });

        tasks.Add(producer);
    }
}
