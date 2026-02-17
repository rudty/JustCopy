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
    using System.Threading;
    using System.Runtime.InteropServices;
    using System.Runtime.CompilerServices;

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
#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1
        private static readonly bool IsReferenceOrContainsReferences = RuntimeHelpers.IsReferenceOrContainsReferences<T>();
#else
        private const bool IsReferenceOrContainsReferences = true;
#endif

        private readonly object syncRoot = new object();
        private readonly SingleProducerSingleConsumerQueue queue;

        private bool isWaiting;
        private T fastItem;

        public MpscBlockingQueue(int initializeSegmentSize = 4096)
        {
            queue = new SingleProducerSingleConsumerQueue(initializeSegmentSize);
        }

        public void Add(T item)
        {
            lock (syncRoot)
            {
                if (isWaiting)
                {
                    fastItem = item;
                    isWaiting = false; // "너 이제 대기 상태 아님!" 이라고 알려줌
                    Monitor.Pulse(syncRoot);
                }
                else
                {
                    queue.Enqueue(item);
                }
            }
        }

        public bool TryTake(
#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1
            [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] 
#endif
            out T outItem)
        {
            return queue.TryDequeue(out outItem);
        }

        public bool TryTake(
#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1
            [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)]
#endif
            out T item, int millisecondsTimeout)
        {
            // 1. Fast Path
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

            // 2. Sleep 준비
            lock (syncRoot)
            {
                // 3-1. Monitor.Wait 전에 항목을 다시 확인
                if (TryTake(out item))
                {
                    return true; // 락 잡는 찰나에 들어왔는지 재확인
                }

                while (true)
                {
                    // 3-2. 타임아웃 시간 갱신
                    if (useTimeout)
                    {
                        remainTimeout = MpscBlockingQueue_Companion.UpdateTimeOut(startTimeStamp, millisecondsTimeout);
                        if (remainTimeout <= 0)
                        {
                            return false; // 타임아웃 만료
                        }
                    }

                    isWaiting = true;
                    if (!Monitor.Wait(syncRoot, remainTimeout))
                    {
                        isWaiting = false; 
                        return false; // 타임아웃 발생 (Monitor.Wait이 false 반환)
                    }

                    // 신호 받고 깨어남 (Add에서 isWaiting을 false로 설정했음)
                    if (!isWaiting)
                    {
                        item = fastItem;
                        if (IsReferenceOrContainsReferences)
                        {
                            fastItem = default;
                        }

                        return true; 
                    }
                }
            }
        }

        public T Take()
        {
            // 1. Fast Path
            if (TryTake(out var item))
            {
                return item;
            }

            // 2. Sleep 준비
            lock (syncRoot)
            {
                if (TryTake(out item))
                {
                    return item; // 락 잡는 찰나에 들어왔는지 재확인
                }

                isWaiting = true;

                while (isWaiting)
                {
                    Monitor.Wait(syncRoot);
                }

                item = fastItem;
                if (IsReferenceOrContainsReferences)
                {
                    fastItem = default;
                }

                return item;
            }
        }

        // Licensed to the .NET Foundation under one or more agreements.
        // The .NET Foundation licenses this file to you under the MIT license.
        /// <summary>
        /// Provides a producer/consumer queue safe to be used by only one producer and one consumer concurrently.
        /// </summary>
        private sealed class SingleProducerSingleConsumerQueue
        {
            // Design:
            //
            // SingleProducerSingleConsumerQueue (SPSCQueue) is a concurrent queue designed to be used
            // by one producer thread and one consumer thread. SPSCQueue does not work correctly when used by
            // multiple producer threads concurrently or multiple consumer threads concurrently.
            //
            // SPSCQueue is based on segments that behave like circular buffers. Each circular buffer is represented
            // as an array with two indexes: _first and _last. _first is the index of the array slot for the consumer
            // to read next, and _last is the slot for the producer to write next. The circular buffer is empty when
            // (_first == _last), and full when ((_last+1) % _array.Length == _first).
            //
            // Since _first is only ever modified by the consumer thread and _last by the producer, the two indices can
            // be updated without interlocked operations. As long as the queue size fits inside a single circular buffer,
            // enqueues and dequeues simply advance the corresponding indices around the circular buffer. If an enqueue finds
            // that there is no room in the existing buffer, however, a new circular buffer is allocated that is twice as big
            // as the old buffer. From then on, the producer will insert values into the new buffer. The consumer will first
            // empty out the old buffer and only then follow the producer into the new (larger) buffer.
            //
            // As described above, the enqueue operation on the fast path only modifies the _first field of the current segment.
            // However, it also needs to read _last in order to verify that there is room in the current segment. Similarly, the
            // dequeue operation on the fast path only needs to modify _last, but also needs to read _first to verify that the
            // queue is non-empty. This results in true cache line sharing between the producer and the consumer.
            //
            // The cache line sharing issue can be mitigating by having a possibly stale copy of _first that is owned by the producer,
            // and a possibly stale copy of _last that is owned by the consumer. So, the consumer state is described using
            // (_first, _lastCopy) and the producer state using (_firstCopy, _last). The consumer state is separated from
            // the producer state by padding, which allows fast-path enqueues and dequeues from hitting shared cache lines.
            // _lastCopy is the consumer's copy of _last. Whenever the consumer can tell that there is room in the buffer
            // simply by observing _lastCopy, the consumer thread does not need to read _last and thus encounter a cache miss. Only
            // when the buffer appears to be empty will the consumer refresh _lastCopy from _last. _firstCopy is used by the producer
            // in the same way to avoid reading _first on the hot path.

            /// <summary>The initial size to use for segments (in number of elements).</summary>
            private const int MaxSegmentSize = 0x1000000; // this could be made as large as int.MaxValue / 2

            /// <summary>The head of the linked list of segments.</summary>
            private volatile Segment _head;
            /// <summary>The tail of the linked list of segments.</summary>
            private volatile Segment _tail;

            /// <summary>Initializes the queue.</summary>
            public SingleProducerSingleConsumerQueue(int initializeSegmentSize)
            {
                // Validate constants in ctor rather than in an explicit cctor that would cause perf degradation
                Debug.Assert(initializeSegmentSize > 0, "Initial segment size must be > 0.");
                Debug.Assert((initializeSegmentSize & (initializeSegmentSize - 1)) == 0, "Initial segment size must be a power of 2");
                Debug.Assert(initializeSegmentSize <= MaxSegmentSize, "Initial segment size should be <= maximum.");
                Debug.Assert(MaxSegmentSize < int.MaxValue / 2, "Max segment size * 2 must be < int.MaxValue, or else overflow could occur.");

                // Initialize the queue
                _head = _tail = new Segment(initializeSegmentSize);
            }

            /// <summary>Enqueues an item into the queue.</summary>
            /// <param name="item">The item to enqueue.</param>
            public void Enqueue(T item)
            {
                var segment = _tail;
                var array = segment._array;
                var last = segment._state._last; // local copy to avoid multiple volatile reads

                // Fast path: there's obviously room in the current segment
                var tail2 = (last + 1) & (array.Length - 1);
                if (tail2 != segment._state._firstCopy)
                {
                    array[last] = item;
                    segment._state._last = tail2;
                    return;
                }

                // Slow path: there may not be room in the current segment.
                EnqueueSlow(item, ref segment);
            }

            /// <summary>Enqueues an item into the queue.</summary>
            /// <param name="item">The item to enqueue.</param>
            /// <param name="segment">The segment in which to first attempt to store the item.</param>
            private void EnqueueSlow(T item, ref Segment segment)
            {
                Debug.Assert(segment != null, "Expected a non-null segment.");

                if (segment._state._firstCopy != segment._state._first)
                {
                    segment._state._firstCopy = segment._state._first;
                    Enqueue(item); // will only recur once for this enqueue operation
                    return;
                }

                var newSegmentSize = Math.Min(_tail._array.Length * 2, MaxSegmentSize);
                Debug.Assert(newSegmentSize > 0, "The max size should always be small enough that we don't overflow.");

                var newSegment = new Segment(newSegmentSize);
                newSegment._array[0] = item;
                newSegment._state._last = 1;
                newSegment._state._lastCopy = 1;

                try
                { }
                finally
                {
                    // Finally block to protect against corruption due to a thread abort between
                    // setting _next and setting _tail (this is only relevant on .NET Framework).
                    Volatile.Write(ref _tail._next, newSegment); // ensure segment not published until item is fully stored
                    _tail = newSegment;
                }
            }

            /// <summary>Attempts to dequeue an item from the queue.</summary>
            /// <param name="result">The dequeued item.</param>
            /// <returns>true if an item could be dequeued; otherwise, false.</returns>
            public bool TryDequeue(
#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1
            [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] 
#endif
                out T result)

            {
                var segment = _head;
                var array = segment._array;
                var first = segment._state._first; // local copy to avoid multiple volatile reads

                // Fast path: there's obviously data available in the current segment
                if (first != segment._state._lastCopy)
                {
                    result = array[first];
                    if (IsReferenceOrContainsReferences)
                    {
                        array[first] = default; // Clear the slot to release the element
                    }

                    segment._state._first = (first + 1) & (array.Length - 1);
                    return true;
                }

                // Slow path: there may not be data available in the current segment
                return TryDequeueSlow(segment, array, peek: false, out result);
            }

            /// <summary>Attempts to peek at an item in the queue.</summary>
            /// <param name="result">The peeked item.</param>
            /// <returns>true if an item could be peeked; otherwise, false.</returns>
            public bool TryPeek(
#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1
            [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] 
#endif
                out T result)
            {
                var segment = _head;
                var array = segment._array;
                var first = segment._state._first; // local copy to avoid multiple volatile reads

                // Fast path: there's obviously data available in the current segment
                if (first != segment._state._lastCopy)
                {
                    result = array[first];
                    return true;
                }

                // Slow path: there may not be data available in the current segment
                return TryDequeueSlow(segment, array, peek: true, out result);
            }

            /// <summary>Attempts to dequeue an item from the queue.</summary>
            /// <param name="segment">The segment from which the item was dequeued.</param>
            /// <param name="array">The array from <paramref name="segment"/>.</param>
            /// <param name="peek">true if this is only a peek operation; false if the item should be dequeued.</param>
            /// <param name="result">The dequeued item.</param>
            /// <returns>true if an item could be dequeued; otherwise, false.</returns>
            private bool TryDequeueSlow(Segment segment, T[] array, bool peek,
#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1
            [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] 
#endif
                out T result)
            {
                Debug.Assert(segment != null, "Expected a non-null segment.");
                Debug.Assert(array != null, "Expected a non-null item array.");

                if (segment._state._last != segment._state._lastCopy)
                {
                    segment._state._lastCopy = segment._state._last;
                    return peek ?
                        TryPeek(out result) :
                        TryDequeue(out result); // will only recur once for this operation
                }

                if (segment._next != null && segment._state._first == segment._state._last)
                {
                    segment = segment._next;
                    array = segment._array;
                    _head = segment;
                }

                int first = segment._state._first; // local copy to avoid extraneous volatile reads

                if (first == segment._state._last)
                {
                    result = default;
                    return false;
                }

                result = array[first];
                if (!peek)
                {
                    array[first] = default; // Clear the slot to release the element
                    segment._state._first = (first + 1) & (segment._array.Length - 1);
                    segment._state._lastCopy = segment._state._last; // Refresh _lastCopy to ensure that _first has not passed _lastCopy
                }

                return true;
            }

            /// <summary>A segment in the queue containing one or more items.</summary>
            [StructLayout(LayoutKind.Sequential)]
            private sealed class Segment
            {
                /// <summary>The next segment in the linked list of segments.</summary>
                internal Segment _next;
                /// <summary>The data stored in this segment.</summary>
                internal readonly T[] _array;
                /// <summary>Details about the segment.</summary>
                internal MpscBlockingQueue_SegmentState _state; // separated out to enable StructLayout attribute to take effect

                /// <summary>Initializes the segment.</summary>
                /// <param name="size">The size to use for this segment.</param>
                internal Segment(int size)
                {
                    Debug.Assert((size & (size - 1)) == 0, "Size must be a power of 2");
                    _array = new T[size];
                }
            }
        }
    }

    static class MpscBlockingQueue_Companion
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

    [StructLayout(LayoutKind.Explicit, Size = 124)]
    internal struct MpscBlockingQueue_PaddingFor32
    {
    }

    /// <summary>Stores information about a segment.</summary>
    [StructLayout(LayoutKind.Sequential)] // enforce layout so that padding reduces false sharing
    public struct MpscBlockingQueue_SegmentState
    {
        /// <summary>Padding to reduce false sharing between the segment's array and _first.</summary>
        internal MpscBlockingQueue_PaddingFor32 _pad0;

        /// <summary>The index of the current head in the segment.</summary>
        internal volatile int _first;
        /// <summary>A copy of the current tail index.</summary>
        internal int _lastCopy; // not volatile as read and written by the producer, except for IsEmpty, and there _lastCopy is only read after reading the volatile _first

        /// <summary>Padding to reduce false sharing between the first and last.</summary>
        internal MpscBlockingQueue_PaddingFor32 _pad1;

        /// <summary>A copy of the current head index.</summary>
        internal int _firstCopy; // not volatile as only read and written by the consumer thread
        /// <summary>The index of the current tail in the segment.</summary>
        internal volatile int _last;

        /// <summary>Padding to reduce false sharing with the last and what's after the segment.</summary>
        internal MpscBlockingQueue_PaddingFor32 _pad2;
    }
}