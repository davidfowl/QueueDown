using System.Buffers;
using System.Collections.Immutable;
using System.IO.Pipelines;

namespace QueueDown;

public static class MultiStagePipes
{
    /// <summary>
    /// Basically tries to reduce write contention but giving each thread/worker
    /// it's own pipe to write to. Then a single reader pushes input into the
    /// output pipe.
    /// </summary>
    public static void Run(Pipe outputPipe, MemoryPool<byte> pool)
    {
        PipeGroup readers = new();

        for (int i = 0; i < 50; i++) {
            var __ = Task.Run(async () => {
                var producerPipe = new Pipe(new(pool));
                readers.AddPipe(producerPipe.Reader);


                // Write to the pipe
                while (true) {
                    var buffer = producerPipe.Writer.GetMemory();
                    buffer.Span.Fill((byte)i);
                    producerPipe.Writer.Advance(buffer.Length);
                    await producerPipe.Writer.FlushAsync();
                }
            });
        }

        var _ = Task.Run(() => ReadAllPipes(readers, outputPipe.Writer));
    }

    /// <summary>
    /// Reads from pipes in a round-robin manner and pushes output into final pipe
    /// </summary>
    private static async Task ReadAllPipes(PipeGroup stage1Pipes, PipeWriter outputPipe)
    {
        while (true) {
            var pipes = stage1Pipes.Readers;

            foreach (var inputPipe in pipes) 
            {
                if (inputPipe.TryRead(out var result)) 
                {
                    var inputBuffer = result.Buffer;

                    if (inputBuffer.IsSingleSegment) 
                    {
                        outputPipe.Write(result.Buffer.FirstSpan);
                    } 
                    else 
                    {
                        foreach (var segment in result.Buffer) {
                            outputPipe.Write(segment.Span);
                        }
                    }
                    
                    var flushResult = await outputPipe.FlushAsync();

                    inputPipe.AdvanceTo(inputBuffer.End);

                    if (result.IsCompleted || flushResult.IsCompleted) {
                        stage1Pipes.RemovePipe(inputPipe);
                        continue;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Provides a way to add and remove pipes in a fast/thread safe way
    /// </summary>
    public class PipeGroup
    {
        private ImmutableArray<PipeReader> _readers = ImmutableArray<PipeReader>.Empty;

        public ImmutableArray<PipeReader> Readers => _readers;


        public void AddPipe(PipeReader reader)
        {
            ImmutableInterlocked.Update(ref _readers, static (array, arg) => array.Add(arg), reader);
        }

        public void RemovePipe(PipeReader reader)
        {
            ImmutableInterlocked.Update(ref _readers, static (array, arg) => array.Remove(arg), reader);
        }
    }
}
