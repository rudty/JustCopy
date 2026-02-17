#pragma warning disable IDE0007
#pragma warning disable IDE2003
#pragma warning disable IDE0090
#pragma warning disable IDE0161
#pragma warning disable IDE0130
#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1
#nullable disable
#endif

namespace JustCopy
{
    using System;
    using System.Collections.Concurrent;
    using System.Diagnostics;
    using System.Runtime.CompilerServices;
    using System.Threading;

    /// <summary>
    /// <see cref="ConcurrentQueue{T}"/> 기반의 고성능 컬렉션입니다. <see cref="System.Collections.Concurrent.BlockingCollection{T}"/> 의 대안입니다.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><see cref="System.Collections.Concurrent.BlockingCollection{T}"/>의 글로벌 잠금(Lock) 병목으로 인해 성능 저하가 발생할 때 대체하여 사용합니다.</description></item>
    /// <item><description><b>작업 스레드가 1 ~ 2개 (저경합):</b> BlockingCollectionSlim 이 컨텍스트 스위칭 오버헤드가 적어 가장 높은 효율을 보여줍니다.</description></item>
    /// <item><description><b>작업 스레드가 3 ~ 4개 (중간 경합):</b> System.Threading.Channels.Channel 비슷한 속도를 내기 시작하며, 메모리 할당량(GC) 측면에서는 MpmcLockBlockingQueue 가 압도적으로 우수합니다.</description></item>
    /// <item><description><b>작업 스레드가 4개 이상 (고경합):</b> MpmcLockBlockingQueue 가 최고의 처리량을 제공합니다.</description></item>
    /// <item><description>적용 후 실제 비즈니스 로직과 트래픽 환경에서 더 나은 성능으로 작동하는지 벤치마크 테스트를 수행하는 것을 권장합니다.</description></item>
    /// </list>
    /// </remarks>
    public sealed class BlockingCollectionSlim<T>
    {
        private readonly ConcurrentQueue<T> queue = new ConcurrentQueue<T>();
        private readonly object takeLock = new object();
        private int waitingConsumers;

        // 초기 Take 시 (SpinLock) 횟수. 경합이 낮을 때 컨텍스트 스위칭을 피합니다.
        public int SpinCount { get; set; } = 10;

        public int Count => queue.Count;

        /// <summary>
        /// 항목을 큐에 추가하고, 대기 중인 단일 소비자에게 신호를 보냅니다.
        /// </summary>
        public void Add(T item)
        {
            queue.Enqueue(item);
            // --- 싱글 스레드 성능 개선 로직 ---
            // 대기 중인 소비자(waitingConsumers > 0)가 있을 때만 lock을 잡고 Monitor.Pulse를 호출합니다.
            // 대기 중인 스레드가 없으면 lock/Pulse 오버헤드를 완전히 피할 수 있습니다.
            if (Volatile.Read(ref waitingConsumers) > 0)
            {
                lock (takeLock)
                {
                    Monitor.Pulse(takeLock);
                }
            }
            // ------------------------------------
        }

        /// <summary>
        /// 큐에서 항목을 즉시 가져오려고 시도합니다.
        /// </summary>
        public bool TryTake(
#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1
            [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] 
#endif
            out T item)
        {
            return queue.TryDequeue(out item);
        }

        /// <summary>
        /// 큐가 비어 있으면 항목이 사용 가능해질 때까지 무기한으로 블로킹합니다.
        /// </summary>
        public T Take()
        {
            // 1. 비잠금 경로: 큐에 항목이 있다면 바로 반환
            if (TryTake(out var item))
            {
                return item;
            }

            // Spin
            var spinCount = SpinCount;
            SpinWait spin = default;
            while (spin.Count < spinCount)
            {
#if NETCOREAPP3_1_OR_GREATER
                spin.SpinOnce(sleep1Threshold: -1);
＃else
                spin.SpinOnce();
#endif
                if (TryTake(out item))
                {
                    return item;
                }
            }

            // 2. 잠금 및 대기 경로: 항목이 없으면 lock을 잡고 대기
            lock (takeLock)
            {
                // Monitor.Wait을 호출하기 전에 대기 중인 소비자 수를 증가시킵니다.
                Volatile.Write(ref waitingConsumers, waitingConsumers + 1);

                try
                {
                    while (true)
                    {
                        // Monitor.Wait 전에 항목을 다시 확인 (Lost Pulse 방지)
                        if (TryTake(out item))
                        {
                            return item;
                        }

                        // 대기: 신호가 들어올 때까지 잠듦 (무기한)
                        Monitor.Wait(takeLock);
                    }
                }
                finally
                {
                    // Monitor.Wait이 끝난 후 (항목을 가져갔든, 예외로 종료되었든) 소비자 수를 감소시킵니다.
                    Volatile.Write(ref waitingConsumers, waitingConsumers - 1);
                }
            }
        }

