#pragma warning disable IDE0007
#pragma warning disable IDE0090
#pragma warning disable IDE0161
#pragma warning disable IDE0130

namespace JustCopy
{
    using System;
    using System.Collections.Concurrent;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.Runtime.CompilerServices;
    using System.Threading;

    /// <summary>
    /// <see cref="System.Threading.Channels.Channel"/> 의 동기적 버전입니다.
    /// Channel 의 구현체를 그대로 옮겼으므로 Channel 과 비슷한 성능을 가집니다.
    /// 동기적으로 사용했을때(AsTask().Result) Task 할당이 없어 약간의 GC 이득이 있습니다.
    /// </summary>
    /// <typeparam name="T">자료 타입</typeparam>
    public sealed class SyncUnboundedChannel<T>
    {
        private bool _isCompleted;
        private readonly ConcurrentQueue<T> _items = new ConcurrentQueue<T>();
        private readonly object _syncObj = new object();
        private SyncUnboundedChannelWaiter? _waitingReadersTail;

        private readonly SyncUnboundedChannelWaiter _waiterSingleton = new SyncUnboundedChannelWaiter(isPooled: true);

        public bool TryAdd(T item)
        {
            SyncUnboundedChannelWaiter? tail;

            // 1. 최소한의 락(Lock): 큐에 넣고 대기열만 뜯어옵니다.
            lock (_syncObj)
            {
                if (_isCompleted)
                {
                    return false;
                }

                _items.Enqueue(item);
                tail = _waitingReadersTail;
                if (tail is null)
                {
                    return true;
                }

                _waitingReadersTail = null;
            }

            WakeUpWaiters(tail, true);

            return true;
        }

        public void Complete()
        {
            SyncUnboundedChannelWaiter? tail;
            lock (_syncObj)
            {
                _isCompleted = true;
                tail = _waitingReadersTail;
                if (tail is null)
                {
                    return;
                }

                _waitingReadersTail = null;
            }

            WakeUpWaiters(tail, false);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WakeUpWaiters(SyncUnboundedChannelWaiter? tail, bool result)
        {
            do
            {
                var next = tail!.Next;
                tail.Next = null; // help gc
                tail.Pulse(result);
                tail = next;
            } while (!(tail is null));
        }

        public bool WaitToRead()
        {
            // Fast-Path
            if (!_items.IsEmpty)
            {
                return true;
            }

            SyncUnboundedChannelWaiter waiter;
            lock (_syncObj)
            {
                if (!_items.IsEmpty)
                {
                    return true;
                }

                if (_isCompleted)
                {
                    return false;
                }

                // 1. 먼저 singleton 사용
                if (_waiterSingleton.TryOwnAndReset())
                {
                    waiter = _waiterSingleton;
                }
                else
                {
                    // 2. 없으면 새로운 대기 객체 생성
                    waiter = new SyncUnboundedChannelWaiter();
                }

                waiter.Next = _waitingReadersTail;
                _waitingReadersTail = waiter;
            }

            return waiter.Wait();
        }

        public bool WaitToRead(int millisecondsTimeout)
        {
            if (millisecondsTimeout <= -1)
            {
                if (millisecondsTimeout == Timeout.Infinite)
                {
                    return WaitToRead();
                }

                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));
            }

            if (!_items.IsEmpty)
            {
                return true;
            }

            if (millisecondsTimeout == 0)
            {
                return false;
            }

            SyncUnboundedChannelWaiter waiter;
            lock (_syncObj)
            {
                if (!_items.IsEmpty)
                {
                    return true;
                }

                if (_isCompleted)
                {
                    return false;
                }

                if (_waiterSingleton.TryOwnAndReset())
                {
                    waiter = _waiterSingleton;
                }
                else
                {
                    waiter = new SyncUnboundedChannelWaiter();
                }

                waiter.Next = _waitingReadersTail;
                _waitingReadersTail = waiter;
            }

            return waiter.WaitTimeout(millisecondsTimeout);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryTake([MaybeNullWhen(false)] out T item)
        {
            return _items.TryDequeue(out item);
        }
    }

    internal class SyncUnboundedChannelWaiter
    {
        private const int State_InUse = 0;
        private const int State_Available = 1;
        private const int State_Completed = 2;
        private const int State_TimedOut = 3;

        private int _state;

