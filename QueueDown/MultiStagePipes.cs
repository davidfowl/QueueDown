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
            var pipes = stage1Pipes.Readers; //fresh read incase this changed
            bool anyDataThisLoop = false;

            foreach (var inputPipe in pipes) 
            {
                if (inputPipe.TryRead(out var result)) 
                {
                    anyDataThisLoop = true;

                    await ProcessPipeResult(stage1Pipes, inputPipe, result, outputPipe);
                }
            }

            if(!anyDataThisLoop)
            {
                //take the slow path if we are starved
                var results = await stage1Pipes.WaitForMoreData();

                foreach(var result in results)
                {
                    await ProcessPipeResult(stage1Pipes, result.Item1, result.Item2, outputPipe);
                }
            }
        }
    }

    private static async Task ProcessPipeResult(PipeGroup group, PipeReader inputPipe, ReadResult result, PipeWriter outputPipe)
    {
        var inputBuffer = result.Buffer;

        if (inputBuffer.IsSingleSegment) 
        {
            outputPipe.Write(result.Buffer.FirstSpan);
        } 
        else 
        {
            foreach (var segment in result.Buffer) 
            {
                outputPipe.Write(segment.Span);
            }
        }

        var flushResult = await outputPipe.FlushAsync();

        inputPipe.AdvanceTo(inputBuffer.End);


        if (result.IsCompleted || flushResult.IsCompleted) {
            group.RemovePipe(inputPipe);
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

        public async Task<IReadOnlyList<(PipeReader, ReadResult)>> WaitForMoreData()
        {
            var source = new CancellationTokenSource();

            var readers = _readers;
            var waits = 
                readers.Select(reader => reader.ReadAsync(source.Token).AsTask()).ToArray();

            //ignore result because we can look check the Tasks for a result
            //multiple times
            await Task.WhenAny(waits);

            //cancel any pending reads...i think?
            //pipes are a little weird...
            source.Cancel();

            //collect any results that did finish
            List<(PipeReader, ReadResult)> results = new();
            for (int i = 0; i < readers.Length; i++) 
            {
                var task = waits[i];
                //probably need more error handling or something here
                if (task.IsCompleted)
                    results.Add((readers[i], task.Result));
            }

            return results;
        }
    }
}
