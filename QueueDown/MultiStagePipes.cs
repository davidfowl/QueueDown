using System.Buffers;
using System.Collections.Immutable;
using System.IO.Pipelines;

namespace QueueDown;

public static class MultiStagePipes
{
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

    public static void Run(Pipe outputPipe, List<Task> tasks, MemoryPool<byte> pool)
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

    static async Task ReadAllPipes(PipeGroup stage1Pipes, PipeWriter outputPipe)
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
}
