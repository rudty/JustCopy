namespace Benchmark;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Channels;
using System.Threading.Tasks;
using JustCopy;

internal static class BenchmarkUtils
{
    private const int growSize = 262144;
    internal static void GrowSegmentSize<T>(Channel<T> channel)
    {
        for (var i = 0; i < growSize; ++i)
        {
            channel.Writer.TryWrite(default!);
        }

        while (channel.Reader.TryRead(out _))
        {
            // 채널 초기화용
            // segment 단위로 linkedList 라서
            // 첫 segment 를 일정개수 만들도록 함
        }
    }

    internal static void GrowSegmentSize<T>(MpscBlockingQueue<T> queue)
    {
        for (var i = 0; i < growSize; ++i)
        {
            queue.Write(default!);
        }

        while (queue.TryRead(out _))
        {
            // 초기화용
        }
    }

    internal static void GrowSegmentSize<T>(MpmcLockBlockingQueue<T> queue)
    {
        for (var i = 0; i < growSize; ++i)
        {
            queue.Add(default!);
        }

        while (queue.TryTake(out _))
        {
            // 초기화용
        }
    }

    internal static void GrowSegmentSize<T>(BlockingCollection<T> queue)
    {
        for (var i = 0; i < growSize; ++i)
        {
            queue.Add(default!);
        }

        while (queue.TryTake(out _))
        {
            // 초기화용
        }
    }

    public static void GrowSegmentSize(SyncUnboundedChannel<int> b)
    {
        for (var i = 0; i < 262144; ++i)
        {
            b.TryAdd(default!);
        }

        while (b.TryTake(out _))
        {
            // 초기화용
        }
    }

    internal static Task StartSingleConsumer(Func<Task> function, TaskScheduler? scheduler = null)
    {
        scheduler ??= TaskScheduler.Default;
        var consumer = Task.Factory.StartNew(
            function,
            CancellationToken.None,
            TaskCreationOptions.None,
            scheduler
        ).Unwrap();
        return consumer;
    }

    internal static Task[] StartProducers(Func<Task> function, int count, Func<TaskScheduler>? getScheduler = null)
    {
        getScheduler ??= () => TaskScheduler.Default;
        var tasks = new Task[count];

        for (var i = 0; i < count; i++)
        {
            tasks[i] = Task.Factory.StartNew(
                function,
                CancellationToken.None,
                TaskCreationOptions.None,
                getScheduler()
            ).Unwrap();
        }

        return tasks;
    }
}

internal sealed class SingleThreadTaskScheduler : TaskScheduler, IDisposable
{
    private readonly MpscBlockingQueue<Task> _taskQueue;
    private readonly Thread _workerThread;
    private readonly CancellationTokenSource _cts;

    public SingleThreadTaskScheduler()
    {
        _taskQueue = new MpscBlockingQueue<Task>();
        _cts = new CancellationTokenSource();
        _workerThread = new Thread(WorkerLoop) { IsBackground = true };
        _workerThread.Start();
    }
    protected override void QueueTask(Task task) => _taskQueue.Write(task);
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
    {
        if (Thread.CurrentThread == _workerThread)
        {
            return TryExecuteTask(task);
        }

        return false;
    }
    protected override IEnumerable<Task> GetScheduledTasks() => null!;
    private void WorkerLoop()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                if (_taskQueue.TryRead(out var task))
                {
                    TryExecuteTask(task);
                }
                else
                {
                    _taskQueue.WaitToRead();
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
    }
}

/// <summary>
/// 벤치마크용 Long-Lived Worker 스레드 관리자.
/// 스레드 생성 비용과 OS Jitter를 제거하기 위해 미리 스레드를 생성하고 재사용합니다.
/// </summary>
public sealed class BenchmarkThreadPool : IDisposable
{
    private readonly Barrier _startBarrier;
    private readonly Barrier _endBarrier;
    private readonly Thread[] _threads;
    private volatile bool _isRunning;
    private bool _disposed;

    // 벤치마크 메서드에서 주입할 실제 작업 (교체 가능)
    public Action? ProducerAction { get; set; }
    public Action? ConsumerAction { get; set; }

    /// <summary>
    /// 스레드를 생성하고 대기 상태로 만듭니다. (GlobalSetup에서 호출)
    /// </summary>
    /// <param name="workerCount">생산자 수 (소비자 수도 동일하게 생성됨)</param>
    public BenchmarkThreadPool(int producerCount, int consumerCount)
    {
        _isRunning = true;

        // 1. 총 워커 수 = 생산자 + 소비자
        var totalWorkers = producerCount + consumerCount;

        // 2. 배리어 참여자 = 총 워커 + 메인 스레드(1)
        // (기존 코드의 *2 오류 수정: 이미 totalWorkers에 합산되어 있음)
        var totalParticipants = totalWorkers + 1;

        _startBarrier = new Barrier(totalParticipants);
        _endBarrier = new Barrier(totalParticipants);

        // 스레드 배열은 메인 스레드를 제외한 워커 수만큼만 할당
        _threads = new Thread[totalWorkers];

        var threadArrayIndex = 0;

        // 3. 소비자 스레드 생성
        for (var i = 0; i < consumerCount; i++)
        {
            _threads[threadArrayIndex] = new Thread(ConsumerLoop)
            {
                IsBackground = true,
                Name = $"Benchmark-Consumer-{i}",
                Priority = ThreadPriority.Highest
            };
            _threads[threadArrayIndex].Start();

            threadArrayIndex++; // 다음 배열 칸으로 이동
        }

        // 4. 생산자 스레드 생성
        for (var i = 0; i < producerCount; i++)
        {
            _threads[threadArrayIndex] = new Thread(ProducerLoop)
            {
                IsBackground = true,
                Name = $"Benchmark-Producer-{i}",
                Priority = ThreadPriority.Highest
            };
            _threads[threadArrayIndex].Start();

            threadArrayIndex++; // 다음 배열 칸으로 이동
        }
    }

    /// <summary>
    /// [Benchmark] 메서드에서 호출.
    /// 모든 스레드를 동시에 출발시키고, 작업이 끝날 때까지 대기합니다.
    /// </summary>
    public void Execute()
    {
        // 1. 땅! 출발 신호 (모든 스레드 동시 시작)
        _startBarrier.SignalAndWait();

        // 2. 모든 스레드 작업 완료 대기
        _endBarrier.SignalAndWait();
    }

    private void ProducerLoop()
    {
        while (_isRunning)
        {
            try
            {
                _startBarrier.SignalAndWait(); // 대기
                ProducerAction?.Invoke(); // 작업 수행
                _endBarrier.SignalAndWait(); // 보고
            }
            catch (BarrierPostPhaseException)
            {
                return;
            }
            catch
            {
                // ignore
            }
        }
    }

    private void ConsumerLoop()
    {
        while (_isRunning)
        {
            try
            {
                _startBarrier.SignalAndWait(); // 대기
                ConsumerAction?.Invoke(); // 작업 수행
                _endBarrier.SignalAndWait(); // 보고
            }
            catch (BarrierPostPhaseException)
            {
                return;
            }
            catch
            {
                // ignore
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _isRunning = false;

        // 배리어를 깨뜨려 대기 중인 스레드들을 강제 종료시킴
        try
        {
            _startBarrier.RemoveParticipants(_startBarrier.ParticipantCount);
        }
        catch
        {
            // ignore
        }

        try
        {
            _endBarrier.RemoveParticipants(_endBarrier.ParticipantCount);
        }
        catch
        {
            // ignore
        }
    }
}
