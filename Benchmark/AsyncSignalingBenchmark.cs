namespace Benchmark;

using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using JustCopy;

// 비동기 성능과 메모리 할당량 측정
[MemoryDiagnoser]
public class AsyncSignalingBenchmark
{
    private SemaphoreSlim _semaphore = null!;
    private AsyncAutoResetEventSlim asyncAutoResetEventSlim = null!;
    private const int Iterations = 1000000;

    [IterationSetup]
    public void Setup()
    {
        // 매 이터레이션마다 객체 상태 초기화
        _semaphore = new SemaphoreSlim(0);
        asyncAutoResetEventSlim = new AsyncAutoResetEventSlim(false);
    }

    // =========================================================
    // 시나리오 1: Fast Path (미리 신호가 와 있는 상태에서 낚아채기)
    // =========================================================

    [Benchmark]
    public async ValueTask SemaphoreSlim_FastPath()
    {
        var w = _semaphore;
        for (var i = 0; i < Iterations; i++)
        {
            w.Release();
            await w.WaitAsync();
        }
    }

    [Benchmark]
    public async ValueTask AsyncAutoResetEventSlim_FastPath()
    {
        var w = asyncAutoResetEventSlim;
        for (var i = 0; i < Iterations; i++)
        {
            w.Set();
            await w.WaitOneAsync(); 
        }
    }

    // =========================================================
    // 시나리오 2: Ping-Pong (비동기 대기 후 외부에서 깨워주기)
    // =========================================================
    [Benchmark]
    public async Task SemaphoreSlim_PingPong()
    {
        for (var i = 0; i < Iterations; i++)
        {
            var task = _semaphore.WaitAsync();
            _semaphore.Release();
            await task;
        }
    }

    [Benchmark]
    public async ValueTask AsyncAutoResetEventSlim_PingPong()
    {
        var w = asyncAutoResetEventSlim;
        for (var i = 0; i < Iterations; i++)
        {
            var valueTask = w.WaitOneAsync();
            w.Set();
            await valueTask;
        }
    }
}
