using System.Buffers;
using System.IO.Pipelines;

namespace QueueDown;

partial class Program
{
    private static void Pipes4(Pipe pipe, List<Task> tasks, MemoryPool<byte> pool)
    {
        var semaphore = new SemaphoreSlim(1);
        var writer = pipe.Writer;

        // Multiple producers
        for (int i = 0; i < 50; i++)
        {
            var producer1 = Task.Run(async () =>
            {
                var producerPipe = new Pipe(new(pool, readerScheduler: PipeScheduler.Inline));

                async Task DoWritesToSharedPipe()
                {
                    while (true)
                    {
                        var result = await producerPipe.Reader.ReadAsync();
                        await semaphore.WaitAsync();
                        foreach (var m in result.Buffer)
                        {
                            writer.Write(m.Span);
                        }
                        await writer.FlushAsync();
                        semaphore.Release();
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