        /// <summary>
        /// 값이 있는지 결과 값
        /// 기본적으로 항상 true 지만
        /// Timeout 이 발생하거나 Complete 를 호출하거나 했을때는 false 
        /// </summary>
        private bool _result;

        public readonly bool IsPooled;
        public SyncUnboundedChannelWaiter? Next;

        public SyncUnboundedChannelWaiter(bool isPooled = false)
        {
            IsPooled = isPooled;
            // 풀링된 객체(싱글톤)만 Available(1) 상태로 시작
            if (isPooled)
            {
                _state = State_Available;
            }
            else
            {
                _state = State_InUse;
            }
        }

        // 풀링 객체 전용
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryOwnAndReset()
        {
            if (Interlocked.CompareExchange(ref _state, State_InUse, State_Available) == State_Available)
            {
                Next = null;
                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Pulse(bool result)
        {
            lock (this)
            {
                _result = result;
                if (_state == State_TimedOut)
                {
                    // 이미 대기 안하는중임
                    ReturnToPoolUnsafe();
                    return;
                }

                _state = State_Completed;
                Monitor.Pulse(this);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Wait()
        {
            bool result;
            lock (this)
            {
                try
                {
                    while (_state != State_Completed)
                    {
                        Monitor.Wait(this);
                    }
                }
                catch
                {
                    ExcptionCompleteUnsafe();
                    throw;
                }

                result = _result;
            }

            ReturnToPoolUnsafe();
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool WaitTimeout(int millisecondsTimeout)
        {
#if NETCOREAPP3_0_OR_GREATER
            var startTickCount = Environment.TickCount64;
#else
            var startTickCount = Environment.TickCount;
#endif
            var remainTimeout = millisecondsTimeout;
            lock (this)
            {
                try
                {
                    while (_state != State_Completed)
                    {
                        if (!Monitor.Wait(this, remainTimeout))
                        {
                            _state = State_TimedOut;
                            return false; // 타임아웃
                        }

                        remainTimeout = UpdateTimeOut(startTickCount, millisecondsTimeout);

                        if (remainTimeout <= 0)
                        {
                            _state = State_TimedOut;
                            return false; // 타임아웃
                        }
                    }
                }
                catch
                {
                    ExcptionCompleteUnsafe();
                    throw;
                }
            }

            ReturnToPoolUnsafe();
            return true;
        }

        private void ExcptionCompleteUnsafe()
        {
            if (_state == State_Completed)
            {
                // 완료처리해서 pool 에만 반납함.
                ReturnToPoolUnsafe();
                return;
            }

            // interrupt 발생 시 타임아웃과 동일하게 처리.
            _state = State_TimedOut;
            return;
        }

        // 대기 완료 후 풀링된 객체는 다시 Available(1) 상태로 반납
        private void ReturnToPoolUnsafe()
        {
            if (IsPooled)
            {
                Volatile.Write(ref _state, State_Available);
            }
        }

#if NETCOREAPP3_0_OR_GREATER
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int UpdateTimeOut(long startTickCount, int originalWaitMillisecondsTimeout)
        {
            Debug.Assert(originalWaitMillisecondsTimeout != Timeout.Infinite);

            var elapsedMilliseconds = unchecked(Environment.TickCount64 - startTickCount);

            // 이미 시간이 다 지났거나 오버플로로 인해 계산값이 타임아웃을 초과한 경우
            if (elapsedMilliseconds >= originalWaitMillisecondsTimeout || elapsedMilliseconds < 0)
            {
                return 0;
            }

            return (int)(originalWaitMillisecondsTimeout - elapsedMilliseconds);
        }
#else
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int UpdateTimeOut(int startTickCount, int originalWaitMillisecondsTimeout)
        {
            Debug.Assert(originalWaitMillisecondsTimeout != Timeout.Infinite);

            // unchecked 컨텍스트에서는 (현재값 - 시작값) 계산 시 
            // 24.9일 오버플로가 발생해도 경과 시간은 올바르게 양수로 계산됩니다.
            var elapsedMilliseconds = unchecked(Environment.TickCount - startTickCount);

            // 이미 시간이 다 지났거나 오버플로로 인해 계산값이 타임아웃을 초과한 경우
            if (elapsedMilliseconds >= originalWaitMillisecondsTimeout || elapsedMilliseconds < 0)
            {
                return 0;
            }

            return originalWaitMillisecondsTimeout - elapsedMilliseconds;
        }
#endif
    }
}