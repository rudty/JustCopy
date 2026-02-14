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
    /// BlockingCollection의 잠금 경합을 피하기 위해 ConcurrentQueue와 Monitor를 사용하는 고성능 블로킹 컬렉션 대안입니다.
    /// BlockingCollection 의 잠금으로 성능 문제가 있을때 대신해서 사용합니다
    /// 적용 후 자신의 환경에서 더 나은 성능으로 작동하는지 테스트가 필요합니다
    /// </summary>
    public sealed class BlockingCollectionSlim<T>
    {
        private readonly ConcurrentQueue<T> queue = new ConcurrentQueue<T>();
        private readonly object takeLock = new object();
        private volatile int waitingConsumers;

        // 초기 스피닝(SpinLock) 횟수. 경합이 낮을 때 컨텍스트 스위칭을 피합니다.
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
            if (waitingConsumers > 0)
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

            // 2. 잠금 및 대기 경로: 항목이 없으면 lock을 잡고 대기
            lock (takeLock)
            {
                // Monitor.Wait을 호출하기 전에 대기 중인 소비자 수를 증가시킵니다.
                Interlocked.Increment(ref waitingConsumers);

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
                    Interlocked.Decrement(ref waitingConsumers);
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
#if NETCOREAPP3_1_OR_GREATER
            SpinWait spin = default;
            while (spin.Count < spinCount)
            {
                spin.SpinOnce(sleep1Threshold: -1);

                if (TryTake(out item))
                {
                    return true;
                }
            }
#else
            for (var spin = 0; spin < spinCount; ++spin)
            {
                if (spin >= 10 && (spin & 1) == 0)
                {
                    Thread.Yield();
                }

                Thread.SpinWait(1);

                if (TryTake(out item))
                {
                    return true;
                }
            }
#endif
            lock (takeLock)
            {
                // Monitor.Wait을 호출하기 전에 대기 중인 소비자 수를 증가시킵니다.
                Interlocked.Increment(ref waitingConsumers);

                try
                {
                    while (true)
                    {
                        // 3-1. 타임아웃 시간 갱신
                        if (useTimeout)
                        {
                            remainTimeout = Companion.UpdateTimeOut(startTimeStamp, millisecondsTimeout);
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
                    Interlocked.Decrement(ref waitingConsumers);
                }
            }
        }
    }

    static class Companion
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
