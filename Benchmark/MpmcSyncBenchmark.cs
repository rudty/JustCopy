namespace Benchmark;
using System.Collections.Concurrent;
using System.Reflection.PortableExecutable;
using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using JustCopy;

/// <summary>
/// 멀티 스레드의 생산자/소비자 구조에서 동기적으로 블락 하는 벤치마크
/// </summary>
[MemoryDiagnoser]
public class MpmcSyncBenchmark
{
    private const int MaxWorkerCount = 4;

    // 생산자와 소비자의 스레드 쌍(Pair) 개수
    [Params(1, 2, 3, MaxWorkerCount)]
    public int ConsumerCount;

    [Params(1, 2, 3, MaxWorkerCount)]
    public int ProducerCount;

    // 🚀 수정됨: 1, 2, 3, 4로 완벽하게 나누어 떨어지도록 1200으로 고정 (최소공배수 활용)
    private const int TotalItems = 1200;

    public int ItemsPerProducer => TotalItems / ProducerCount;
    public int ItemsPerConsumer => TotalItems / ConsumerCount;

    private static readonly Channel<int> systemThreadingChannel = Channel.CreateUnbounded<int>(
        new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    private static readonly BlockingCollection<int> systemThreadingBlockingCollection = new();
    private static readonly MpmcLockBlockingQueue<int> justCopyMpmcLockBlockingQueue = new();
    private static readonly SyncUnboundedChannel<int> syncUnboundedChannel = new();

    private BenchmarkThreadPool threadPool = null!;

    static MpmcSyncBenchmark()
    {
        BenchmarkUtils.GrowSegmentSize(systemThreadingChannel);
        BenchmarkUtils.GrowSegmentSize(systemThreadingBlockingCollection);
        BenchmarkUtils.GrowSegmentSize(justCopyMpmcLockBlockingQueue);
        BenchmarkUtils.GrowSegmentSize(syncUnboundedChannel);
    }

    [GlobalSetup]
    public void Setup()
    {
        threadPool = new BenchmarkThreadPool(ProducerCount, ConsumerCount);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        threadPool.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void Standard_BlockingCollection()
    {
        var itemsPerConsumer = ItemsPerConsumer;
        threadPool.ConsumerAction = () =>
        {
            for (var j = 0; j < itemsPerConsumer; j++)
            {
                systemThreadingBlockingCollection.Take();
            }
        };

        var itemsPerProducer = ItemsPerProducer;
        threadPool.ProducerAction = () =>
        {
            for (var j = 0; j < itemsPerProducer; j++)
            {
                systemThreadingBlockingCollection.Add(1);
            }
        };

        threadPool.Execute();
    }

    [Benchmark]
    public void Standard_ChannelsUnbounded_AsTaskResult()
    {
        var reader = systemThreadingChannel.Reader;
        var writer = systemThreadingChannel.Writer;

        var itemsPerConsumer = ItemsPerConsumer;
        threadPool.ConsumerAction = () =>
        {
            var count = 0;
            while (reader.WaitToReadAsync().AsTask().Result)
            {
                while (reader.TryRead(out _))
                {
                    if (++count == itemsPerConsumer)
                    {
                        return;
                    }
                }
            }
        };

        var itemsPerProducer = ItemsPerProducer;
        threadPool.ProducerAction = () =>
        {
            for (var j = 0; j < itemsPerProducer; j++)
            {
                writer.TryWrite(1);
            }
        };

        threadPool.Execute();
    }

    [Benchmark]
    public void JustCopy_SyncUnboundedChannel()
    {

        var itemsPerConsumer = ItemsPerConsumer;
        threadPool.ConsumerAction = () =>
        {
            var count = 0;
            var queue = syncUnboundedChannel;
            while (queue.WaitToRead())
            {
                while (queue.TryTake(out _))
                {
                    if (++count == itemsPerConsumer)
                    {
                        return;
                    }
                }
            }
        };

        var itemsPerProducer = ItemsPerProducer;
        threadPool.ProducerAction = () =>
        {
            var queue = syncUnboundedChannel;
            for (var j = 0; j < itemsPerProducer; j++)
            {
                queue.TryAdd(1);
            }
        };

        threadPool.Execute();
    }

    [Benchmark]
    public void JustCopy_MpmcLockBlockingQueue()
    {
        var itemsPerConsumer = ItemsPerConsumer;
        threadPool.ConsumerAction = () =>
        {
            var queue = justCopyMpmcLockBlockingQueue;
            var count = 0;

            while (true)
            {
                _ = queue.Take();
                if (++count == itemsPerConsumer)
                {
                    return;
                }
            }
        };

        var itemsPerProducer = ItemsPerProducer;
        threadPool.ProducerAction = () =>
        {
            for (var i = 0; i < itemsPerProducer; i++)
            {
                justCopyMpmcLockBlockingQueue.Add(1);
            }
        };

        threadPool.Execute();
    }
}