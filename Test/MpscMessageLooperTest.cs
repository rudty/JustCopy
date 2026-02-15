namespace Test;

using JustCopy;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

public class MpscMessageLooperTest
{
    // 테스트용으로 구현한 Concrete Looper 클래스
    public class TestMessageLooper : MpscMessageLooper<int>
    {
        public int ProcessedCount => processedCount;
        private int processedCount;

        public int ExceptionCount => exceptionCount;
        private int exceptionCount;

        // 처리가 완료된 아이템들을 순서대로 담아둘 컬렉션 (검증용)
        public ConcurrentQueue<int> ProcessedItems { get; } = new ();

        // 특정 아이템에서 고의로 예외를 발생시키기 위한 플래그
        public int? PoisonPillItem { get; set; }

        public override void OnItemReceived(int item)
        {
            if (item == PoisonPillItem)
            {
                throw new InvalidOperationException($"Poison pill triggered for item: {item}");
            }

            ProcessedItems.Enqueue(item);
            Interlocked.Increment(ref processedCount);
        }

        public override void OnException(int item, Exception exception)
        {
            Interlocked.Increment(ref exceptionCount);
            // 기본 동작(무시) 외에 테스트를 위해 카운트만 증가시킵니다.
            base.OnException(item, exception);
        }
    }

    public class MpscMessageLooperTests
    {
        [Fact(DisplayName = "1. [기본] Add()로 넣은 데이터가 OnItemReceived()에서 정확히 처리되는지 확인")]
        public async Task Start_ProcessesItems_Correctly()
        {
            // Arrange
            var looper = new TestMessageLooper();
            using var cts = new CancellationTokenSource();

            // Act
            // 루프를 백그라운드 태스크로 시작
            var loopTask = looper.Start(cts.Token, schedulingContext: false);

            looper.Add(10);
            looper.Add(20);
            looper.Add(30);

            // 데이터가 처리될 수 있도록 아주 잠깐 대기
            await Task.Delay(100);

            // 루프 종료 요청
            cts.Cancel();
            await loopTask;

            // Assert
            Assert.Equal(3, looper.ProcessedCount);
            Assert.Equal(new[] { 10, 20, 30 }, looper.ProcessedItems.ToArray());
        }

        [Fact(DisplayName = "2. [스트레스/MPMC] 다중 생산자가 동시에 엄청난 양의 데이터를 넣어도 유실이 없는지 검증")]
        public async Task MultiProducer_StressTest_NoDataLoss()
        {
            // Arrange
            var looper = new TestMessageLooper();
            using var cts = new CancellationTokenSource();

            var producerCount = 8;
            var itemsPerProducer = 50_000;
            var totalExpectedItems = producerCount * itemsPerProducer;

            // Act
            var loopTask = looper.Start(cts.Token, schedulingContext: false);

            var producers = new Task[producerCount];
            for (var i = 0; i < producerCount; i++)
            {
                producers[i] = Task.Run(() =>
                {
                    for (var j = 0; j < itemsPerProducer; j++)
                    {
                        looper.Add(1); // 값은 중요하지 않고 개수가 중요함
                    }
                });
            }

            // 모든 생산자가 데이터를 다 넣을 때까지 대기
            await Task.WhenAll(producers);

            // 데이터가 큐에서 다 빠져나가 OnItemReceived로 전달될 때까지 약간 대기
            // (실제 환경에서는 별도의 TaskCompletionSource로 동기화하지만 테스트이므로 폴링 방식 사용)
            while (looper.ProcessedCount < totalExpectedItems)
            {
                await Task.Delay(10);
            }

            cts.Cancel();
            await loopTask;

            // Assert
            Assert.Equal(totalExpectedItems, looper.ProcessedCount);
        }

        [Fact(DisplayName = "3. [종료 보장] Cancellation 요청 후에도 큐에 남은 데이터를 끝까지 털어내는지(Drain) 검증")]
        public async Task Start_DrainsRemainingItems_WhenCancelled()
        {
            // Arrange
            var looper = new TestMessageLooper();
            using var cts = new CancellationTokenSource();

            // Act
            // 큐에 미리 데이터를 잔뜩 넣어둠 (루프는 아직 시작 안 함)
            for (var i = 0; i < 1000; i++)
            {
                looper.Add(i);
            }

            // 취소를 먼저 예약함! (루프 시작 즉시 Cancellation이 감지되도록)
            cts.Cancel();

            // 루프 시작 (시작하자마자 취소 상태이므로 while 루프를 빠져나와 5번 Drain 단계로 직행해야 함)
            await looper.Start(cts.Token, schedulingContext: false);

            // Assert
            // 취소가 먼저 되었음에도 불구하고 큐에 있던 1000개의 데이터는 무조건 처리되어야 함
            Assert.Equal(1000, looper.ProcessedCount);
        }

        [Fact(DisplayName = "4. [예외 격리] 사용자 로직에서 예외가 터져도 루프가 죽지 않고 다음 항목을 처리하는지 검증")]
        public async Task Start_Survives_ExceptionsInItemHandler()
        {
            // Arrange
            var looper = new TestMessageLooper
            {
                PoisonPillItem = 999 // 999번 아이템이 들어오면 고의로 Exception 발생
            };
            using var cts = new CancellationTokenSource();

            // Act
            var loopTask = looper.Start(cts.Token, schedulingContext: false);

            looper.Add(1);
            looper.Add(999); // 폭탄 투하!
            looper.Add(2);
            looper.Add(3);

            await Task.Delay(100); // 처리 대기
            cts.Cancel();
            await loopTask;

            // Assert
            // 폭탄(999) 처리 중 발생한 예외가 OnException으로 1번 전달되어야 함
            Assert.Equal(1, looper.ExceptionCount);

            // 폭탄 때문에 잠시 멈췄더라도, 다음 아이템(2, 3)은 정상적으로 처리되어 루프가 살아있음을 증명해야 함
            Assert.Equal(3, looper.ProcessedCount);
            Assert.Equal(new[] { 1, 2, 3 }, looper.ProcessedItems.ToArray());
        }

        [Fact(DisplayName = "5. [방어] null 값을 Add() 시도할 때 ArgumentNullException을 던지는지 확인")]
        public void Add_ThrowsArgumentNullException_WhenItemIsNull()
        {
            // Arrange
            // 제네릭 T를 string(참조형)으로 설정하여 null 테스트
            var looper = new StringTestLooper();

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => looper.Add(null!));
        }

        // null 테스트를 위한 임시 클래스
        private class StringTestLooper : MpscMessageLooper<string>
        {
            public override void OnItemReceived(string item)
            {
            }
        }
    }
}
