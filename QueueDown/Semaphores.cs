using System.IO.Pipelines;

partial class Program
{
    public static void Semaphores(Pipe pipe, List<Task> tasks)
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
