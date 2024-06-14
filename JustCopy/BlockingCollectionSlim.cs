namespace JustCopy
{
    using System;
    using System.Collections;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Runtime.CompilerServices;
    using System.Threading;

    /// <summary>
    /// BlockingCollection 의 잠금으로 성능 문제가 있을때 대신해서 사용합니다
    /// 적용 후 자신의 환경에서 더 나은 성능으로 작동하는지 테스트가 필요합니다
    /// </summary>
    public sealed class BlockingCollectionSlim<T>
    {
        private readonly IProducerConsumerCollection<T> queue;
        private readonly object takeLock = new object();
        private int waitThreadCount = 0;

        public int SpinCount { get; set; } = 10;

        public int Count => queue.Count;

        public BlockingCollectionSlim(int unsafeConsumeThreadCount = int.MaxValue)
        {
            if (unsafeConsumeThreadCount < 1)
            {
                throw new ArgumentException($"{nameof(unsafeConsumeThreadCount)}({unsafeConsumeThreadCount}) < 1");
            }

            if (unsafeConsumeThreadCount == 1)
            {
                queue = new MpscLinkedQueue<T>();
            }
            else
            {
                queue = new ConcurrentQueue<T>();
            }
        }

        public void Add(T item)
        {
            queue.TryAdd(item);

            if (Volatile.Read(ref waitThreadCount) > 0)
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
            if (queue.TryTake(out item))
            {
                return true;
            }

            item = default;
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
                        remainTimeout = BlockingCollectionSlimCompanion.UpdateTimeOut(startTimeStamp, millisecondsTimeout);
                        if (remainTimeout <= 0)
                        {
                            return false;
                        }
                    }

                    if (TryTake(out item))
                    {
                        if (waitThreadCount > 0)
                        {
                            Monitor.Pulse(takeLock);
                        }

                        return true;
                    }

                    Interlocked.Increment(ref waitThreadCount);

                    try
                    {
                        if (!Monitor.Wait(takeLock, remainTimeout))
                        {
                            return false;
                        }
                    }
                    finally
                    {
                        Interlocked.Decrement(ref waitThreadCount);
                    }
                }
            }
        }
    }

    public static class BlockingCollectionSlimCompanion
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

    internal sealed class MpscLinkedQueueNode<T>
    {
        internal T item;
        internal volatile MpscLinkedQueueNode<T> next;

        public MpscLinkedQueueNode(T item, MpscLinkedQueueNode<T> next)
        {
            this.item = item;
            this.next = next;
        }
    }

    public sealed class MpscLinkedQueue<T> : IProducerConsumerCollection<T>
    {
        // https://www.1024cores.net/home/lock-free-algorithms/queues/non-intrusive-mpsc-node-based-queue

        private volatile MpscLinkedQueueNode<T> head;
        private MpscLinkedQueueNode<T> tail;

        public MpscLinkedQueue()
        {
            head = tail = new MpscLinkedQueueNode<T>(default, null);
        }

        public int Count
        {
            get
            {
                var c = 0;
                var currentNext = tail.next;
                while (currentNext != null)
                {
                    currentNext = currentNext.next;
                    c += 1;
                }

                return c;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryEnqueue(T item)
        {
            var newNode = new MpscLinkedQueueNode<T>(item, null);
            return EnqueueInternal(newNode);
        }

        public void Enqueue(T item)
        {
            var newNode = new MpscLinkedQueueNode<T>(item, null);
            while (true)
            {
                if (EnqueueInternal(newNode))
                {
                    break;
                }
            }
        }

        private bool EnqueueInternal(MpscLinkedQueueNode<T> newNode)
        {
            var currentHead = head;
            if (Interlocked.CompareExchange(ref head, newNode, currentHead) == currentHead)
            {
                currentHead.next = newNode;
                return true;
            }

            return false;
        }
#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1
#else
        private static readonly bool IsReference = typeof(T).IsClass || typeof(T).IsInterface;
#endif
        public bool TryDequeue(
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
#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1
                if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
#else
                if (IsReference)
#endif
                {
                    Debug.Assert(((object)currentNext.item) != null, "item is null");
                    currentNext.item = default;
                }

                return true;
            }

            item = default;
            return false;
        }

        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            throw new NotImplementedException();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            throw new NotImplementedException();
        }

        void ICollection.CopyTo(Array array, int index)
        {
            throw new NotImplementedException();
        }

        bool ICollection.IsSynchronized => throw new NotImplementedException();

        object ICollection.SyncRoot => throw new NotImplementedException();

        void IProducerConsumerCollection<T>.CopyTo(T[] array, int index)
        {
            throw new NotImplementedException();
        }

        T[] IProducerConsumerCollection<T>.ToArray()
        {
            throw new NotImplementedException();
        }

        bool IProducerConsumerCollection<T>.TryAdd(T item)
        {
            Enqueue(item);
            return true;
        }

        bool IProducerConsumerCollection<T>.TryTake(out T item)
        {
            return TryDequeue(out item);
        }
    }
}
