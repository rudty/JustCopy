namespace Test;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using JustCopy;

public class SyncUnboundedChannelTests
{
    [Fact(DisplayName = "1. 기본 입출력: TryAdd와 TryTake가 정상 작동해야 한다")]
    public void Basic_AddAndTake_ShouldWork()
    {
        var channel = new SyncUnboundedChannel<int>();

        Assert.True(channel.TryAdd(42));
        Assert.True(channel.WaitToRead(0)); // Fast-Path 확인

        Assert.True(channel.TryTake(out var item));
        Assert.Equal(42, item);
        Assert.False(channel.WaitToRead(0)); // 비었으므로 false
    }

    [Fact(DisplayName = "2. 대기 기상: WaitToRead는 데이터가 들어올 때까지 대기했다가 깨어나야 한다")]
    public async Task WaitToRead_ShouldBlockUntilItemAdded()
    {
        var channel = new SyncUnboundedChannel<string>();
        var stopwatch = Stopwatch.StartNew();

        var readTask = Task.Run(channel.WaitToRead);

        await Task.Delay(100); // 소비자가 먼저 깊은 잠에 빠지도록 대기
        Assert.True(channel.TryAdd("Hello"));

        var waitResult = await readTask;
        stopwatch.Stop();

        Assert.True(waitResult);
        Assert.True(stopwatch.ElapsedMilliseconds >= 90); // 최소 100ms 근처는 대기했어야 함
        Assert.True(channel.TryTake(out var result));
        Assert.Equal("Hello", result);
    }

    [Fact(DisplayName = "3. 타임아웃 지연 청소(Lazy Sweeping): 타임아웃 발생 후 생산자가 정상적으로 복구해야 한다")]
    public async Task Timeout_LazySweeping_ShouldNotBreakChannel()
    {
        var channel = new SyncUnboundedChannel<int>();

        // 1. 소비자가 100ms 대기 후 타임아웃으로 떠남 (상태가 State_TimedOut으로 남음)
        var isRead = channel.WaitToRead(100);
        Assert.False(isRead, "데이터가 없으므로 타임아웃 처리되어야 함");

        // 2. 소비자가 떠난 뒤 생산자가 데이터를 넣음 (이 과정에서 허공에 Pulse를 쏘지 않고 싱글톤을 복구해야 함)
        Assert.True(channel.TryAdd(99));

        // 3. 완전히 새로운 소비자가 데이터를 무사히 읽어갈 수 있어야 함
        var readTask = Task.Run(() => channel.WaitToRead(1000));
        Assert.True(await readTask);
        Assert.True(channel.TryTake(out var item));
        Assert.Equal(99, item);
    }

    [Fact(DisplayName = "4. Complete 1: 완료 후에는 추가를 거부하고, 대기 중인 소비자를 깨워야 한다")]
    public async Task Complete_ShouldRejectAdds_And_WakeUpWaiters()
    {
        var channel = new SyncUnboundedChannel<int>();

        // 소비자가 대기열에 진입
        var readTask = Task.Run(channel.WaitToRead);
        await Task.Delay(50); // 완전히 잠들 때까지 대기

        // 생산자 퇴근 선언
        channel.Complete();

        // 대기하던 소비자는 데이터 없이 false를 받고 깨어나야 함
        Assert.False(await readTask);

        // 완료된 이후에는 추가할 수 없어야 함
        Assert.False(channel.TryAdd(1));
    }

    [Fact(DisplayName = "5. Complete 2: 완료되어도 남은 데이터는 끝까지 읽을 수 있어야 한다")]
    public void Complete_ShouldAllowReadingExistingItems()
    {
        var channel = new SyncUnboundedChannel<int>();
        channel.TryAdd(1);
        channel.TryAdd(2);

        channel.Complete(); // 생산 끝

        Assert.True(channel.WaitToRead());
        Assert.True(channel.TryTake(out var item1));
        Assert.Equal(1, item1);

        Assert.True(channel.WaitToRead());
        Assert.True(channel.TryTake(out var item2));
        Assert.Equal(2, item2);

        // 다 파먹었으면 이제 false 반환
        Assert.False(channel.WaitToRead());
    }

    [Fact(DisplayName = "6. 예외 안전성: Thread.Interrupt 발생 시 채널이 고장나지 않아야 한다")]
    public void ThreadInterrupt_ShouldNotCorruptState()
    {
        var channel = new SyncUnboundedChannel<int>();
        Exception? thrownException = null;

        var thread = new Thread(() =>
        {
            try
            {
                channel.WaitToRead(); // 영원히 대기 시작
            }
            catch (Exception ex)
            {
                thrownException = ex;
            }
        });

        thread.Start();
        Thread.Sleep(50); // 스레드가 확실히 Monitor.Wait에 들어가도록 대기

        // 강제 인터럽트 공격!
        thread.Interrupt();
        thread.Join();

        // 아키텍트님의 catch { throw; } 에 의해 예외가 발생했어야 함
        Assert.IsType<ThreadInterruptedException>(thrownException);

        // 🚀 핵심 검증: 인터럽트 공격을 받은 후에도 큐가 정상 작동하는가?
        Assert.True(channel.TryAdd(100)); // 생산자가 Pulse를 쏠 때 죽지 않아야 함
        Assert.True(channel.WaitToRead(1000));
        Assert.True(channel.TryTake(out var val));
        Assert.Equal(100, val);
    }

    [Fact(DisplayName = "7. 극한의 다중 경합 (MPMC): 수많은 생산자와 소비자가 데이터를 유실 없이 처리해야 한다")]
    public async Task MPMC_ShouldProcessAllItemsWithoutLoss()
    {
        var channel = new SyncUnboundedChannel<int>();
        var producerCount = 10;
        var itemsPerProducer = 10000;
        var totalItems = producerCount * itemsPerProducer;

        var receivedItems = new ConcurrentBag<int>();
        var completedConsumers = 0;

        // 소비자 10명 투입
        var consumers = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            while (channel.WaitToRead())
            {
                if (channel.TryTake(out var item))
                {
                    receivedItems.Add(item);
                }
            }

            Interlocked.Increment(ref completedConsumers);
        })).ToArray();

        // 생산자 10명 투입
        var producers = Enumerable.Range(0, producerCount).Select(pId => Task.Run(() =>
        {
            for (var i = 0; i < itemsPerProducer; i++)
            {
                channel.TryAdd(pId * itemsPerProducer + i);
            }
        })).ToArray();

        // 생산자들이 모두 일을 마칠 때까지 대기
        await Task.WhenAll(producers);

        // 모든 생산자가 끝났으므로 완료 선언 (이 시점에 남아서 자고 있는 소비자들이 전부 깨어남)
        channel.Complete();

        // 소비자들이 모두 스스로 루프를 탈출하여 종료될 때까지 대기
        await Task.WhenAll(consumers);

        // 검증: 단 1개의 데이터도 유실되거나 중복되지 않고 처리되었는가?
        Assert.Equal(totalItems, receivedItems.Count);
        Assert.Equal(10, completedConsumers); // 10명의 소비자 스레드가 무사히 퇴근했는가?
    }
}
