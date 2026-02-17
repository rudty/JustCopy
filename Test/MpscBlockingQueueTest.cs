namespace Test;

using JustCopy;

[Collection("ALL")]
public class MpscBlockingQueueTest
{
    [Fact(DisplayName = "1. 싱글 스레드 기본 Enqueue/Dequeue 테스트")]
    public void SingleThread_AddAndTake_WorksCorrectly()
    {
        // Arrange
        var queue = new MpscBlockingQueue<int>();

        // Act
        queue.Add(10);
        queue.Add(20);

        // Assert
        Assert.True(queue.TryTake(out var item1));
        Assert.Equal(10, item1);

        Assert.Equal(20, queue.Take());

        Assert.False(queue.TryTake(out var item3));
        Assert.Equal(0, item3);
    }

    [Fact(DisplayName = "2. MPSC 스트레스 테스트 (다중 생산자, 단일 소비자 데이터 무결성)")]
    public async Task MultiProducerSingleConsumer_StressTest_NoDataLoss()
    {
        // Arrange
        var queue = new MpscBlockingQueue<int>();
        var producerCount = 10;
        var itemsPerProducer = 100_000; // 🚀 전체 테스트 부하를 위해 50만에서 10만으로 축소 권장
        var totalItems = producerCount * itemsPerProducer;

        var consumedCount = 0;

        // Act - 🚀 생산자들을 먼저 스레드 풀에 올림
        var producerTasks = new Task[producerCount];
        for (var i = 0; i < producerCount; i++)
        {
            producerTasks[i] = Task.Run(() =>
            {
                for (var j = 0; j < itemsPerProducer; j++)
                {
                    queue.Add(1);
                }
            });
        }

        // 🚀 생산자들이 모두 스레드 풀에 진입할 시간을 살짝 줌 (기아 상태 방지)
        await Task.Delay(100);

        // Act - 소비자(Consumer) 스레드 시작
        var consumerTask = Task.Run(() =>
        {
            for (var i = 0; i < totalItems; i++)
            {
                queue.Take();
                consumedCount++; // 주의: SPSC 구조라 소비자가 1명이므로 Interlocked 불필요!
            }
        });

        // Assert
        try
        {
            // 🚀 생산자와 소비자를 한 번에 묶어서 비동기로 기다립니다! (좀비 스레드 방지)
            var allTasks = new List<Task>(producerTasks) { consumerTask };
            var waitAllTask = Task.WhenAll(allTasks);

            await waitAllTask.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch
        {
            Assert.Fail($"데드락 발생 또는 처리 지연! 처리된 항목 수: {consumedCount}/{totalItems}");
        }

        Assert.Equal(totalItems, consumedCount);
    }

    [Fact(DisplayName = "3. Wake-Up 테스트 (소비자가 잠들었을 때 정확히 깨우는지 확인)")]
    public async Task Take_BlocksAndWakesUp_WhenItemAdded()
    {
        // Arrange
        var queue = new MpscBlockingQueue<string>();
        var expectedItem = "WakeUp!";

        // Act
        // 데이터가 없으므로 여기서 Monitor.Wait 상태로 진입해야 함
        var consumerTask = Task.Run(queue.Take);

        // 소비자가 확실히 잠들 시간을 줌
        await Task.Delay(500);

        // 생산자가 데이터를 넣음 (이때 waiters 카운트를 보고 깨워야 함)
        queue.Add(expectedItem);

        // Assert
        // 2초 안에 깨어나서 값을 반환하는지 확인
        try
        {
            var completed = await consumerTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(expectedItem, completed);
        }
        catch 
        {
            Assert.Fail("소비자가 깨어나지 못했습니다! (Pulse 로직 버그 혹은 Deadlock)");
        }
    }

    [Fact(DisplayName = "4. TryTake 타임아웃 테스트 (빈 큐에서 지정된 시간 후 false 반환)")]
    public void TryTake_WithTimeout_ReturnsFalse_WhenEmpty()
    {
        // Arrange
        var queue = new MpscBlockingQueue<int>();
        var timeoutMs = 200;

        // Act
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = queue.TryTake(out var item, timeoutMs);
        watch.Stop();

        // Assert
        Assert.False(result);
        Assert.Equal(default, item);

        // 타임아웃 시간이 대략적으로 지켜졌는지 확인 (여유폭 50ms)
        Assert.True(watch.ElapsedMilliseconds >= timeoutMs - 50, $"일찍 반환됨: {watch.ElapsedMilliseconds}ms");
    }

    [Fact(DisplayName = "5. TryTake 대기 중 데이터 유입 시 즉시 true 반환 테스트")]
    public async Task TryTake_WithTimeout_ReturnsTrue_WhenItemAddedBeforeTimeout()
    {
        // Arrange
        var queue = new MpscBlockingQueue<int>();
        var expectedItem = 99;

        // Act
        var consumerTask = Task.Run(() =>
        {
            // 5초 동안 대기하지만, 중간에 데이터가 들어오면 즉시 반환해야 함
            var success = queue.TryTake(out var item, 5000);
            return (success, item);
        });

        // 소비자가 대기 상태로 들어갈 시간을 줌
        await Task.Delay(200);

        // 데이터 삽입
        queue.Add(expectedItem);

        // Assert
        // 5초 타임아웃을 걸었지만 1초 안에 처리가 완료되어야 함
        try
        {
            var completedResult = await consumerTask.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.True(completedResult.success);
            Assert.Equal(expectedItem, completedResult.item);
        }
        catch (Exception e)
        {
            Assert.Fail("TryTake가 대기 중일 때 데이터를 넣어도 깨어나지 못했습니다." + e);
        }
    }
}

