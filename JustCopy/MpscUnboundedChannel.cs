#pragma warning disable IDE0007
#pragma warning disable IDE2003
#pragma warning disable IDE0161
#pragma warning disable IDE0090
#pragma warning disable IDE0083
#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1
#nullable disable
#endif
namespace JustCopy
{
    using System;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Threading.Tasks.Sources;

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct MpscUnboundedChannel_CacheLinePadding
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    public sealed class MpscUnboundedChannel<T> : IValueTaskSource<bool>
    {
        [StructLayout(LayoutKind.Auto)]
        private struct Slot
        {
            public T Item;
            public int WrittenStateVolatile;
        }

        private sealed class Segment
        {
            public readonly Slot[] Slots;
            public Segment NextVolatile;
            public int EnqueueIndex;

            public Segment(int segmentSize)
            {
                Slots = new Slot[segmentSize];
            }
        }

#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1
        private static readonly bool IsReferenceOrContainsReferences = RuntimeHelpers.IsReferenceOrContainsReferences<T>();
#else
        private const bool IsReferenceOrContainsReferences = true;
#endif

#pragma warning disable IDE0051
#pragma warning disable CS0169
        private readonly MpscUnboundedChannel_CacheLinePadding padding0;
        private Segment headVolatile;
        private int isSleepingVolatile;
        private readonly MpscUnboundedChannel_CacheLinePadding padding1;

        private Segment tail;
        private int consumerIndex;

        private ManualResetValueTaskSourceCore<bool> mrvtsc;
        private readonly MpscUnboundedChannel_CacheLinePadding padding2;
        private readonly int segmentSize;
#pragma warning restore CS0169
#pragma warning restore IDE0051

        public MpscUnboundedChannel(int segmentSize)
        {
            var initialSegment = new Segment(segmentSize);
            headVolatile = initialSegment;
            tail = initialSegment;
            this.segmentSize = segmentSize;
            mrvtsc = new ManualResetValueTaskSourceCore<bool>
            {
                RunContinuationsAsynchronously = true
            };
        }

        public bool TryWrite(T item)
        {
            var segment = Volatile.Read(ref headVolatile);
            while (true)
            {
                var index = Interlocked.Increment(ref segment.EnqueueIndex) - 1;

                if (index < segmentSize)
                {
                    ref var segmentSlot = ref segment.Slots[index];
                    segmentSlot.Item = item;
                    Interlocked.Exchange(ref segmentSlot.WrittenStateVolatile, 1);
                    break;
                }

                // --- segment.Next 
                // 다음 코드를 멀티스레드 안정하도록 만든 코드
                // segment.NextVolatile = new Segment(segmentSize);
                // headVolatile = segment.NextVolatile;
                var nextSegment = Volatile.Read(ref segment.NextVolatile);
                if (nextSegment is null)
                {
                    // 여러 개의 스레드 환경에서는 경합이 낮아 대부분 성공합니다.
                    var newSeg = new Segment(segmentSize);
                    if (Interlocked.CompareExchange(ref segment.NextVolatile, newSeg, null) is null)
                    {
                        nextSegment = newSeg;
                    }
                    else
                    {
                        nextSegment = Volatile.Read(ref segment.NextVolatile);
                    }
                }
                // --- segment.Next 끝

                if (Interlocked.CompareExchange(ref headVolatile, nextSegment, segment) == segment)
                {
                    segment = nextSegment;
                }
                else
                {
                    segment = Volatile.Read(ref headVolatile);
                }

                // continue;
            }

            if (Volatile.Read(ref isSleepingVolatile) == 1)
            {
                if (Interlocked.Exchange(ref isSleepingVolatile, 0) == 1)
                {
                    mrvtsc.SetResult(true);
                }
            }

            return true;
        }

        public bool TryRead(
#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1
        [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)]
#endif
            out T outItem)
        {
            var segment = tail;
            var index = consumerIndex;

            if (index == segmentSize)
            {
                var next = Volatile.Read(ref segment.NextVolatile);
                if (next is null)
                {
                    outItem = default;
                    return false;
                }

                tail = next;
                segment = next;
                index = 0;
                consumerIndex = 0;
            }

            ref var segmentSlot = ref segment.Slots[index];
            if (Volatile.Read(ref segmentSlot.WrittenStateVolatile) == 0)
            {
                outItem = default;
                return false;
            }

            outItem = segmentSlot.Item;

            if (IsReferenceOrContainsReferences)
            {
                segmentSlot.Item = default;
            }

            consumerIndex += 1;
            return true;
        }

        public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new ValueTask<bool>(Task.FromCanceled<bool>(cancellationToken));
            }

            var segment = tail;
            var index = consumerIndex;
            {
                if (index < segmentSize)
                {
                    if (Volatile.Read(ref segment.Slots[index].WrittenStateVolatile) == 1)
                    {
                        return new ValueTask<bool>(true);
                    }
                }
                else
                {
                    var next = Volatile.Read(ref segment.NextVolatile);
                    if (next != null)
                    {
                        if (Volatile.Read(ref next.Slots[0].WrittenStateVolatile) == 1)
                        {
                            return new ValueTask<bool>(true);
                        }

                        // 아래에서 쓸걸 대비해 업데이트
                        segment = next;
                        index = 0;
                    }
                }
            }

            mrvtsc.Reset();

            // Full Fence 메모리 방어벽
            Interlocked.Exchange(ref isSleepingVolatile, 1);

            // 2. isSleepingVolatile 세트 후 생산자가 그 찰나에 데이터를 넣었는지 다시 확인
            {
                if (index != segmentSize)
                {
                    if (Volatile.Read(ref segment.Slots[index].WrittenStateVolatile) == 1)
                    {
                        if (Interlocked.Exchange(ref isSleepingVolatile, 0) == 1)
                        {
                            return new ValueTask<bool>(true);
                        }
                    }
                }
                else
                {
                    var next = Volatile.Read(ref segment.NextVolatile);
                    if (next != null)
                    {
                        if (Volatile.Read(ref next.Slots[0].WrittenStateVolatile) == 1)
                        {
                            if (Interlocked.Exchange(ref isSleepingVolatile, 0) == 1)
                            {
                                return new ValueTask<bool>(true);
                            }
                        }
                    }
                }
            }

            return new ValueTask<bool>(this, mrvtsc.Version);
        }

        bool IValueTaskSource<bool>.GetResult(short token)
        {
            return mrvtsc.GetResult(token);
        }

        ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token)
        {
            return mrvtsc.GetStatus(token);
        }

        void IValueTaskSource<bool>.OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            mrvtsc.OnCompleted(continuation, state, token, flags);
        }
    }
}

