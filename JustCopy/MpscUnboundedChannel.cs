// 기본적으로 UnboundedChannel 에서 NextSegment 할 때 잠금을 사용하는 매크로입니다.
// 만약 잠금이 없는 버전을 사용하려면 이 매크로를 제거하세요.
// 잠금이 없는 버전은 락 프리이지만, 세그먼트 확장 시 다수의 생산자가 동시에 NextSegment를 시도할 때
// 일시적으로 성능 저하가 발생할 수 있습니다.
#define MPSC_UNBOUND_CHANNEL_NEXT_SEGMENT_LOCK

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

    /// <summary>
    /// System.Threading.Channels.Channel&lt;T&gt;의 Unbounded 모드를 대체하기 위해 설계된 
    /// 고성능 MPSC(Multiple-Producer Single-Consumer) 비동기 큐입니다.
    /// <para>
    /// 일반적인 상황에서는 표준 라이브러리의 최적화된 풀링 정책으로 인해 성능이 오히려 떨어질 수 있으나,
    /// 생산자(Producer) 스레드가 잠금(Lock)으로 인해 블로킹되는 현상을 원천적으로 방지해야 하는 
    /// 초고성능 파이프라인(Network I/O, Logging 등)에 최적화되어 있습니다.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <para><b>[아키텍처 특징]</b></para>
    /// <list type="bullet">
    /// <item>
    ///     <description><b>세그먼트 링크드 리스트 (Segmented Linked List):</b> 
    ///     내부적으로 배열 세그먼트를 연결 리스트로 관리합니다. 
    ///     큐가 확장될 때 전체 배열을 복사(Resize)하는 비용이 발생하지 않으며, 메모리 파편화를 줄입니다.
    ///     </description>
    /// </item>
    /// <item>
    ///     <description><b>Lock-Free 쓰기 (Wait-Free Enqueue):</b> 
    ///     다수의 생산자가 <see cref="Interlocked.Increment(ref int)"/> 연산만으로 경합 없이 데이터를 삽입합니다. 
    ///     표준 채널과 달리 쓰기 작업에서 스레드 대기(Spin/Monitor)가 발생하지 않습니다.
    ///     </description>
    /// </item>
    /// <item>
    ///     <description><b>배치 할당 (Batch Allocation):</b> 
    ///     데이터 하나마다 노드를 생성하지 않고 세그먼트 단위로 메모리를 할당하여, 
    ///     대량의 메시지 처리 시 GC(가비지 컬렉션) 오버헤드를 최소화했습니다.
    ///     </description>
    /// </item>
    /// <item>
    ///     <description><b>효율적인 비동기 대기:</b> 
    ///     <see cref="WaitToReadAsync"/>를 통해 소비자가 데이터 유입을 CPU 소모 없이 효율적으로 기다릴 수 있습니다.
    ///     </description>
    /// </item>
    /// </list>
    /// <para><b>[스레드 안전성 주의]</b></para>
    /// <para>
    /// 이 클래스는 <b>단일 소비자(Single Consumer)</b> 패턴을 전제로 설계되었습니다. 
    /// <see cref="TryWrite(T)"/>는 여러 스레드에서 동시에 호출해도 안전하지만, 
    /// <see cref="TryRead(out T)"/> 및 <see cref="WaitToReadAsync"/>는 반드시 한 번에 하나의 스레드에서만 호출해야 합니다.
    /// </para>
    /// </remarks>
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
#if MPSC_UNBOUND_CHANNEL_NEXT_SEGMENT_LOCK
#if NET10_0_OR_GREATER
        private readonly Lock nextSegmentLock = new();
#else
        private readonly object nextSegmentLock = new object();
#endif
#endif
        private readonly MpscUnboundedChannel_CacheLinePadding padding0;
        private Segment headVolatile;
        private int isSleepingVolatile;
        private readonly MpscUnboundedChannel_CacheLinePadding padding1;

        private Segment tail;
        private int consumerIndex;

        private ManualResetValueTaskSourceCore<bool> mrvtsc;
        private readonly MpscUnboundedChannel_CacheLinePadding padding2;
        private readonly int maxSegmentSize;
#pragma warning restore CS0169
#pragma warning restore IDE0051

        public MpscUnboundedChannel(int maxSegmentSize = 4096)
        {
            const int initialSize = 32;
            var initialSegment = new Segment(initialSize);
            headVolatile = initialSegment;
            tail = initialSegment;
            this.maxSegmentSize = maxSegmentSize;
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
                var currentSegmentSize = segment.Slots.Length;
                if (index < currentSegmentSize)
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
#if MPSC_UNBOUND_CHANNEL_NEXT_SEGMENT_LOCK
                if (nextSegment is null)
                {
                    lock (nextSegmentLock)
                    {
                        // 3. 락 안에서 다시 한번 확인 (진짜 없는지?)
                        nextSegment = Volatile.Read(ref segment.NextVolatile);

                        if (nextSegment is null)
                        {
                            // 4. 진짜 없을 때만 할당
                            var nextSegmentSize = Math.Min((segment.Slots.Length << 1), maxSegmentSize);
                            nextSegment = new Segment(nextSegmentSize);

                            // 5. 연결 (락 안이므로 Interlocked 불필요, 하지만 가시성을 위해 Volatile.Write)
                            Volatile.Write(ref segment.NextVolatile, nextSegment);
                            Volatile.Write(ref headVolatile, nextSegment);
                            Interlocked.MemoryBarrier();
                        }
                    }
                }

                segment = nextSegment;
#else
                if (nextSegment is null)
                {
                    var nextSegmentSize = Math.Min((segment.Slots.Length << 1), maxSegmentSize);
                    nextSegment = new Segment(nextSegmentSize);
                    if (Interlocked.CompareExchange(ref segment.NextVolatile, nextSegment, null) != null)
                    {
                        nextSegment = Volatile.Read(ref segment.NextVolatile);
                    }
                }

                if (Interlocked.CompareExchange(ref headVolatile, nextSegment, segment) == segment)
                {
                    segment = nextSegment;
                }
                else
                {
                    segment = Volatile.Read(ref headVolatile);
                }
#endif
            } // while (true)

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

            if (index == segment.Slots.Length)
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
                if (index < segment.Slots.Length)
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
                if (index != segment.Slots.Length)
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

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct MpscUnboundedChannel_CacheLinePadding
    {
    }
}

