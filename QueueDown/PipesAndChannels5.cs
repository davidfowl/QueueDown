using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using System.Threading.Channels;

partial class Program
{
    // for debugging
    static Output[] s_outputs = new Output[50];

    public static PipeReader Pipes5(Counter<long> counter, List<Task> tasks)
    {
        //var pool = new PinnedBlockMemoryPool();
        var pool = MemoryPool<byte>.Shared;
        var channel = Channel.CreateBounded<Output>(new BoundedChannelOptions(50) 
        {
            SingleReader = true
        });

        // Multiple producers
        for (int i = 0; i < 50; i++)
        {
            var producerPipe = new Pipe(new(pool, minimumSegmentSize:64*1024 ));
            var output = new Output { Reader = producerPipe.Reader, Id = i.ToString() };
            s_outputs[i] = output;
            var b = (byte)i;

            var producer1 = Task.Run(async () =>
            {
                // Write to the pipe
                while (true)
                {
                    var buffer = producerPipe.Writer.GetMemory();
                    int writeSize = 4096;
                    buffer.Span.Slice(0, writeSize).Fill(b);
                    producerPipe.Writer.Advance(writeSize);
                    bool queue = output.Enqueue(writeSize);
                    var flushTask = producerPipe.Writer.FlushAsync();
                    if (queue)
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
        return new MultiPipeReader(channel, counter);
    }

    class MultiPipeReader : PipeReader
    {
        Channel<Output> _channel;
        Counter<long> _counter;
        IAsyncEnumerator<Output> _enum;
        ReadOnlySequence<byte> _lastBuffer;

        public MultiPipeReader(Channel<Output> channel, Counter<long> counter)
        {
            _channel = channel;
            _counter = counter;
            _enum = channel.Reader.ReadAllAsync().GetAsyncEnumerator();
        }

        Output Current => _enum.Current;

        public override void AdvanceTo(SequencePosition consumed)
        {
            Debug.Assert(consumed.Equals(_lastBuffer.End));
            if (Current.Dequeue(_lastBuffer.Length))
            {
                // Add this stream to the back of the queue if data needs to be written
                _channel.Writer.TryWrite(Current);
            }
            _counter.Add(_lastBuffer.Length / 1024, KeyValuePair.Create("Stream", (object?)Current.Id));
            Current.Reader.AdvanceTo(consumed);
        }

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            Debug.Assert(consumed.Equals(_lastBuffer.End));
            if (Current.Dequeue(_lastBuffer.Length))
            {
                // Add this stream to the back of the queue if data needs to be written
                _channel.Writer.TryWrite(Current);
            }
            _counter.Add(_lastBuffer.Length / 1024, KeyValuePair.Create("Stream", (object?)Current.Id));
            Current.Reader.AdvanceTo(consumed, examined);
        }

        public override void CancelPendingRead()
        {
            Current.Reader.CancelPendingRead();
        }

        public override void Complete(Exception? exception = null)
        {
            Current.Reader.Complete(exception);
        }

        public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            bool b = await _enum.MoveNextAsync();
            Debug.Assert(b);
            var ret = await Current.Reader.ReadAsync(cancellationToken);
            _lastBuffer = ret.Buffer;
            return ret;
        }

        public override bool TryRead(out ReadResult result)
        {
            var ret = _enum.Current.Reader.TryRead(out result);
            _lastBuffer = result.Buffer;
            return ret;
        }
    }
}
