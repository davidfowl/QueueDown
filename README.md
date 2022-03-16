# QueueDown

This experiment tries to find the most efficient way to improve throughput of lots of current writes to a shared resource (a PipeWriter in this example).
50 concurrent writes write to a single PipeWriter and the throughput can be measured using `dotnet counters monitor -n QueueDown --counters QueueDown System.Runtime`.