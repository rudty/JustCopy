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
    using System.Diagnostics;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// MpscMessageLooper: MPSC Producer + Single Consumer Orchestrator with Cancellation
    /// MPSC(다중 생산자, 단일 소비자) 큐를 기반으로 하는 고성능 워커 루프의 추상 기본 클래스입니다.
    /// </summary>
    /// <typeparam name="T">처리할 메시지 또는 항목의 타입입니다.</typeparam>
    public abstract class MpscMessageLooper<T>
    {
        private readonly MpscAsyncLinkedQueue<T> taskQueue = new MpscAsyncLinkedQueue<T>();
        private readonly RecycleTask task = new RecycleTask();

        /// <summary>
        /// Producer method: 큐에 항목을 추가하고 Consumer에게 신호를 보냅니다.
        /// </summary>
        public void Add(T value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            taskQueue.Enqueue(value);

            // 대기 중인 Consumer에게 신호를 보냄
            task.Complete();
        }

        /// <summary>
        /// 큐에서 꺼낸 항목을 처리하는 로직을 구현합니다. (필수 구현)
        /// </summary>
        public abstract void OnItemReceived(T item);

        /// <summary>
        /// OnItemReceived에서 예외가 발생했을 때 호출됩니다. (선택적 오버라이드)
        /// </summary>
        public virtual void OnException(T item, Exception exception)
        {
            Debug.WriteLine($"MpscMessageLooper OnException Item:{item} Exception:{exception}");
        }

        /// <summary>
        /// Consumer 루프를 시작하고 큐 항목이 도착할 때까지 효율적으로 대기합니다.
        /// </summary>
        public virtual async Task Start(
            CancellationToken cancellation,
            bool schedulingContext = true,
            bool captureExecutionContext = true)
        {
            // Awaiter 실행 환경 설정
            task.RegisterConsumerMessageExecutor(schedulingContext, captureExecutionContext);

            using (var registration = cancellation.Register(t => (t as RecycleTask).Complete(), task))
            {
                _ = registration; // ignore warn
                while (true)
                {
                    // 1. 큐의 모든 항목을 처리하는 고속 경로
                    while (taskQueue.TryDequeue(out var v1))
                    {
                        // 항목 처리 로직 실행
                        ExecuteItemHandler(v1);
                    }

                    // 2. 대기 상태로 전환 준비 (RecycleTask.signaled = 0)
                    task.Reset();

                    // 2-1. 취소 요청 시 종료
                    if (cancellation.IsCancellationRequested)
                    {
                        break;
                    }

                    // 3. 방어적인 재확인 (Lost Wakeup 방어): Reset()과 await task 사이에 도착한 항목이 있는지 확인
                    if (taskQueue.TryDequeue(out var v2))
                    {
                        ExecuteItemHandler(v2);

                        // 항목이 있다면 잠들지 않고 다시 1단계로 돌아가 처리
                        continue;
                    }

                    // 4. 대기: 큐가 비었고, 신호 누락이 없음을 확인했을 때만 잠듦

                    if (task.IsCompleted)
                    {
                        continue;
                    }

                    await task;
                }
            }

            // 5. 종료 전 남은 항목 처리
            while (taskQueue.TryDequeue(out var v3))
            {
                ExecuteItemHandler(v3);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ExecuteItemHandler(T item)
        {
            try
            {
                OnItemReceived(item);
            }
            catch (Exception e)
            {
                try
                {
                    // 사용자 정의 예외 처리 메서드 호출
                    OnException(item, e);
                }
                catch
                {
                    // OnException 메서드가 던진 예외는 무시하고 루프를 유지
                }
            }
        }
    }

    // ======================================================================
    // Execution Context Handling (MessageExecutor Hierarchy)
    // ======================================================================
    internal abstract class MessageExecutorBase
    {
        private ExecutionContext executionContext;

        public void CaptureExecutionContext()
        {
            executionContext = ExecutionContext.Capture();
        }

        public void Run(Action c)
        {
            RunInternal(ExecuteContinuationWithContext, c);
        }

        protected abstract void RunInternal(Action<object> action, object state);

        private void ExecuteContinuationWithContext(object c)
        {
            if (!(executionContext is null))
            {
                // ExecutionContext.Run을 사용하여 캡처된 컨텍스트 내에서 Continuation 실행
                ExecutionContext.Run(executionContext, RunState, c);
            }
            else
            {
                // 컨텍스트가 캡처되지 않았거나 null인 경우, 직접 실행
                RunState(c);
            }
        }

        private static void RunState(object state)
        {
            var action = state as Action;
            action.Invoke();
        }
    }

    internal class TaskSchedulerMessageExecutor : MessageExecutorBase
    {
        private readonly TaskScheduler scheduler;

        public TaskSchedulerMessageExecutor(TaskScheduler scheduler)
        {
            this.scheduler = scheduler ?? 
                             throw new ArgumentNullException(nameof(scheduler));
        }

        protected override void RunInternal(Action<object> action, object state)
        {
            // TaskScheduler를 사용하여 Continuation 실행
            Task.Factory.StartNew(
                action,
                state,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                scheduler);
        }
    }

    internal class SynchronizationContextMessageExecutor : MessageExecutorBase
    {
        private readonly SynchronizationContext synchronizationContext;

        public SynchronizationContextMessageExecutor(SynchronizationContext synchronizationContext)
        {
            this.synchronizationContext = synchronizationContext ??
                                          throw new ArgumentNullException(nameof(synchronizationContext));
        }

        protected override void RunInternal(Action<object> action, object state)
        {
            // SynchronizationContext.Post를 사용하여 Continuation 실행 (UI/Single Thread Context)
            synchronizationContext.Post(action.Invoke, state);
        }
    }

    internal class ThreadPoolMessageExecutor : MessageExecutorBase
    {
        protected override void RunInternal(Action<object> action, object state)
        {
            // ThreadPool에서 Continuation 실행 (default ConfigureAwait(false) 효과)
            ThreadPool.UnsafeQueueUserWorkItem(action.Invoke, state);
        }
    }

    internal class NotImplementsMessageExecutor : MessageExecutorBase
    {
        public static readonly NotImplementsMessageExecutor Instance = new NotImplementsMessageExecutor();

        protected override void RunInternal(Action<object> action, object state)
        {
            throw new NotImplementedException("Message Executor is not implemented.");
        }
    }

    internal sealed class RecycleTask : ICriticalNotifyCompletion
    {
        // 0 = not signaled, 1 = signaled
        private int signaled;
        private volatile Action next;
        private bool captureContext = true;
        private MessageExecutorBase messageExecutor = NotImplementsMessageExecutor.Instance;

        public void RegisterConsumerMessageExecutor(bool schedulingContext, bool captureExecutionContext)
        {
            if (schedulingContext)
            {
                if (SynchronizationContext.Current is SynchronizationContext sc)
                {
                    messageExecutor = new SynchronizationContextMessageExecutor(sc);
                }
                else
                {
                    messageExecutor = new TaskSchedulerMessageExecutor(TaskScheduler.Current);
                }
            }
            else
            {
                messageExecutor = new ThreadPoolMessageExecutor();
            }

            captureContext = captureExecutionContext;
        }

        public bool IsCompleted => Volatile.Read(ref signaled) == 1;
        public RecycleTask GetAwaiter() => this;

        public void GetResult()
        {
        } // 결과가 없는 Task이므로 비워둡니다.

        /// <summary>
        /// Task를 완료(신호)하고 대기 중인 Continuation을 실행합니다.
        /// </summary>
        public void Complete()
        {
            // 0 -> 1로 상태를 변경하고, 이전에 0이었을 때만 InvokeNext 호출
            if (Interlocked.Exchange(ref signaled, 1) == 0)
            {
                InvokeNext();
            }
        }

        /// <summary>
        /// 다음 Continuation을 실행하며, 캡처된 ExecutionContext를 사용합니다.
        /// 이 메서드는 'signaled'가 1로 설정된 후에만 호출되어야 합니다.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void InvokeNext()
        {
            // 등록된 Action을 가져오고 'next' 슬롯을 비웁니다.
            var c = Interlocked.Exchange(ref next, null);

            if (c is null)
            {
                return;
            }

            ExecuteContinuationWithContext(c);
        }

        /// <summary>
        /// 주어진 Action을 캡처된 ExecutionContext 내에서 실행합니다.
        /// InvokeNext 및 OnCompleted의 모든 동기 실행 경로에서 사용됩니다.
        /// </summary>
        private void ExecuteContinuationWithContext(Action c)
        {
            Debug.Assert(!(c is null), "ExecuteContinuationWithContext action is not null");

            messageExecutor.Run(c);
        }

        public void OnCompleted(Action continuation)
        {

            if (captureContext)
            {
                messageExecutor.CaptureExecutionContext();
            }

            // 1. Fast Path: 이미 완료(signaled == 1)되었는지 확인
            // 만약 이미 완료되었다면, Continuation을 'ExecuteContinuationWithContext'로 즉시 실행합니다.
            if (Volatile.Read(ref signaled) == 1)
            {
                ExecuteContinuationWithContext(continuation); // 수정: 컨텍스트 흐름 보장
                return;
            }

            // 2. 슬롯 확보/대기자 등록
            // 단일 대기자(Single Awaiter) 패턴을 가정합니다.
            var c = Interlocked.CompareExchange(ref next, continuation, null);
            if (!(c is null))
            {
                // 2-1. 등록 실패: 이미 대기자가 존재함 (Contention)
                // 단일 대기자만 지원하므로, 이 경우는 비정상 상황이거나 Lost Wakeup Case 1에 해당됩니다.
                // 일관성을 위해 continuation을 사용하여 호출자의 continuation을 실행합니다.
                ExecuteContinuationWithContext(continuation);
                return;
            }

            // 3. Lost Wakeup Check: 등록 후, 신호가 들어왔는지 다시 확인 (Signal 경합)
            if (Volatile.Read(ref signaled) == 1)
            {
                // Task가 등록과 동시에 완료되었으므로, 등록된 Continuation을 실행합니다.
                InvokeNext(); // InvokeNext는 이미 ExecuteContinuationWithContext를 사용합니다.
            }
            // Task가 아직 완료되지 않은 경우, 'next'에 저장되어 Complete()를 기다립니다.
        }

        // ICriticalNotifyCompletion 구현 (Execution Context를 캡처했더라도 OnCompleted와 동일하게 처리)
        public void UnsafeOnCompleted(Action continuation)
        {
            OnCompleted(continuation);
        }

        /// <summary>
        /// Task를 재사용을 위해 초기화합니다.
        /// next와 executionContext를 해제하여 메모리 누수를 방지합니다.
        /// </summary>
        public void Reset()
        {
            Volatile.Write(ref signaled, 0);
            // 메모리 해제 및 다음 사용을 위한 초기화
            next = null;
        }
    }

    internal sealed class MpscAsyncLinkedQueueNode<T>
    {
        internal T item;
        internal volatile MpscAsyncLinkedQueueNode<T> next;

        internal MpscAsyncLinkedQueueNode(T item, MpscAsyncLinkedQueueNode<T> next)
        {
            this.item = item;
            this.next = next;
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct MpscAsyncLinkedQueueCacheLinePadding
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    internal sealed class MpscAsyncLinkedQueue<T>
    {
        // https://www.1024cores.net/home/lock-free-algorithms/queues/non-intrusive-mpsc-node-based-queue
        private static readonly bool IsReferenceOrContainsReferences =
#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1
        RuntimeHelpers.IsReferenceOrContainsReferences<T>();
#else
            typeof(T).IsClass || typeof(T).IsInterface;
#endif
#pragma warning disable IDE0051
#pragma warning disable CS0169
        private MpscAsyncLinkedQueueCacheLinePadding padding0;
        private volatile MpscAsyncLinkedQueueNode<T> head;
        private MpscAsyncLinkedQueueCacheLinePadding padding1;
        private MpscAsyncLinkedQueueNode<T> tail;
        private MpscAsyncLinkedQueueCacheLinePadding padding2;
#pragma warning restore CS0169
#pragma warning restore IDE0051

        public MpscAsyncLinkedQueue()
        {
            head = tail = new MpscAsyncLinkedQueueNode<T>(
#pragma warning disable CS8604
                default,
#pragma warning restore CS8604
                null);
        }

        internal void Enqueue(T item)
        {
            var newNode = new MpscAsyncLinkedQueueNode<T>(item, null);
            var prevHead = Interlocked.Exchange(ref head, newNode);
            prevHead.next = newNode;
        }

        internal bool TryDequeue(
#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1
            [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] 
#endif
            out T item)
        {
            var currentNext = tail.next;
            if (!(currentNext is null))
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
