using System.Buffers;
using System.Diagnostics.Tracing;
using System.IO.Pipelines;
using System.Threading.Channels;
using System.Threading.Tasks.Sources;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Infrastructure.PipeWriterHelpers;

namespace ConsoleApp24
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var pool = new PinnedBlockMemoryPool();
            var pipe = new Pipe(new(pool));
            var tasks = new List<Task>();

            Pipes3(pipe, tasks, pool);
            // Pipes2(pipe, tasks, pool);
            // Pipes(pipe, tasks, pool);
            // Semaphores(pipe, tasks);
            // Channels(pipe, tasks);

            var consumer = Task.Run(async () =>
            {
                var reader = pipe.Reader;
                while (true)
                {
                    var result = await reader.ReadAsync();
                    var buffer = result.Buffer;
                    ServiceEventSource.Log.ConsumeBytes(buffer.Length);
                    reader.AdvanceTo(buffer.End);
                }
            });

            tasks.Add(consumer);

            await Task.WhenAll(tasks);
        }

        private static void Pipes(Pipe pipe, List<Task> tasks, MemoryPool<byte> pool)
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

        private static void Pipes3(Pipe pipe, List<Task> tasks, MemoryPool<byte> pool)
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

        private static void Pipes2(Pipe pipe, List<Task> tasks, MemoryPool<byte> pool)
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

        private static void Channels(Pipe pipe, List<Task> tasks)
        {
            var channel = Channel.CreateBounded<(byte[], ManualResetValueTaskSource<object?>)>(new BoundedChannelOptions(50)
            {
                SingleReader = true
            });

            // Multiple producers
            for (int i = 0; i < 50; i++)
            {
                var producer1 = Task.Run(async () =>
                {
                    var buffer = new byte[4096];

                    var mrvts = new ManualResetValueTaskSource<object?>();

                    while (true)
                    {
                        Array.Fill(buffer, (byte)i);
                        await channel.Writer.WriteAsync((buffer, mrvts));
                        await new ValueTask(mrvts, mrvts.Version);
                        mrvts.Reset();
                    }
                });
            }

            var producer = Task.Run(async () =>
            {
                var writer = pipe.Writer;

                await foreach (var (data, ch) in channel.Reader.ReadAllAsync())
                {
                    await writer.WriteAsync(data);
                    ch.SetResult(null);
                }
            });

            tasks.Add(producer);
        }

        private static void Semaphores(Pipe pipe, List<Task> tasks)
        {
            var s = new SemaphoreSlim(1);
            // Multiple producers
            for (int i = 0; i < 50; i++)
            {
                var producer = Task.Run(async () =>
                {
                    var writer = pipe.Writer;

                    var buffer = new byte[4096];

                    while (true)
                    {
                        await s.WaitAsync();
                        try
                        {
                            Array.Fill(buffer, (byte)i);
                            await writer.WriteAsync(buffer);
                        }
                        finally
                        {
                            s.Release();
                        }
                    }
                });

                tasks.Add(producer);
            }
        }
    }

    public class ServiceEventSource : EventSource
    {
        public static ServiceEventSource Log = new ServiceEventSource();
        private IncrementingPollingCounter? _invocationRateCounter;

        public long _bytesConsumed;

        public ServiceEventSource() : base("MyApp")
        {

        }

        protected override void OnEventCommand(EventCommandEventArgs command)
        {
            if (command.Command == EventCommand.Enable)
            {
                _invocationRateCounter = new IncrementingPollingCounter("transfer-rate", this, () => Volatile.Read(ref _bytesConsumed))
                {
                    DisplayRateTimeScale = TimeSpan.FromSeconds(1),
                    DisplayUnits = "B"
                };
            }
        }

        internal void ConsumeBytes(long bytesConsumed)
        {
            Interlocked.Add(ref _bytesConsumed, bytesConsumed);
        }
    }
}

internal sealed class ManualResetValueTaskSource<T> : IValueTaskSource<T>, IValueTaskSource
{
    private ManualResetValueTaskSourceCore<T> _core; // mutable struct; do not make this readonly

    public bool RunContinuationsAsynchronously { get => _core.RunContinuationsAsynchronously; set => _core.RunContinuationsAsynchronously = value; }
    public short Version => _core.Version;
    public void Reset() => _core.Reset();
    public void SetResult(T result) => _core.SetResult(result);
    public void SetException(Exception error) => _core.SetException(error);

    public T GetResult(short token) => _core.GetResult(token);
    void IValueTaskSource.GetResult(short token) => _core.GetResult(token);
    public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);
    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _core.OnCompleted(continuation, state, token, flags);

    public ValueTaskSourceStatus GetStatus() => _core.GetStatus(_core.Version);

    public void TrySetResult(T result)
    {
        if (_core.GetStatus(_core.Version) == ValueTaskSourceStatus.Pending)
        {
            _core.SetResult(result);
        }
    }
}