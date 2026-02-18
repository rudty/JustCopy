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
    /// Ring Buffer를 사용한 고성능 Queue 구현입니다.
    /// 프로그램 시작 시 객체를 생성하고 삭제하지 않는 구조에서는 System.Threading.Channels.Channel 보다 좋은 성능으로 동작합니다.
    /// 생성되고 해제될 가능성이 있는 곳에서는 GC 혹은 SpinWait의 부담이 커서 대부분의 상황에서 나쁜 성능을 가집니다.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public sealed class MpscBoundedChannel<T> : IValueTaskSource<bool>
    {
        // 데이터와 시퀀스(순서)를 한 몸으로 묶은 구조체
        [StructLayout(LayoutKind.Auto)]
        private struct Slot
        {
            public T Item;
            public int SequenceNumberVolatile;
        }

#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1
        private static readonly bool IsReferenceOrContainsReferences = RuntimeHelpers.IsReferenceOrContainsReferences<T>();
#else
        private const bool IsReferenceOrContainsReferences = true;
#endif

        private readonly Slot[] slots;
        private readonly int mask;

        /// <summary>
        /// 생산자가 쓰는 위치
        /// </summary>
        private MpscBoundedChannel_PaddedInt tail;

        /// <summary>
        /// 소비자가 읽는 위치
        /// </summary>
        private MpscBoundedChannel_PaddedInt head;

        private int isWaitingVolatile;

        /// <summary>
        /// .netstandard 2.0 = PackageReference Microsoft.Bcl.AsyncInterfaces
        /// </summary>
        private ManualResetValueTaskSourceCore<bool> mrvtsc;

        public MpscBoundedChannel(int minCapacity = 131072)
        {
            const int maxCapacity = 1 << 30; // int 범위 내에서 최대 2의 배수

            if (minCapacity < 32)
            {
                minCapacity = 32; // 최소 크기 보장
            }
            else if (minCapacity > maxCapacity)
            {
                minCapacity = maxCapacity;
            }
            else
            {
#if NETCOREAPP3_0_OR_GREATER
                minCapacity = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)minCapacity);
#else
                // 2의 제곱수로 올림 (capacity가 이미 2의 제곱수라면 그대로 유지)
                minCapacity -= 1;
                minCapacity |= minCapacity >> 1;
                minCapacity |= minCapacity >> 2;
                minCapacity |= minCapacity >> 4;
                minCapacity |= minCapacity >> 8;
                minCapacity |= minCapacity >> 16;
                minCapacity += 1;
#endif
            }

            slots = new Slot[minCapacity];
            mask = minCapacity - 1;

            mrvtsc = new ManualResetValueTaskSourceCore<bool>
            {
                RunContinuationsAsynchronously = true
            };
        }

        public bool TryWrite(T item)
        {
            // 1. 꼬리표(티켓) 뽑기
            var nextTail = Interlocked.Increment(ref tail.ValueVolatile);
            var currentTail = nextTail - 1;

            // 2. 백프레셔(Backpressure): 큐가 한 바퀴 다 돌아서 꽉 찼다면 소비자가 비워줄 때까지 대기
            SpinWait spin = default;
            var headValue = Volatile.Read(ref head.ValueVolatile);
            while (unchecked(currentTail - headValue) >= slots.Length)
            {
                spin.SpinOnce();
                headValue = Volatile.Read(ref head.ValueVolatile);
            }

            var index = currentTail & mask;

            // 3. 데이터 쓰기 및 시퀀스 넘버 발행 (ABA 문제 완벽 차단)
            ref var currentSlot = ref slots[index];
            currentSlot.Item = item;
            Volatile.Write(ref currentSlot.SequenceNumberVolatile, nextTail);

            // MemoryBarrier 가 없으면 컴파일러/CPU 최적화로 위아래 구문이 바뀔 수 있음
            Interlocked.MemoryBarrier();

            // 4. 잠든 소비자 깨우기
            if (Volatile.Read(ref isWaitingVolatile) == 1)
            {
                if (Interlocked.Exchange(ref isWaitingVolatile, 0) == 1)
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
            out T item)
        {
            var currentHead = Volatile.Read(ref head.ValueVolatile);
            var index = currentHead & mask;
            var nextHead = unchecked(currentHead + 1);
            ref var currentSlot = ref slots[index];

            // 시퀀스 넘버가 정확히 이번 바퀴(currentHead + 1)인지 확인
            if (Volatile.Read(ref currentSlot.SequenceNumberVolatile) == nextHead)
            {
                item = currentSlot.Item;
                if (IsReferenceOrContainsReferences)
                {
                    currentSlot.Item = default;
                }

                // 머리 위치 이동 (단일 소비자이므로 Interlocked 불필요)
                Volatile.Write(ref head.ValueVolatile, nextHead);
                return true;
            }

            item = default;
            return false;
        }

        public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new ValueTask<bool>(Task.FromCanceled<bool>(cancellationToken));
            }

            var currentHead = Volatile.Read(ref head.ValueVolatile);
            var index = currentHead & mask;
            var nextHead = unchecked(currentHead + 1);
            ref var currentSlot = ref slots[index];

            if (Volatile.Read(ref currentSlot.SequenceNumberVolatile) == nextHead)
            {
                return new ValueTask<bool>(true);
            }

            mrvtsc.Reset();
            Interlocked.Exchange(ref isWaitingVolatile, 1);

            if (Volatile.Read(ref currentSlot.SequenceNumberVolatile) == nextHead)
            {
                if (Interlocked.Exchange(ref isWaitingVolatile, 0) == 1)
                {
                    return new ValueTask<bool>(true);
                }
            }

            return new ValueTask<bool>(this, mrvtsc.Version);
        }

        // IValueTaskSource<bool> 인터페이스 맵핑
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

    // CPU 캐시 오염을 막기 위한 128바이트 패딩 인덱스
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    internal struct MpscBoundedChannel_PaddedInt
    {
        [FieldOffset(64)] public int ValueVolatile;
    }
}