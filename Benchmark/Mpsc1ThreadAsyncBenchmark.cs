namespace Benchmark;

using JustCopy;
using System;
using System.Threading.Tasks;
using System.Threading;
using BenchmarkDotNet.Attributes;
using System.Threading.Channels;

[MemoryDiagnoser]
public class Mpsc1ThreadAsyncBenchmark
{
    // 총 아이템 수는 통계용으로 표시만 합니다 (입력값 아님)
    public int TotalItems => ActorCount * ItemsPerActor;

    // 액터(채널)의 개수
    //[Params(3000)]
    public readonly int ActorCount = 3000;

    // 각 액터가 처리할 메시지 수
    //[Params(4000)]
    public readonly int ItemsPerActor = 4000;

    [Params(1, 2, MaxWorkerCount)]
    public int WorkerCount;

    private const int MaxWorkerCount = 4;

    private static readonly SingleThreadTaskScheduler _consumerThreadScheduler = new();
    private static readonly SingleThreadTaskScheduler[] _producerThreadSchedulers = new SingleThreadTaskScheduler[MaxWorkerCount];
    private int inc;

    private static readonly MpscUnboundedChannel<int> mpscUnboundedChannel = new();
    private static readonly MpscBoundedChannel<int> mpscBoundedChannel = new();
    private static readonly Channel<int> systemThreadingChannel = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });

    static Mpsc1ThreadAsyncBenchmark()
    {
        for (var i = 0; i < _producerThreadSchedulers.Length; i++)
        {
            _producerThreadSchedulers[i] = new SingleThreadTaskScheduler();
        }

        BenchmarkUtils.GrowSegmentSize(systemThreadingChannel);
    }

    public TaskScheduler NextProducerThreadScheduler()
    {
        var index = Interlocked.Increment(ref inc) % WorkerCount;
        return _producerThreadSchedulers[index];
    }

    public async ValueTask DoSleep(int index)
    {
        if (index % 100 == 0)
        {
            await Task.Yield();
        }
    }

    [Benchmark]
    public async Task JustCopy_1Thread_MpscUnboundedChannel()
    {

        var consumer = StartSingleConsumer(async () =>
        {
            var received = 0;
            while (received < TotalItems)
            {
                if (await mpscUnboundedChannel.WaitToReadAsync())
                {
                    while (mpscUnboundedChannel.TryRead(out _))
                    {
                        received++;
                    }
                }
            }
        });

        var producers = StartProducers(async () =>
        {
            for (var j = 0; j < ItemsPerActor; j++)
            {
                mpscUnboundedChannel.TryWrite(j);
                await DoSleep(j);
            }
        });

        await consumer;
        await Task.WhenAll(producers);
    }

    [Benchmark]
    public async Task JustCopy_1Thread_BoundedChannel_131072()
    {
        var consumer = StartSingleConsumer(async () =>
        {
            var received = 0;
            while (received < TotalItems)
            {
                if (await mpscBoundedChannel.WaitToReadAsync())
                {
                    while (mpscBoundedChannel.TryRead(out _))
                    {
                        received++;
                    }
                }
            }
        });

        var producers = StartProducers(async () =>
        {
            for (var j = 0; j < ItemsPerActor; j++)
            {
                mpscBoundedChannel.Write(j);
                await DoSleep(j);
            }
        });

        await consumer;
        await Task.WhenAll(producers);
    }

    [Benchmark(Baseline = true)]
    public async Task Standard_1Thread_ChannelSingleReader()
    {
        var reader = systemThreadingChannel.Reader;
        var writer = systemThreadingChannel.Writer;

        var consumer = StartSingleConsumer(async () =>
        {
            var received = 0;
            while (received < TotalItems)
            {
                if (await reader.WaitToReadAsync())
                {
                    while (reader.TryRead(out _))
                    {
                        received++;
                    }
                }
            }
        });

        var producers = BenchmarkUtils.StartProducers(async () =>
        {
            for (var j = 0; j < ItemsPerActor; j++)
            {
                writer.TryWrite(j);
                await DoSleep(j);
            }
        }, ActorCount);

        await consumer;
        await Task.WhenAll(producers);
    }

    private Task StartSingleConsumer(Func<Task> consumerAction)
    {
        return BenchmarkUtils.StartSingleConsumer(consumerAction, _consumerThreadScheduler);
    }

    private Task[] StartProducers(Func<Task> producerAction)
    {
        return BenchmarkUtils.StartProducers(producerAction, ActorCount, NextProducerThreadScheduler);
    }
}
