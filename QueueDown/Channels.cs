using System.IO.Pipelines;
using System.Threading.Channels;

namespace QueueDown;

partial class Program
{
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
}
