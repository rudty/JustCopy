namespace Benchmark;

using BenchmarkDotNet.Attributes;
using System.Threading.Channels;
using System.Threading.Tasks;
using JustCopy; 

[MemoryDiagnoser]
public class MpscAsyncBenchmark
{
    public int TotalItems = 10_000;

    [Params(1, 2, 4)]
    public int WorkerCount;
    private int itemsPerProducer;

    [GlobalSetup]
    public void Setup()
    {
        itemsPerProducer = TotalItems / WorkerCount;
    }

    // 꽉 차서 스핀이 걸리지 않도록 넉넉하게 잡을 것
    private static readonly Channel<int> systemThreadingChannel = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });

    private readonly MpscUnboundedChannel<int> mpscUnbounded = new ();
    private readonly MpscBoundedChannel<int> mpscBoundedChannel = new (131072);

    static MpscAsyncBenchmark()
    {
        BenchmarkUtils.GrowSegmentSize(systemThreadingChannel);
    }

    [Benchmark]
    public async Task JustCopyMpscUnbounded()
    {
        var consumerTask = Task.Run(async () =>
        {
            var received = 0;
            while (received < TotalItems)
            {
                if (mpscUnbounded.TryRead(out _))
                {
                    received++;
                }
                else
                {
                    await mpscUnbounded.WaitToReadAsync();
                }
            }
        });

        var producers = StartProducers(() =>
        {
            for (var j = 0; j < itemsPerProducer; j++)
            {
                mpscUnbounded.TryWrite(j);
            }

            return Task.CompletedTask;
        });

        await Task.WhenAll(producers);
        await consumerTask;
    }

    
    [Benchmark]
    public async Task JustCopyMpscBounded_131072()
    {
        var consumerTask = Task.Run(async () =>
        {
            var received = 0;
            while (received < TotalItems)
            {
                if (mpscBoundedChannel.TryRead(out _))
                {
                    received++;
                }
                else
                {
                    await mpscBoundedChannel.WaitToReadAsync();
                }
            }
        });

        var producers = StartProducers(() =>
        {
            for (var j = 0; j < itemsPerProducer; j++)
            {
                mpscBoundedChannel.Write(j);
            }

            return Task.CompletedTask;
        });

        await Task.WhenAll(producers);
        await consumerTask;
    }

    [Benchmark(Baseline = true)]
    public async Task StandardChannelSingleReader()
    {
        var reader = systemThreadingChannel.Reader;
        var writer = systemThreadingChannel.Writer;

        var consumerTask = BenchmarkUtils.StartSingleConsumer(async () =>
        {
            var received = 0;
            while (received < TotalItems)
            {
                if (reader.TryRead(out _))
                {
                    received++;
                }
                else
                {
                    await reader.WaitToReadAsync();
                }
            }
        });

        var producers = StartProducers(() =>
        {
            for (var j = 0; j < itemsPerProducer; j++)
            {
                writer.TryWrite(j);
            }

            return Task.CompletedTask;
        });

        await Task.WhenAll(producers);
        await consumerTask;
    }

    private Task[] StartProducers(Func<Task> producerAction)
    {
        return BenchmarkUtils.StartProducers(producerAction, WorkerCount);
    }
}
