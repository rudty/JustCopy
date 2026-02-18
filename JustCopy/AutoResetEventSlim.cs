namespace JustCopy
{
    using System;
    using System.Diagnostics;
    using System.Runtime.CompilerServices;
    using System.Threading;

    /// <summary>
    /// <see cref="AutoResetEventSlim"/> 은 <see cref="AutoResetEvent"/> 를 대체하여 사용할 수 있는 가벼운 이벤트 동기화 클래스입니다.
    /// <para>커널 객체를 바로 생성하지 않고 스핀 대기를 활용하므로, 대부분의 상황에서 <see cref="AutoResetEvent"/> 보다 더 나은 성능을 제공합니다.</para> 
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>1 ~ 2개의 스레드 대기 상황에서는 대부분의 상황에서 <see cref="AutoResetEventSlim"/> 이 좋습니다</description></item>
    /// <item><description>4개 이상의 스레드를 기다리는 상황에서는 <see cref="SemaphoreSlim"/> 을 사용하는 것이 더 좋은 성능을 보입니다</description></item>
    /// <item><description>어느쪽이어도 <see cref="AutoResetEvent"/> 보다는 좋은 성능으로 작동합니다</description></item>
    /// </list>
    /// </remarks>
    public sealed class AutoResetEventSlim
    {
        private readonly object lockObject = new object();
        private static readonly double tickFrequency = (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency;

        public int SpinCount { get; set; } = 10;

        private int isSet;
        private int waiters;

        public bool IsSet => Volatile.Read(ref isSet) == 1;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryAutoReset()
        {
            return Interlocked.Exchange(ref isSet, 0) == 1;
        }

        public void Set()
        {
            var oldSet = Interlocked.Exchange(ref isSet, 1);

            if (oldSet == 1)
            {
                return;
            }

            if (Volatile.Read(ref waiters) > 0)
            {
                lock (lockObject)
                {
                    Monitor.Pulse(lockObject);
                }
            }
        }

        public bool WaitOne(int millisecondsTimeout = Timeout.Infinite)
        {
            if (TryAutoReset())
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
                if (TryAutoReset())
                {
                    return true;
                }
            }

            lock (lockObject)
            {
                Interlocked.Increment(ref waiters);
                try
                {
                    while (true)
                    {

                        if (useTimeout)
                        {
                            remainTimeout = UpdateTimeOut(startTimeStamp, millisecondsTimeout);
                            if (remainTimeout <= 0)
                            {
                                return false;
                            }
                        }

                        if (TryAutoReset())
                        {
                            return true;
                        }

                        if (!Monitor.Wait(lockObject, remainTimeout))
                        {
                            return false;
                        }
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref waiters);
                }
            }
        }

        private static int UpdateTimeOut(long startTimeStamp, int originalWaitMillisecondsTimeout)
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
        private static long GetElapsedMilliseconds(long startTimeStamp)
        {
            var currentTimeStamp = Stopwatch.GetTimestamp();
            var elapsedTimeStamp = currentTimeStamp - startTimeStamp;
            return unchecked((long)(elapsedTimeStamp * tickFrequency)) / TimeSpan.TicksPerMillisecond;
        }
    }
}
