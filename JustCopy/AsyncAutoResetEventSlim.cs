namespace JustCopy
{

    using System;
    using System.Diagnostics;
    using System.Runtime.InteropServices;
    using System.Threading.Tasks;
    using System.Threading.Tasks.Sources;

    [StructLayout(LayoutKind.Sequential)]
    public sealed class AsyncAutoResetEventSlim
    {
        [StructLayout(LayoutKind.Explicit, Size = 64)]
        private struct CacheLinePadding
        {
        }

        private const int PoolSize = 32;

#if NET10_0_OR_GREATER
        private readonly Lock lockObject = new();
#else
        private readonly object lockObject = new object();
#endif

        private WaitNode? headNode;
        private WaitNode? tailNode;
#pragma warning disable IDE0051
#pragma warning disable CS0169
        private readonly CacheLinePadding padding0;

        private readonly WaitNode?[] waitNodePool;
        private int waitNodePoolCount;

        private readonly CacheLinePadding padding1;
        private volatile bool isSet;
        private readonly CacheLinePadding padding2;
#pragma warning restore CS0169
#pragma warning restore IDE0051
        public AsyncAutoResetEventSlim(bool initialState)
        {
            isSet = initialState;
            waitNodePool = new WaitNode?[PoolSize];
        }

        public void Set()
        {
            WaitNode? nodeToWake;

            lock (lockObject)
            {
                nodeToWake = headNode;
                if (nodeToWake is null)
                {
                    isSet = true;
                    return;
                }

                // enqueue pool
                var nextNode = nodeToWake.Next;
                headNode = nextNode;
                if (!(nextNode is null))
                {
                    nextNode.Prev = null;
                }
                else
                {
                    tailNode = null;
                }

                // gc help
                nodeToWake.Next = null;
                nodeToWake.Prev = null;
            }

            try
            {
                nodeToWake.SetResult(true);
            }
            catch
            {
                // ignore
            }
        }

        public ValueTask<bool> WaitOneAsync(int millisecondsTimeout = -1)
        {
            if (millisecondsTimeout == 0)
            {
                return new ValueTask<bool>(false);
            }

            lock (lockObject)
            {
                if (isSet)
                {
                    isSet = false;
                    return new ValueTask<bool>(true);
                }

                WaitNode? node;

                // dequeue pool
                var poolCount = waitNodePoolCount - 1;
                if (poolCount >= 0)
                {
                    waitNodePoolCount = poolCount;
                    node = waitNodePool[poolCount];
                    waitNodePool[poolCount] = null;
                }
                else
                {
                    node = new WaitNode(this);
                }

                Debug.Assert(!(node is null), "node is not null");

                node.Reset();
                var valueTask = new ValueTask<bool>(node, node.UnsafeVersion);

                // enqueue tail
                var tail = tailNode;
                if (tail is null)
                {
                    headNode = node;
                    tailNode = node;
                }
                else
                {
                    tail.Next = node;
                    node.Prev = tail;
                    tailNode = node;
                }

                if (millisecondsTimeout == -1)
                {
                    return valueTask;
                }

                return WaitWithTimeoutAsync(node, valueTask, millisecondsTimeout);
            }
        }

        private async ValueTask<bool> WaitWithTimeoutAsync(WaitNode node, ValueTask<bool> originalTask, int timeoutMillis)
        {
            var waitNodeVersion = node.UnsafeVersion;
            try
            {
                var task = originalTask.AsTask();
#if NET8_0_OR_GREATER
                return await task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMillis)).ConfigureAwait(false);
#else
                var delayTask = Task.Delay(timeoutMillis);
                var completedTask = await Task.WhenAny(task, delayTask).ConfigureAwait(false);
                if (completedTask == task)
                {
                    // 원래 작업이 완료된 경우 결과 반환
                    return await task.ConfigureAwait(false);
                }

                WaitTimeoutException(node, waitNodeVersion);
                return false;
#endif
            }
            catch (TimeoutException)
            {

                WaitTimeoutException(node, waitNodeVersion);
                return false;
            }
        }

        private void WaitTimeoutException(WaitNode node, short waitNodeVersion)
        {
            lock (lockObject)
            {
                // 이미 다른곳에서 Set 들어갔으면 waitNodeVersion != timeoutWaitNodeVersion 임
                var timeoutWaitNodeVersion = node.UnsafeVersion;
                if (timeoutWaitNodeVersion == waitNodeVersion &&
                    !node.IsCompleted)
                {
                    if (!(node.Next is null))
                    {
                        node.Next.Prev = node.Prev;
                    }
                    else if (tailNode == node)
                    {
                        tailNode = node.Prev;
                    }

                    if (!(node.Prev is null))
                    {
                        node.Prev.Next = node.Next;
                    }
                    else if (headNode == node)
                    {
                        headNode = node.Next;
                    }

                    // 연결선 초기화
                    node.Next = null;
                    node.Prev = null;

                    // 위에서 Task 로 변경했으므로 따로 처리하지 않아도됨
                    node.SetResult(false);
                }
            }
        }

        private void ReturnNode(WaitNode node)
        {
            lock (lockObject)
            {
                var poolCount = waitNodePoolCount;
                if (poolCount < PoolSize)
                {
                    waitNodePool[poolCount] = node;
                    poolCount += 1;
                    waitNodePoolCount = poolCount;
                }
            }
        }

        private sealed class WaitNode : IValueTaskSource<bool>
        {
            private ManualResetValueTaskSourceCore<bool> _core;
            private readonly AsyncAutoResetEventSlim _parent;

            public WaitNode? Prev;
            public WaitNode? Next;

            public WaitNode(AsyncAutoResetEventSlim parent)
            {
                _parent = parent;
                _core = new ManualResetValueTaskSourceCore<bool>
                {
                    RunContinuationsAsynchronously = false
                };
            }

            public bool IsCompleted => _core.GetStatus(_core.Version) != ValueTaskSourceStatus.Pending;

            public short UnsafeVersion => _core.Version;

            public void Reset()
            {
                _core.Reset();
            }

            public void SetResult(bool result)
            {
                _core.SetResult(result);
            }

            public bool GetResult(short token)
            {
                try
                {
                    return _core.GetResult(token);
                }
                finally
                {
                    _parent.ReturnNode(this);
                }
            }

            public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

            public void OnCompleted(
                Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            {
                _core.OnCompleted(continuation, state, token, flags);
            }
        }
    }
}