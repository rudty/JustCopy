namespace Test;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;
using JustCopy;

[Collection("ALL")]
public class MpscBlockingQueueTest
{
    [Fact(DisplayName = "1. 싱글 스레드 기본 Add/TryTake 테스트")]
    public void SingleThread_AddAndTryTake_WorksCorrectly()
    {
        // Arrange
        var queue = new MpscBlockingQueue<int>();

        // Act
        queue.Add(10);
        queue.Add(20);

        // Assert
        Assert.True(queue.TryTake(out var item1));
        Assert.Equal(10, item1);

        Assert.True(queue.TryTake(out var item2));
        Assert.Equal(20, item2);

        // 큐가 비었을 때 TryTake는 즉시 false를 반환해야 함 (블로킹 없음)
        Assert.False(queue.TryTake(out var item3));
        Assert.Equal(0, item3);
    }

    [Fact(DisplayName = "2. MPSC 스트레스 테스트 (다중 생산자, 단일 소비자 채널 패턴)")]
    public async Task MultiProducerSingleConsumer_StressTest_NoDataLoss()
    {
        // Arrange
        var queue = new MpscBlockingQueue<int>();
        var producerCount = 10;
        var itemsPerProducer = 100_000;
        var totalItems = producerCount * itemsPerProducer;

        var consumedCount = 0;

        // Act - 생산자들을 먼저 스레드 풀에 올림
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

        // 생산자들이 모두 스레드 풀에 진입할 시간을 줌
        await Task.Delay(100);

        // Act - 소비자(Consumer) 스레드 시작 (표준 Channel 소비 패턴 적용)
        var consumerTask = Task.Run(() =>
        {
            // 💡 큐가 비었을 때만 WaitToRead()로 대기
            while (queue.WaitToRead())
            {
                // 💡 데이터가 있으면 락 프리(Lock-Free)로 최대한 뽑아냄
                while (queue.TryTake(out _))
                {
                    consumedCount++;

                    // 완료 조건 (채널 닫기 기능이 없으므로 카운트로 종료)
                    if (consumedCount == totalItems)
                    {
                        return;
                    }
                }
            }
        });

        // Assert
        try
        {
            var allTasks = new List<Task>(producerTasks) { consumerTask };
            var waitAllTask = Task.WhenAll(allTasks);

            // 15초 내에 모든 생산과 소비가 완료되어야 함
            await waitAllTask.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch
        {
            Assert.Fail($"데드락 발생 또는 처리 지연! 처리된 항목 수: {consumedCount}/{totalItems}");
        }

        Assert.Equal(totalItems, consumedCount);
    }

    [Fact(DisplayName = "3. Wake-Up 테스트 (소비자가 WaitToRead로 잠들었을 때 정확히 깨우는지 확인)")]
    public async Task WaitToRead_BlocksAndWakesUp_WhenItemAdded()
    {
        // Arrange
        var queue = new MpscBlockingQueue<string>();
        var expectedItem = "WakeUp!";

        // Act
        var consumerTask = Task.Run(() =>
        {
            // 1. 데이터가 없으므로 여기서 잠들어야 함
            queue.WaitToRead();

            // 2. 깨어난 후 데이터를 빼냄
            queue.TryTake(out var item);
            return item;
        });

        // 소비자가 확실히 잠들 시간을 줌
        await Task.Delay(500);

        // 생산자가 데이터를 넣음 (이때 소비자를 깨워야 함)
        queue.Add(expectedItem);

        // Assert
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

    [Fact(DisplayName = "4. 타임아웃 테스트 (빈 큐에서 WaitToRead 지정된 시간 후 false 반환)")]
    public void WaitToRead_WithTimeout_ReturnsFalse_WhenEmpty()
    {
        // Arrange
        var queue = new MpscBlockingQueue<int>();
        var timeoutMs = 200;

        // Act
        var watch = Stopwatch.StartNew();

        // 데이터가 안 들어오므로 timeoutMs 이후에 false를 반환해야 함
        var result = queue.WaitToRead(timeoutMs);
        watch.Stop();

        // Assert
        Assert.False(result);

        // 큐는 여전히 비어있어야 함
        Assert.False(queue.TryTake(out var item));
        Assert.Equal(default, item);

        // 타임아웃 시간이 대략적으로 지켜졌는지 확인 (여유폭 50ms)
        Assert.True(watch.ElapsedMilliseconds >= timeoutMs - 50, $"일찍 반환됨: {watch.ElapsedMilliseconds}ms");
    }

    [Fact(DisplayName = "5. 타임아웃 대기 중 데이터 유입 시 즉시 기상 테스트 (기적의 배달)")]
    public async Task WaitToRead_WithTimeout_ReturnsTrue_WhenItemAddedBeforeTimeout()
    {
        // Arrange
        var queue = new MpscBlockingQueue<int>();
        var expectedItem = 99;

        // Act
        var consumerTask = Task.Run(() =>
        {
            // 5초 동안 대기하지만, 중간에 데이터가 들어오면 즉시 true 반환 후 탈출해야 함
            var success = queue.WaitToRead(5000);
            queue.TryTake(out var item);
            return (success, item);
        });

        // 소비자가 대기 상태로 들어갈 시간을 줌
        await Task.Delay(200);

        // 데이터 삽입
        queue.Add(expectedItem);

        // Assert
        try
        {
            // 5초 타임아웃을 걸었지만 1초 안에 처리가 완료되어야 함
            var completedResult = await consumerTask.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.True(completedResult.success);
            Assert.Equal(expectedItem, completedResult.item);
        }
        catch (Exception e)
        {
            Assert.Fail("WaitToRead가 대기 중일 때 데이터를 넣어도 깨어나지 못했습니다.\n" + e);
        }
    }
}