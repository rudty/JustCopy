namespace JustCopy
{
    using System;
    using System.Collections.Concurrent;
    using System.Diagnostics;
    using System.Runtime.CompilerServices;
    using System.Threading;

    /// <summary>
    /// BlockingCollection 의 잠금으로 성능 문제가 있을때 대신해서 사용합니다
    /// 적용 후 자신의 환경에서 더 나은 성능으로 작동하는지 테스트가 필요합니다
    /// </summary>
    public sealed class BlockingCollectionSlim<T>
    {
        private readonly ConcurrentQueue<T> queue = new ConcurrentQueue<T>();
        private readonly object lockObject = new object();

        public int SpinCount { get; set; } = 10;

        private int count;

        public int Count => count;

        private readonly int addNoPulseCount;

        public BlockingCollectionSlim(int unsafeConsumeThreadCount = int.MaxValue)
        {
            if (unsafeConsumeThreadCount < 1)
            {
                throw new ArgumentException($"{nameof(unsafeConsumeThreadCount)}({unsafeConsumeThreadCount}) < 1");
            }
            
            var localAddNoPulseCount = unsafeConsumeThreadCount * 2L;
            if (localAddNoPulseCount > int.MaxValue)
            {
                localAddNoPulseCount = int.MaxValue;
            }

            addNoPulseCount = (int)localAddNoPulseCount;
        }

        public void Add(T item)
        {
            queue.Enqueue(item);
            var oldCount = Interlocked.Increment(ref count);
            if (oldCount > addNoPulseCount)
            {
                return;
            }

            lock (lockObject)
            {
                Monitor.Pulse(lockObject);
            }
        }

#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1
        public bool TryTake([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? item)
#else
        public bool TryTake(out T item)
#endif
        {
            var localCount = Volatile.Read(ref count);
            if (localCount > 0)
            {
                if (queue.TryDequeue(out item))
                {
                    _ = Interlocked.Decrement(ref count);
                    return true;
                }
            }
            else
            {
                item = default;
            }

            return false;
        }

        public T Take()
        {
            while (true)
            {
                if (TryTake(out var item, Timeout.Infinite))
                {
                    return item;
                }
            }
        }

#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1
        public bool TryTake([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? item, int millisecondsTimeout)
#else
        public bool TryTake(out T item, int millisecondsTimeout)
#endif
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
            lock (lockObject)
            {
                while (true)
                {
                    if (useTimeout)
                    {
                        remainTimeout = BlockingCollectionSlimCompanion.UpdateTimeOut(startTimeStamp, millisecondsTimeout);
                        if (remainTimeout <= 0)
                        {
                            return false;
                        }
                    }

                    if (TryTake(out item))
                    {
                        return true;
                    }

                    if (!Monitor.Wait(lockObject, remainTimeout))
                    {
                        return false;
                    }
                }
            }
        }
    }

    public class BlockingCollectionSlimCompanion
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
