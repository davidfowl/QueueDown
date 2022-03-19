using System.Buffers;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Infrastructure.PipeWriterHelpers;

partial class Program
{
    public static void Pipes(Pipe pipe, List<Task> tasks, MemoryPool<byte> pool)
    {
        var @lock = new object();
        var writer = new ConcurrentPipeWriter(pipe.Writer, pool, @lock);
        // Multiple producers
        for (int i = 0; i < 50; i++)
        {
            var producer1 = Task.Run(async () =>
            {
                ValueTask<FlushResult> DoCopy(PipeWriter writer, in ReadOnlySequence<byte> buffer)
                {
                    lock (@lock)
                    {
                        foreach (var m in buffer)
                        {
                            writer.Write(m.Span);
                        }
                        return writer.FlushAsync();
                    }
                }


                var producerPipe = new Pipe(new(pool, readerScheduler: PipeScheduler.Inline));

                async Task DoWritesToSharedPipe()
                {
                    while (true)
                    {
                        var result = await producerPipe.Reader.ReadAsync();
                        await DoCopy(writer, result.Buffer);
                        producerPipe.Reader.AdvanceTo(result.Buffer.End);
                    }
                }

                _ = DoWritesToSharedPipe();

                // Write to the pipe
                while (true)
                {
                    var buffer = producerPipe.Writer.GetMemory();
                    buffer.Span.Fill((byte)i);
                    producerPipe.Writer.Advance(buffer.Length);
                    await producerPipe.Writer.FlushAsync();
                }
            });

            tasks.Add(producer1);
        }
    }
}
