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
    using System.Diagnostics;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using System.Threading;

    /// <summary>
    /// Multiple-producer single-consumer (MPSC) blocking queue.
    /// Note: T must be non-null reference when T is reference type — Add(null) throws.
    /// Only a single consumer may call Take/TryTake concurrently.
    /// -
    /// BlockingCollection 의 잠금으로 성능 문제가 있을때 대신해서 사용합니다
    /// 적용 후 자신의 환경에서 더 나은 성능으로 작동하는지 테스트가 필요합니다
    /// </summary>
    public sealed class MpscBlockingQueue<T>
    {
        private readonly MpscBlockingQueueLinkedQueue<T> queue = new MpscBlockingQueueLinkedQueue<T>();
        private readonly object takeLock = new object();

        private int waiters;

        public int SpinCount { get; set; } = 10;

        public void Add(T item)
        {
            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }
            
            queue.Enqueue(item);

            if (Volatile.Read(ref waiters) > 0)
            {
                lock (takeLock)
                {
                    Monitor.Pulse(takeLock);
                }
            }
        }

        public bool TryTake(
#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1
            [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] 
#endif
            out T item)
        {
            if (queue.TryDequeue(out item))
            {
                return true;
            }

            item = default;
            return false;
        }

        public T Take()
        {
            if (TryTake(out var item))
            {
                return item;
            }

            lock (takeLock)
            {
                while (true)
                {
                    try
                    {
                        Interlocked.Increment(ref waiters);

                        if (TryTake(out item))
                        {
                            return item;
                        }

                        Monitor.Wait(takeLock);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref waiters);
                    }
                }
            }
        }

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
                while (true)
                {
                    if (useTimeout)
                    {
                        remainTimeout = MpmcBlockingCollectionSlimCompanion.UpdateTimeOut(startTimeStamp, millisecondsTimeout);
                        if (remainTimeout <= 0)
                        {
                            return false;
                        }
                    }

                    try
                    {
                        Interlocked.Increment(ref waiters);

                        if (TryTake(out item))
                        {
                            return true;
                        }

                        if (!Monitor.Wait(takeLock, remainTimeout))
                        {
                            return false;
                        }
                    }
                    finally
                    {
                        Interlocked.Decrement(ref waiters);
                    }
                }
            }
        }
    }

    internal static class MpmcBlockingCollectionSlimCompanion
    {
        private static readonly double tickFrequency = (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int UpdateTimeOut(long startTimeStamp, int originalWaitMillisecondsTimeout)
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
        internal static long GetElapsedMilliseconds(long startTimeStamp)
        {
            var currentTimeStamp = Stopwatch.GetTimestamp();
            var elapsedTimeStamp = currentTimeStamp - startTimeStamp;
            return unchecked((long)(elapsedTimeStamp * tickFrequency)) / TimeSpan.TicksPerMillisecond;
        }
    }

    internal sealed class MpscBlockingQueueLinkedQueueNode<T>
    {
        internal T item;
        internal volatile MpscBlockingQueueLinkedQueueNode<T> next;

        internal MpscBlockingQueueLinkedQueueNode(T item, MpscBlockingQueueLinkedQueueNode<T> next)
        {
            this.item = item;
            this.next = next;
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct MpscBlockingQueueCacheLinePadding
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    internal sealed class MpscBlockingQueueLinkedQueue<T>
    {
        // https://www.1024cores.net/home/lock-free-algorithms/queues/non-intrusive-mpsc-node-based-queue

#pragma warning disable IDE0051
#pragma warning disable CS0169
        private readonly MpscBlockingQueueCacheLinePadding padding0;
        private volatile MpscBlockingQueueLinkedQueueNode<T> head;
        private readonly MpscBlockingQueueCacheLinePadding padding1;
        private MpscBlockingQueueLinkedQueueNode<T> tail;
        private readonly MpscBlockingQueueCacheLinePadding padding2;
#pragma warning restore CS0169
#pragma warning restore IDE0051
        internal MpscBlockingQueueLinkedQueue()
        {
            head = tail = new MpscBlockingQueueLinkedQueueNode<T>(
#pragma warning disable CS8604
                default,
#pragma warning restore CS8604
                null);
        }

        internal void Enqueue(T item)
        {
            var newNode = new MpscBlockingQueueLinkedQueueNode<T>(item, null);
            var prevHead = Interlocked.Exchange(ref head, newNode);
            prevHead.next = newNode;
        }

        private static readonly bool IsReferenceOrContainsReferences =
#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1
        RuntimeHelpers.IsReferenceOrContainsReferences<T>();
#else
        typeof(T).IsClass || typeof(T).IsInterface;
#endif
        internal bool TryDequeue(
#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1
        [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)]
#endif
        out T item)
        {
            var currentNext = tail.next;
            if (currentNext != null)
            {
                tail = currentNext;
                item = currentNext.item;
                if (IsReferenceOrContainsReferences)
                {
                    Debug.Assert(((object)currentNext.item) != null, "item is null");
                    currentNext.item = default;
                }

                return true;
            }

            item = default;
            return false;
        }
    }
}
