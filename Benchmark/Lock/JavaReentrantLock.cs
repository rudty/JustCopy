#pragma warning disable 
namespace JustCopy
{
    using System;
    using System.Collections.Concurrent;
    using System.Runtime.InteropServices;
    using System.Threading;

    /// <summary>
    /// Java의 java.util.concurrent.locks.ReentrantLock
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public sealed class JavaReentrantLock
    {
        // -------------------------------------------------------------------
        // 2. AQS Node (CHL Queue)
        // -------------------------------------------------------------------
        [StructLayout(LayoutKind.Sequential)]
        private sealed class Node
        {
            public volatile Node Prev;
            public volatile Node Next;
            public volatile int WaitStatus; // -1: SIGNAL, 0: INITIAL
            private int _permit;

            private readonly CacheLinePadding padding0;
            public volatile Node NextFree; // 풀링(Stack)을 위한 포인터
            public void Park()
            {
                lock (this)
                {
                    // Permit이 이미 발급되었다면 소진하고 대기 없이 패스
                    if (_permit == 1)
                    {
                        _permit = 0;
                        return;
                    }

                    Monitor.Wait(this);
                    _permit = 0; // 깨어나면 Permit 소진
                }
            }
            public void Unpark()
            {
                lock (this)
                {
                    _permit = 1;
                    Monitor.Pulse(this);
                }
            }

            public void Reset()
            {
                Prev = null;
                Next = null;
                NextFree = null;
                WaitStatus = 0;
                Volatile.Write(ref _permit, 0);
            }
        }

        [StructLayout(LayoutKind.Explicit, Size = 64)]
        private struct CacheLinePadding
        {
        }

        // -------------------------------------------------------------------
        // 3. AQS 상태 및 변수
        // -------------------------------------------------------------------
        private volatile int _state;
        private Thread _owner;
        private CacheLinePadding _padding1;
        private volatile Node _head;
        private volatile Node _tail;
        private CacheLinePadding _padding2;
        private volatile Node _freeStackHead; // ConcurrentStack 대신 사용

        private void PushToPool(Node node)
        {
            node.Reset(); // 깨끗하게 씻어서 넣기
            Node oldHead;
            int spinCount = 0;
            do
            {
                oldHead = _freeStackHead;
                node.NextFree = oldHead;
                if (spinCount > 10)
                    Thread.SpinWait(spinCount); // 경합이 심하면 양보
                spinCount++;
            } while (Interlocked.CompareExchange(ref _freeStackHead, node, oldHead) != oldHead);
        }

        private Node PopFromPool()
        {
            Node oldHead;
            Node newHead;
            do
            {
                oldHead = _freeStackHead;
                if (oldHead == null)
                {
                    return new Node(); // 풀이 비었으면 생성
                }

                newHead = oldHead.NextFree;
            } while (Interlocked.CompareExchange(ref _freeStackHead, newHead, oldHead) != oldHead);

            oldHead.NextFree = null; // 꺼낸 노드 초기화
            return oldHead;
        }

        // -------------------------------------------------------------------
        // 4. Lock / Unlock (NonFair)
        // -------------------------------------------------------------------
        public void Lock()
        {
            // NonFair Fast Path: 큐에 대기자가 있어도 새치기(Barging)를 시도!
            if (Interlocked.CompareExchange(ref _state, 1, 0) == 0)
            {
                _owner = Thread.CurrentThread;
                return;
            }
            Acquire(1);
        }

        public void Unlock()
        {
            Release(1);
        }

        // -------------------------------------------------------------------
        // 5. AQS 코어 로직
        // -------------------------------------------------------------------
        private void Acquire(int arg)
        {
            if (!TryAcquire(arg))
            {
                Node node = AddWaiter();
                AcquireQueued(node, arg);
            }
        }

        private bool TryAcquire(int acquires)
        {
            Thread current = Thread.CurrentThread;
            int c = _state;

            if (c == 0)
            {
                // NonFair: 큐를 무시하고 즉시 락 탈취 시도
                if (Interlocked.CompareExchange(ref _state, acquires, 0) == 0)
                {
                    _owner = current;
                    return true;
                }
            }
            else if (_owner == current)
            {
                // 재진입 (Reentrancy) 처리
                _state = c + acquires;
                return true;
            }
            return false;
        }

        private Node AddWaiter()
        {
            var node = PopFromPool();

            while (true)
            {
                Node t = _tail;
                if (t != null)
                {
                    node.Prev = t;
                    if (Interlocked.CompareExchange(ref _tail, node, t) == t)
                    {
                        t.Next = node;
                        return node;
                    }
                }
                else
                {
                    // 큐 초기화 (Dummy Head 생성)
                    var h = PopFromPool();
                    if (Interlocked.CompareExchange(ref _head, h, null) == null)
                    {
                        _tail = _head;
                    }
                    else
                    {
                        // 다른 스레드가 먼저 더미를 만들었다면, 내가 꺼낸 건 다시 넣습니다.
                        PushToPool(h);
                    }
                }
            }
        }

        private void AcquireQueued(Node node, int arg)
        {
            try
            {
                while (true)
                {
                    Node p = node.Prev;

                    // 내 앞사람(Prev)이 Head라면, 이제 내 차례일 수 있으니 다시 TryAcquire 시도
                    if (p == _head && TryAcquire(arg))
                    {
                        _head = node;
                        node.Prev = null;
                        p.Next = null; // Help GC
                        
                        //-- gc
                        
                        PushToPool(p);
                        return;
                    }

                    // 락 획득 실패 시 파킹(수면) 해야 하는지 확인
                    if (ShouldParkAfterFailedAcquire(p))
                    {
                        node.Park(); // 여기서 스레드 블로킹 (OS 커널 모드)
                    }
                }
            }
            catch
            {
                throw;
            }
        }

        private bool ShouldParkAfterFailedAcquire(Node pred)
        {
            int ws = pred.WaitStatus;
            if (ws == -1) // SIGNAL: 앞 노드가 나를 깨워주기로 약속함. 안심하고 자면 됨.
                return true;

            // SIGNAL 세팅 (다음 바퀴에서 true를 반환하며 파킹됨)
            if (ws == 0)
            {
                Interlocked.CompareExchange(ref pred.WaitStatus, -1, 0);
            }
            return false;
        }

        private void Release(int arg)
        {
            if (TryRelease(arg))
            {
                Node h = _head;
                if (h != null && h.WaitStatus != 0)
                {
                    UnparkSuccessor(h);
                }
            }
        }

        private bool TryRelease(int releases)
        {
            int c = _state - releases;
            if (Thread.CurrentThread != _owner)
                throw new SynchronizationLockException("Current thread does not own the lock.");

            bool free = false;
            if (c == 0)
            {
                free = true;
                _owner = null;
            }
            _state = c;
            return free;
        }

        private void UnparkSuccessor(Node node)
        {
            int ws = node.WaitStatus;
            if (ws < 0)
                Interlocked.CompareExchange(ref node.WaitStatus, 0, ws);

            Node s = node.Next;
            if (s == null || s.WaitStatus > 0)
            {
                s = null;
                // 꼬리에서부터 앞으로 탐색하며 깨울 노드를 찾음 (AQS 특유의 큐 무결성 보장 로직)
                for (Node t = _tail; t != null && t != node; t = t.Prev)
                    if (t.WaitStatus <= 0)
                        s = t;
            }

            if (s != null)
            {
                s.Unpark(); // 다음 타자 기상!
            }
        }
    }
}