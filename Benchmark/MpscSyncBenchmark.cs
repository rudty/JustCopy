namespace Benchmark;

using System.Reflection.PortableExecutable;
using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using JustCopy;

[MemoryDiagnoser]
public class MpscSyncBenchmark
{
    // 생산자 스레드 개수 
    [Params(1, 2, 4)] public int WorkerCount { get; set; }

    // 각 생산자가 큐에 넣을 데이터 개수
    public int ItemsPerProducer { get; set; } = 10_000;

    private int TotalItems => WorkerCount * ItemsPerProducer;

    private static readonly Channel<int> systemThreadingChannel = Channel.CreateUnbounded<int>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    private static readonly MpscBoundedChannel<int> mpscBoundedChannel = new(100_000 * 8);
    private static readonly MpscUnboundedChannel<int> mpscUnboundedChannel = new();
    private static readonly MpscBlockingQueue<int> mpscBlockingQueue = new();

    private BenchmarkThreadPool threadPool = null!;

    static MpscSyncBenchmark()
    {
        BenchmarkUtils.GrowSegmentSize(systemThreadingChannel);
        BenchmarkUtils.GrowSegmentSize(mpscBlockingQueue);
    }

    [GlobalSetup]
    public void Setup()
    {
        threadPool = new BenchmarkThreadPool(WorkerCount, 1);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        threadPool.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void Standard_ChannelsUnbounded()
    {
        var reader = systemThreadingChannel.Reader;
        var writer = systemThreadingChannel.Writer;

        threadPool.ProducerAction = () =>
        {
            for (var j = 0; j < ItemsPerProducer; j++)
            {
                writer.TryWrite(1);
            }
        };

        threadPool.ConsumerAction = () =>
        {
            var count = 0;
            while (reader.WaitToReadAsync().AsTask().Result)
            {
                while (reader.TryRead(out _))
                {
                    if (++count == TotalItems)
                    {
                        return;
                    }
                }
            }
        };

        threadPool.Execute();
    }

    //[Benchmark]
    //public void JustCopy_MpscBoundedChannel()
    //{

    //    threadPool.ProducerAction = () =>
    //    {
    //        for (var j = 0; j < ItemsPerProducer; j++)
    //        {
    //            mpscBoundedChannel.Write(1);
    //        }
    //    };

    //    threadPool.ConsumerAction = () =>
    //    {
    //        var count = 0;
    //        while (mpscBoundedChannel.WaitToReadAsync().AsTask().Result)
    //        {
    //            while (mpscBoundedChannel.TryRead(out _))
    //            {
    //                if (++count == TotalItems)
    //                {
    //                    return;
    //                }
    //            }
    //        }
    //    };

    //    threadPool.Execute();
    //}

    //[Benchmark]
    //public void JustCopy_MpscUnboundedChannel()
    //{
    //    threadPool.ProducerAction = () =>
    //    {
    //        for (var j = 0; j < ItemsPerProducer; j++)
    //        {
    //            mpscUnboundedChannel.TryWrite(1);
    //        }
    //    };

    //    threadPool.ConsumerAction = () =>
    //    {
    //        var count = 0;
    //        while (mpscUnboundedChannel.WaitToReadAsync().AsTask().Result)
    //        {
    //            while (mpscUnboundedChannel.TryRead(out _))
    //            {
    //                if (++count == TotalItems)
    //                {
    //                    return;
    //                }
    //            }
    //        }
    //    };

    //    threadPool.Execute();
    //}

    [Benchmark]
    public void JustCopy_MpscBlockingQueue()
    {

        threadPool.ProducerAction = () =>
        {
            for (var j = 0; j < ItemsPerProducer; j++)
            {
                mpscBlockingQueue.Write(1);
            }
        };

        threadPool.ConsumerAction = () =>
        {
            var count = 0;
            var queue = mpscBlockingQueue;
            while (queue.WaitToRead())
            {
                while (queue.TryRead(out _))
                {
                    if (++count == TotalItems)
                    {
                        return;
                    }
                }
            }
        };

        threadPool.Execute();
    }
}

