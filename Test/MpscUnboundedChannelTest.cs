namespace Test;
using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using JustCopy;

[Collection("ALL")]
public class MpscUnboundedChannelTests
{
    [Fact(DisplayName = "1. 기본 입출력: 데이터가 순서대로 들어가고 잘 나오는지 검증")]
    public void Basic_ReadWrite_Works()
    {
        // Arrange
        var channel = new MpscUnboundedChannel<int>(32);

        // Act & Assert
        Assert.True(channel.TryWrite(100));
        Assert.True(channel.TryWrite(200));

        Assert.True(channel.TryRead(out var item1));
        Assert.Equal(100, item1);

        Assert.True(channel.TryRead(out var item2));
        Assert.Equal(200, item2);

        // 비어있을 때는 false 반환
        Assert.False(channel.TryRead(out _));
    }

    [Fact(DisplayName = "2. 세그먼트 경계 돌파(Unbounded 특성): 큐 사이즈를 초과했을 때 새 배열이 동적으로 잘 붙고 읽히는지 검증")]
    public void CrossSegment_Boundary_WorksFlawlessly()
    {
        // Arrange: 세그먼트 사이즈를 아주 작은 '4'로 설정하여 경계 돌파를 강제 유발
        var segmentSize = 4;
        var channel = new MpscUnboundedChannel<int>(segmentSize);
        var totalItems = 10; // 4칸짜리 배열을 3개(4+4+2) 생성하게 만듦

        // Act (Write)
        for (var i = 0; i < totalItems; i++)
        {
            channel.TryWrite(i);
        }

        // Assert (Read)
        for (var i = 0; i < totalItems; i++)
        {
            Assert.True(channel.TryRead(out var item), $"인덱스 {i}에서 읽기 실패 (세그먼트 연결 불량 의심)");
            Assert.Equal(i, item);
        }

        // 10개를 다 읽었으면 큐는 비어있어야 함
        Assert.False(channel.TryRead(out _));
    }

    [Fact(DisplayName = "3. 비동기 대기 및 세그먼트 경계 깨우기: 데이터가 없을 때 잠들고, 경계를 넘어서 데이터가 들어와도 정확히 깨어나는지 검증")]
    public async Task WaitToReadAsync_AcrossSegments_WakesUpCorrectly()
    {
        // Arrange: 사이즈 2짜리 초미니 큐 생성
        var channel = new MpscUnboundedChannel<string>(2);

        // 1. 큐를 꽉 채우고 비웁니다. (소비자의 인덱스를 segment 끝단으로 몰아넣기 위함)
        channel.TryWrite("A");
        channel.TryWrite("B");
        channel.TryRead(out _);
        channel.TryRead(out _);

        // 2. 현재 소비자는 첫 번째 세그먼트의 끝(index 2)에 도달하여 다음 세그먼트를 바라보고 있습니다.
        // 데이터가 없으므로 여기서 잠듭니다.
        var readTask = channel.WaitToReadAsync().AsTask();
        Assert.False(readTask.IsCompleted, "데이터가 없는데 Task가 완료되었습니다 (상태 머신 꼬임 의심)");

        // Act
        // 3. 다른 스레드(생산자)가 데이터를 넣어 소비자를 깨웁니다. 
        // 이때 생산자는 새로운 세그먼트를 동적으로 생성하고 데이터를 넣습니다.
        await Task.Run(() => channel.TryWrite("C_From_New_Segment"));

        // Assert
        // 4. 세그먼트가 분리되어 있어도 소비자가 무사히 깨어나야 합니다.
        var canRead = await readTask;
        Assert.True(canRead);

        // 5. 정확한 데이터를 빼내는지 확인
        Assert.True(channel.TryRead(out var item));
        Assert.Equal("C_From_New_Segment", item);
    }

    [Fact(DisplayName = "4. 동시성 스트레스(MPSC): 수천 명의 생산자가 락 없이 세그먼트를 무한히 증식시키며 쏠 때 유실/오염/데드락이 없는지 검증")]
    public async Task Concurrency_MultipleProducers_SingleConsumer_NoDataLoss()
    {
        // Arrange
        var channel = new MpscUnboundedChannel<long>(1024); // 동적 증식을 위해 사이즈를 1024로 설정
        var producerCount = Environment.ProcessorCount * 2;
        var messagesPerProducer = 100_000L;

        var expectedTotalSum = 0L;
        var sumPerProducer = messagesPerProducer * (messagesPerProducer + 1) / 2;
        expectedTotalSum = sumPerProducer * producerCount;

        var actualTotalSum = 0L;
        var totalMessagesReceived = 0L;
        var expectedTotalMessages = producerCount * messagesPerProducer;

        // 소비자 Task (단일 소비자)
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

        // Act: 다중 생산자 Task 폭격 시작 (동시에 큐에 밀어넣으며 배열을 미친듯이 증식시킴)
        var producerTasks = Enumerable.Range(0, producerCount).Select(_ => Task.Run(() =>
        {
            for (long i = 1; i <= messagesPerProducer; i++)
            {
                channel.TryWrite(i);
            }
        })).ToArray();

        // Assert: 모든 작업이 무사히 끝날 때까지 대기
        // (데드락이나 무한루프가 있다면 여기서 테스트가 멈춥니다)
        await Task.WhenAll(producerTasks);
        await consumerTask;

        // 락프리로 수십MB의 세그먼트가 생성/연결되었음에도 단 1건의 유실이나 덮어쓰기가 없어야 함!
        Assert.Equal(expectedTotalMessages, totalMessagesReceived);
        Assert.Equal(expectedTotalSum, actualTotalSum);
    }
}
