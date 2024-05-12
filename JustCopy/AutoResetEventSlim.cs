namespace JustCopy
{
    using System;
    using System.Diagnostics;
    using System.Runtime.CompilerServices;
    using System.Threading;

    /// <summary>
    /// AutoResetEvent 대신 사용 가능한 클래스
    /// </summary>
    public sealed class AutoResetEventSlim
    {
        private readonly object lockObject = new object();
        private static readonly double tickFrequency = (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency;

        public int SpinCount { get; set; } = 10;

        private int isSet;

        public int IsSet => Volatile.Read(ref isSet);

        private bool TryAutoReset()
        {
            return Interlocked.Exchange(ref isSet, 0) == 1;
        }

        public void Set()
        {
            var oldSet = Interlocked.Exchange(ref isSet, 1);
            if (oldSet is 0)
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
#if NETCOREAPP3_1_OR_GREATER
            SpinWait spin = default;
            while (spin.Count < spinCount)
            {
                spin.SpinOnce(sleep1Threshold: -1);

                if (TryAutoReset())
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

                if (TryAutoReset())
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
