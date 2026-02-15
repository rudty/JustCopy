namespace Test;

using JustCopy;
using System.Diagnostics;

public class AutoResetEventSlimTest
{
    [Fact(DisplayName = "1. [기본] Set() 호출 시 WaitOne()이 한 번만 통과하고 닫히는지 확인 (Auto-Reset 특성)")]
    public void Set_AllowsOnlyOneThreadToPass_And_ResetsAutomatically()
    {
        // Arrange
        var autoEvent = new AutoResetEventSlim();

        // Act & Assert
        Assert.False(autoEvent.WaitOne(0), "초기 상태는 닫혀 있어야 합니다.");

        autoEvent.Set();

        // 첫 번째 대기는 즉시 성공해야 함 (Fast-Path)
        Assert.True(autoEvent.WaitOne(0), "Set() 이후 첫 번째 WaitOne()은 통과해야 합니다.");

        // 두 번째 대기는 실패해야 함 (자동으로 닫혔는지 검증)
        Assert.False(autoEvent.WaitOne(0), "첫 번째 통과 후 자동으로 닫혀야 합니다(Auto-Reset).");
    }

    [Fact(DisplayName = "2. [기본] Set()을 여러 번 연속 호출해도 신호는 1번만 누적되는지 확인 (Lost Signal 특성)")]
    public void MultipleSets_DoNotStack()
    {
        // Arrange
        var autoEvent = new AutoResetEventSlim();

        // Act
        autoEvent.Set();
        autoEvent.Set();
        autoEvent.Set(); // 3번을 연속으로 호출 (대기자 없음)

        // Assert
        Assert.True(autoEvent.WaitOne(0), "첫 번째 통과는 성공해야 합니다.");
        Assert.False(autoEvent.WaitOne(0), "신호가 누적되지 않으므로 두 번째 통과는 실패해야 합니다.");
    }

    [Fact(DisplayName = "3. [웨이크업] 잠들어 있는 스레드를 Set()으로 정확히 깨우는지 확인")]
    public async Task WaitOne_Blocks_And_WakesUp_OnSet()
    {
        // Arrange
        var autoEvent = new AutoResetEventSlim();

        // Act
        var waitTask = Task.Run(() =>
        {
            // 타임아웃을 넉넉히 주고 대기 (Monitor.Wait 진입)
            return autoEvent.WaitOne(5000);
        });

        // 스레드가 확실히 잠들도록 시간을 줌
        Thread.Sleep(200);
        Assert.False(waitTask.IsCompleted, "Set()을 호출하기 전에는 깨어나면 안 됩니다.");

        // 메인 스레드에서 깨움 (Pulse 발생)
        autoEvent.Set();

        // Assert: 즉시 깨어나서 true를 반환해야 함
        try
        {
            var completed = await waitTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(completed, "정상적으로 신호를 받고 true를 반환해야 합니다.");
        }
        catch
        {
            Assert.Fail("스레드가 깨어나지 못했습니다! (Lost Pulse 의심)");
        }
    }

    [Fact(DisplayName = "4. [타임아웃] 아무도 Set()을 해주지 않으면 지정된 시간 후 false를 반환하는지 확인")]
    public void WaitOne_WithTimeout_ReturnsFalse_WhenExpired()
    {
        // Arrange
        var autoEvent = new AutoResetEventSlim();
        var timeoutMs = 200;

        // Act
        var sw = Stopwatch.StartNew();
        var result = autoEvent.WaitOne(timeoutMs);
        sw.Stop();

        // Assert
        Assert.False(result, "신호가 없었으므로 false를 반환해야 합니다.");

        // OS 타이머 해상도를 고려한 검증 (-50ms 오차 허용)
        Assert.True(sw.ElapsedMilliseconds >= timeoutMs - 50, $"너무 일찍 반환되었습니다: {sw.ElapsedMilliseconds}ms");
    }

    [Fact(DisplayName = "5. [스트레스] 다중 스레드 락(Lock) 경합 환경에서 데드락 및 상호 배제(Mutex) 완벽 검증")]
    public async Task MultiThread_Contention_ActsAsPerfectMutex()
    {
        // Arrange
        var autoEvent = new AutoResetEventSlim();
        autoEvent.Set(); // 토큰을 1개 던져놓음 (문을 한 번 염)

        var workerCount = 4;
        var iterations = 10_000;
        var sharedCounter = 0;

        // Act
        var tasks = new Task[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                for (var j = 0; j < iterations; j++)
                {
                    autoEvent.WaitOne(); // 토큰 획득 (Lock)

                    // --- 임계 구역 (Critical Section) ---
                    // 만약 Auto-Reset이 고장나서 2명이 동시에 들어오면 이 카운터는 무조건 꼬입니다.
                    var current = sharedCounter;
                    current++;
                    sharedCounter = current;
                    // ----------------------------------

                    autoEvent.Set(); // 토큰 반납 (Unlock)
                }
            });
        }

        // Assert
        // 10초 내에 모든 스레드가 10,000번씩 작업을 끝마쳐야 함 (데드락 방지)
        var allTask = Task.WhenAll(tasks);
        try
        {
            await allTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch
        {
            Assert.Fail("데드락이 발생했습니다!");
        }

        Assert.Equal(workerCount * iterations, sharedCounter); // 데이터 무결성 검증
    }
}