namespace Test;
using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using JustCopy;

[Collection("ALL")]
public class MpscBoundedChannelTests
{
    [Fact(DisplayName = "1. 기본 입출력: 데이터가 순서대로 잘 들어가고 나오는지 검증")]
    public void Basic_ReadWrite_Works()
    {
        // Arrange
        var channel = new MpscBoundedChannel<int>(32);

        // Act & Assert
        Assert.True(channel.TryWrite(10));
        Assert.True(channel.TryWrite(20));

        Assert.True(channel.TryRead(out var item1));
        Assert.Equal(10, item1);

        Assert.True(channel.TryRead(out var item2));
        Assert.Equal(20, item2);

        // 비어있을 때는 false 반환
        Assert.False(channel.TryRead(out _));
    }

    [Fact(DisplayName = "2. 비동기 대기(WaitToReadAsync): 데이터가 없을 때 잠들고, 쓰면 깨어나는지 검증")]
    public async Task WaitToReadAsync_YieldsAndResumes()
    {
        // Arrange
        var channel = new MpscBoundedChannel<string>(32);

        // 1. 데이터가 없으므로 일단 큐에 읽기 대기를 걸어둡니다.
        var readTask = channel.WaitToReadAsync().AsTask();

        // 현재 readTask는 완료되지 않은 상태여야 합니다. (소비자 수면 상태)
        Assert.False(readTask.IsCompleted);

        // Act
        // 🚨 수정됨: Task.Run().Wait()을 지우고 깔끔하게 await Task.Run으로 비동기 호출!
        await Task.Run(() => channel.TryWrite("Hello, Architect!"));

        // Assert
        // 3. 소비자가 깨어나서 true를 반환해야 합니다.
        var canRead = await readTask;
        Assert.True(canRead);

        // 4. 실제로 데이터를 빼냅니다.
        Assert.True(channel.TryRead(out var item));
        Assert.Equal("Hello, Architect!", item);
    }

    [Fact(DisplayName = "3. 백프레셔(Backpressure) 방어: 큐가 꽉 찼을 때 생산자가 SpinWait으로 대기하는지 검증")]
    public async Task Backpressure_SpinsWhenFull_And_ResumesWhenConsumed()
    {
        // Arrange: 가장 작은 크기인 32로 생성
        var channel = new MpscBoundedChannel<int>(32);

        // 큐를 한 바퀴 꽉 채웁니다. (크기가 32이므로 32개 쓰기)
        for (var i = 0; i < 32; i++)
        {
            channel.TryWrite(i);
        }

        // Act
        // 33번째 쓰기 시도 -> 큐가 꽉 찼으므로 SpinWait에 걸려 멈춰야 함!
        var overWriteTask = Task.Run(() => channel.TryWrite(999));

        // 약간의 시간을 주어도 overWriteTask가 완료되지 않아야 함 (스핀 중)
        await Task.Delay(100);
        Assert.False(overWriteTask.IsCompleted, "큐가 꽉 찼는데 생산자가 대기하지 않고 뚫고 지나갔습니다!");

        // Assert: 소비자가 1개를 빼주면 생산자가 다시 돌아가야 함
        channel.TryRead(out _); // 1개 여유 공간 확보

        // 여유 공간이 생겼으므로 생산자의 Task가 정상적으로 완료되어야 함
        await overWriteTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(overWriteTask.IsCompletedSuccessfully);

        // 마지막 33번째 데이터가 제대로 들어갔는지 확인 (꼬리에 999가 있어야 함)
        var lastItem = 0;
        while (channel.TryRead(out var item))
        {
            lastItem = item;
        }

        Assert.Equal(999, lastItem);
    }

    [Fact(DisplayName = "4. 동시성 스트레스(MPSC): 수천 명의 생산자가 락 없이 데이터를 쏠 때 유실/오염이 없는지 검증")]
    public async Task Concurrency_MultipleProducers_SingleConsumer_NoDataLoss()
    {
        // Arrange
        var channel = new MpscBoundedChannel<long>(131072); // 1MB 넉넉한 큐
        var producerCount = Environment.ProcessorCount * 2; // 코어 수의 2배만큼 생산자 생성
        const long messagesPerProducer = 100_000;

        // 1부터 100,000까지의 합 * 생산자 수
        const long sumPerProducer = messagesPerProducer * (messagesPerProducer + 1) / 2;
        var expectedTotalSum = sumPerProducer * producerCount;

        var actualTotalSum = 0L;
        var totalMessagesReceived = 0L;
        var expectedTotalMessages = producerCount * messagesPerProducer;

        // 🚀 수정됨: 생산자 Tasks를 먼저 스레드 풀에 띄움 (소비자 기아 상태 방지)
        var producerTasks = Enumerable.Range(0, producerCount).Select(_ => Task.Run(() =>
        {
            for (long i = 1; i <= messagesPerProducer; i++)
            {
                channel.TryWrite(i);
            }
        })).ToArray();

        // 🚀 수정됨: 생산자들이 스레드 풀에 안착할 시간을 살짝 줌
        await Task.Delay(100);

        // 소비자 Task (단일 소비자) - 생산자 뒤에 띄움!
        var consumerTask = Task.Run(async () =>
        {
            while (totalMessagesReceived < expectedTotalMessages)
            {
                if (await channel.WaitToReadAsync())
                {
                    while (channel.TryRead(out var item))
                    {
                        actualTotalSum += item;
                        totalMessagesReceived++;
                    }
                }
            }
        });

        // Assert: 모든 생산자와 소비자가 무사히 끝날 때까지 대기
        try
        {
            // 🚀 수정됨: 생산자와 소비자를 Task.WhenAll로 묶어서 동시에 비동기 대기!
            var allTasks = producerTasks.Append(consumerTask);
            await Task.WhenAll(allTasks).WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch
        {
            Assert.Fail($"데드락 발생 또는 처리 지연! 처리된 항목 수: {totalMessagesReceived}/{expectedTotalMessages}");
        }

        // 단 1건의 유실도, 덮어쓰기(오염)도 없어야 합이 정확히 일치함!
        Assert.Equal(expectedTotalMessages, totalMessagesReceived);
        Assert.Equal(expectedTotalSum, actualTotalSum);
    }
}