namespace Test;

using JustCopy;
using System.Diagnostics;

public class BlockingCollectionSlimTest
{
    [Fact(DisplayName = "1. [기본] 단일 스레드 Add, Take, TryTake 및 Count 동작 확인")]
    public void SingleThread_BasicOperations_WorkCorrectly()
    {
        // Arrange
        var queue = new BlockingCollectionSlim<int>();

        // Act & Assert
        Assert.Equal(0, queue.Count);

        queue.Add(10);
        queue.Add(20);
        Assert.Equal(2, queue.Count);

        Assert.True(queue.TryTake(out var item1));
        Assert.Equal(10, item1);

        Assert.Equal(20, queue.Take());
        Assert.Equal(0, queue.Count);

        Assert.False(queue.TryTake(out var item3));
        Assert.Equal(0, item3);
    }

    [Fact(DisplayName = "2. [스트레스] 다중 생산자 - 다중 소비자 (MPMC) 데이터 무결성 검증")]
    public async Task MPMC_StressTest_NoDataLoss_And_NoDeadlock()
    {
        // Arrange
        var queue = new BlockingCollectionSlim<int>();
        var workerCount = 8;        // 8쌍의 생산자/소비자
        var itemsPerWorker = 10_000; // 각 1만 개씩 (총 8만 개)
        var totalItems = workerCount * itemsPerWorker;

        var consumedCount = 0;

        // Act
        var consumers = new Task[workerCount];
        var producers = new Task[workerCount];

        for (var i = 0; i < workerCount; i++)
        {
            // 소비자 스레드 생성
            consumers[i] = Task.Run(() =>
            {
                for (var j = 0; j < itemsPerWorker; j++)
                {
                    queue.Take();
                    Interlocked.Increment(ref consumedCount);
                }
            });

            // 생산자 스레드 생성
            producers[i] = Task.Run(() =>
            {
                for (var j = 0; j < itemsPerWorker; j++)
                {
                    queue.Add(1);
                }
            });
        }

        // 생산 완료 대기
        await Task.WhenAll(producers);

        // Assert: 소비자가 데드락에 빠지지 않고 모두 처리하는지 타임아웃(5초)을 걸어 확인
        try
        {
            var completedTask = Task.WhenAll(consumers);
            await completedTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            Assert.Fail($"데드락 발생! 총 {totalItems}개 중 {consumedCount}개만 처리됨.");
        }

        Assert.Equal(totalItems, consumedCount);
        Assert.Equal(0, queue.Count);
    }

    [Fact(DisplayName = "3. [웨이크업] 대기 중인 소비자가 정확히 깨어나는지 검증 (Lost Pulse 방지)")]
    public async Task Take_BlocksAndWakesUp_WhenItemAdded()
    {
        // Arrange
        var queue = new BlockingCollectionSlim<string>();
        const string expectedValue = "Hello, Slim!";

        // Act
            // 데이터가 없으므로 여기서 Monitor.Wait 으로 잠들어야 함
        var consumerTask = Task.Run(queue.Take);

        // 소비자가 확실히 잠들도록 약간의 시간을 줌
        Thread.Sleep(200);

        // 이 시점에서는 태스크가 아직 완료되지 않아야 함
        Assert.False(consumerTask.IsCompleted, "소비자가 잠들지 않고 미리 종료되었습니다!");

        // 생산자가 데이터를 넣음 (waitingConsumers > 0 이므로 Pulse 발생)
        queue.Add(expectedValue);

        // Assert: 2초 안에 깨어나서 정확한 값을 가져오는지 확인
        try
        {
            var completed = await consumerTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(expectedValue, completed);
        }
        catch (Exception e)
        {
            Assert.Fail("소비자가 깨어나지 못했습니다! (Monitor.Pulse 누락 의심)" + e);

        }
    }

    [Fact(DisplayName = "4. [타임아웃] TryTake(timeout)이 빈 큐에서 지정된 시간 후 false를 반환하는지 검증")]
    public void TryTake_WithTimeout_ReturnsFalse_WhenTimeoutExpires()
    {
        // Arrange
        var queue = new BlockingCollectionSlim<int>();
        var timeoutMs = 300; // 300ms 대기

        // Act
        var sw = Stopwatch.StartNew();
        var result = queue.TryTake(out var item, timeoutMs);
        sw.Stop();

        // Assert
        Assert.False(result);
        Assert.Equal(default, item);

        // OS 타이머 해상도를 고려해 -50ms 정도의 오차 허용
        Assert.True(sw.ElapsedMilliseconds >= timeoutMs - 50, $"너무 일찍 반환되었습니다. (소요 시간: {sw.ElapsedMilliseconds}ms)");
    }

    [Fact(DisplayName = "5. [타임아웃] TryTake 대기 중 데이터가 들어오면 즉시 true를 반환하는지 검증")]
    public async Task TryTake_WithTimeout_ReturnsTrue_IfItemArrivesInTime()
    {
        // Arrange
        var queue = new BlockingCollectionSlim<int>();
        var expectedValue = 999;

        // Act
        var consumerTask = Task.Run(() =>
        {
            // 5초 동안 넉넉하게 기다림
            var success = queue.TryTake(out var item, 5000);
            return (success, item);
        });

        // 대기 상태 진입 유도
        Thread.Sleep(200);

        // 데이터 삽입
        queue.Add(expectedValue);

        // Assert: 5초 대기였지만 데이터가 들어왔으므로 즉시(1초 이내) 완료되어야 함
        try
        {
            var completed = await consumerTask.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(completed.success);
            Assert.Equal(expectedValue, completed.item);
        } catch (Exception e)
        {
            Assert.Fail("데이터가 들어왔음에도 TryTake가 깨어나지 않았습니다." + e);
        }
    }
}
