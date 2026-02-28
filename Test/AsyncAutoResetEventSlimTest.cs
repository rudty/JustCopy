namespace Test;

using System.Collections.Concurrent;
using JustCopy;

[Collection("ALL")]
public class AsyncAutoResetEventSlimTests
{
    // 1. 기본 동작 테스트: Wait 하고 있다가 Set 하면 깨어나는가?
    [Fact]
    public async Task WaitOneAsync_Should_Wait_Until_Set()
    {
        // Arrange
        var evt = new AsyncAutoResetEventSlim(false);
        var isCompleted = false;

        // Act
        var task = Task.Run(async () =>
        {
            await evt.WaitOneAsync();
            isCompleted = true;
        });

        // Assert 1: 아직 Set 안 했으므로 끝나면 안 됨
        await Task.Delay(50);
        Assert.False(isCompleted, "Set을 호출하기 전에는 완료되면 안 됩니다.");

        // Act 2: 신호 보냄
        evt.Set();

        // Assert 2: 깨어나야 함 (약간의 여유 시간 대기)
        await Task.WhenAny(task, Task.Delay(1000));
        Assert.True(isCompleted, "Set 호출 후에는 작업이 완료되어야 합니다.");
    }

    // 2. FIFO 순서 보장 테스트: 먼저 대기한 놈이 먼저 나가는가?
    [Fact]
    public async Task WaitOneAsync_Should_Preserve_FIFO_Order()
    {
        // Arrange
        var evt = new AsyncAutoResetEventSlim(false);
        var completedOrder = new ConcurrentQueue<int>();
        var tasks = new List<Task>();
        var count = 5;

        // Act: 순서대로 줄 세우기
        for (var i = 0; i < count; i++)
        {
            var id = i;
            tasks.Add(Task.Run(async () =>
            {
                await evt.WaitOneAsync();
                completedOrder.Enqueue(id);
            }));
            // 확실한 Enqueue 순서 보장을 위해 미세한 지연
            await Task.Delay(10);
        }

        // Act: 하나씩 깨우기
        for (var i = 0; i < count; i++)
        {
            evt.Set();
            await Task.Delay(20); // 처리 순서가 섞이지 않게 텀을 줌
        }

        await Task.WhenAll(tasks);

        // Assert: 순서 검증
        var results = completedOrder.ToArray();
        Assert.Equal(count, results.Length);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(i, results[i]); // 0, 1, 2, 3, 4 순서여야 함
        }
    }

    // 3. 타임아웃 & 고스트 노드 테스트 (핵심 버그 픽스 검증)
    // 중간에 낀 노드가 타임아웃으로 나가도, 리스트 연결이 끊어지지 않는지 확인
    [Fact]
    public async Task Timeout_Should_Not_Break_LinkedList_Chain()
    {
        // Arrange
        var evt = new AsyncAutoResetEventSlim(false);

        // [T1] (Long Wait)
        var t1 = Task.Run(async () => await evt.WaitOneAsync(5000));

        // [T2] (Short Wait -> Timeout Expected)
        var t2 = Task.Run(async () => await evt.WaitOneAsync(100));

        // T2가 확실히 큐에 들어가도록 대기
        await Task.Delay(20);

        // [T3] (Long Wait)
        var t3 = Task.Run(async () => await evt.WaitOneAsync(5000));

        // Act 1: T2가 타임아웃 될 때까지 대기
        var t2Result = await t2;

        // Assert 1: T2는 타임아웃으로 false여야 함
        Assert.False(t2Result, "T2는 타임아웃으로 실패해야 합니다.");

        // Act 2: 신호를 줘서 T1을 깨움
        evt.Set();
        var t1Completed = await Task.WhenAny(t1, Task.Delay(500)) == t1;
        Assert.True(t1Completed, "T1이 신호를 받고 깨어나야 합니다. (Head 보존 확인)");

        // Act 3: 신호를 줘서 T3를 깨움
        // 만약 리스트가 끊어졌다면(Next가 null이 되었다면) T3는 영원히 못 깨어남
        evt.Set();
        var t3Completed = await Task.WhenAny(t3, Task.Delay(500)) == t3;
        Assert.True(t3Completed, "T3가 신호를 받고 깨어나야 합니다. (중간 링크 복구 확인)");
    }

    // 4. 동시성 스트레스 테스트 (Queue Corruption 확인)
    [Fact]
    public async Task StressTest_Should_Handle_Concurrent_Access()
    {
        // Arrange
        var evt = new AsyncAutoResetEventSlim(false);
        var threadCount = 20;
        var operationsPerThread = 100; // 총 2000번의 Wait/Set
        var totalConsumed = 0;

        var consumers = new Task[threadCount];
        var producers = new Task[threadCount];

        // Act
        for (var i = 0; i < threadCount; i++)
        {
            consumers[i] = Task.Run(async () =>
            {
                for (var j = 0; j < operationsPerThread; j++)
                {
                    await evt.WaitOneAsync();
                    Interlocked.Increment(ref totalConsumed);
                }
            });

            producers[i] = Task.Run(async () =>
            {
                for (var j = 0; j < operationsPerThread; j++)
                {
                    // 소비자가 준비될 때까지 조금 기다려주며 Set (신호 유실 방지 노력)
                    // 완전한 1:1 매칭보다는 "죽지 않고", "큐가 안 깨지는지"를 검증
                    while (true)
                    {
                        evt.Set();
                        await Task.Yield();
                        // 간단한 흐름 제어
                        if (totalConsumed < (j + 1) * threadCount)
                        {
                            break;
                        }
                    }
                }
            });
        }

        // Assert: 일정 시간 내에 완료되는지 확인 (Deadlock 감지)
        // Set이 Wait보다 많으면 무조건 완료되어야 함.
        // 여기서는 간단하게 생산자를 넉넉하게 돌려 소비자가 다 먹는지 확인.

        var cts = new CancellationTokenSource(); // 10초 타임아웃
        _ = Task.Run(async () =>
        {
            // 혹시라도 신호가 부족해서 멈추는 걸 방지하기 위해 
            // 소비자가 다 먹을 때까지 계속 두드려주는 도우미
            while (totalConsumed < threadCount * operationsPerThread && !cts.IsCancellationRequested)
            {
                evt.Set();
                await Task.Delay(1);
            }
        });

        var allConsumers = Task.WhenAll(consumers);
        var completedTask = await Task.WhenAny(allConsumers, Task.Delay(10000));

        Assert.Equal(allConsumers, completedTask); // 10초 안에 다 못 먹으면 Deadlock 의심
        Assert.Equal(threadCount * operationsPerThread, totalConsumed);
        cts.Cancel();
    }
}