        /// <summary>
        /// 큐가 비어 있으면 지정된 타임아웃 시간 동안 항목을 가져오기 위해 블로킹합니다.
        /// </summary>
        public bool TryTake(
#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1
            [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)]
#endif
            out T item, int millisecondsTimeout)
        {
            if (TryTake(out item))
            {
                return true;
            }

            if (millisecondsTimeout < -1)
            {
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));
            }

            if (millisecondsTimeout == 0)
            {
                return false;
            }

            var startTimeStamp = 0L;
            var useTimeout = false;
            var remainTimeout = millisecondsTimeout; // this will be adjusted if necessary.

            if (millisecondsTimeout != Timeout.Infinite)
            {
                startTimeStamp = Stopwatch.GetTimestamp();
                useTimeout = true;
            }

            // Spin
            var spinCount = SpinCount;
            SpinWait spin = default;
            while (spin.Count < spinCount)
            {
#if NETCOREAPP3_1_OR_GREATER
                spin.SpinOnce(sleep1Threshold: -1);
#else
                spin.SpinOnce();
#endif
                if (TryTake(out item))
                {
                    return true;
                }
            }

            lock (takeLock)
            {
                // Monitor.Wait을 호출하기 전에 대기 중인 소비자 수를 증가시킵니다.
                Volatile.Write(ref waitingConsumers, waitingConsumers + 1);

                try
                {
                    while (true)
                    {
                        // 3-1. 타임아웃 시간 갱신
                        if (useTimeout)
                        {
                            remainTimeout = BlockingCollectionSlim_Companion.UpdateTimeOut(startTimeStamp, millisecondsTimeout);
                            if (remainTimeout <= 0)
                            {
                                return false; // 타임아웃 만료
                            }
                        }

                        // 3-2. Monitor.Wait 전에 항목을 다시 확인
                        if (TryTake(out item))
                        {
                            return true;
                        }

                        // 3-3. 대기
                        if (!Monitor.Wait(takeLock, remainTimeout))
                        {
                            return false; // 타임아웃 발생 (Monitor.Wait이 false 반환)
                        }

                        // 신호가 들어왔으면 루프를 반복하여 TryTake 재시도
                    }
                }
                finally
                {
                    // Monitor.Wait이 끝난 후 소비자 수를 감소시킵니다.
                    Volatile.Write(ref waitingConsumers, waitingConsumers - 1);
                }
            }
        }
    }

    static class BlockingCollectionSlim_Companion
    {
        private static readonly double tickFrequency = (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int UpdateTimeOut(long startTimeStamp, int originalWaitMillisecondsTimeout)
        {
            Debug.Assert(originalWaitMillisecondsTimeout != Timeout.Infinite);

            var elapsedMilliseconds = GetElapsedMilliseconds(startTimeStamp);

            if (elapsedMilliseconds > int.MaxValue)
            {
                return 0;
            }

            var waitTimeout = originalWaitMillisecondsTimeout - (int)elapsedMilliseconds;
            if (waitTimeout <= 0)
            {
                return 0;
            }

            return waitTimeout;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long GetElapsedMilliseconds(long startTimeStamp)
        {
            var currentTimeStamp = Stopwatch.GetTimestamp();
            var elapsedTimeStamp = currentTimeStamp - startTimeStamp;
            return unchecked((long)(elapsedTimeStamp * tickFrequency)) / TimeSpan.TicksPerMillisecond;
        }
    }
}
