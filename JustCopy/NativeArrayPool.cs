#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
#nullable enable
#endif

namespace JustCopy
{
    using System;
    using System.Diagnostics;
    using System.Runtime.CompilerServices;
    using System.Runtime.ConstrainedExecution;
    using System.Runtime.InteropServices;
    using System.Threading;

    public struct NativeBuffer
    {
        public IntPtr Pointer;
        public int Capacity;

        public NativeBuffer(IntPtr pointer, int capacity)
        {
            Pointer = pointer;
            Capacity = capacity;
        }

        public static readonly NativeBuffer Empty = new NativeBuffer(IntPtr.Zero, 0);
    }

    public sealed class NativeArrayPool
    {
        /// <summary>The number of buckets (array sizes) in the pool, one for each array length, starting from length 16.</summary>
        private const int NumBuckets = 27; // Utilities.SelectBucketIndex(1024 * 1024 * 1024 + 1)

        /// <summary>A per-thread array of arrays, to cache one array per array size per thread.</summary>
#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
        [ThreadStatic] private static ThreadLocalNativeArrayBuckets? t_tlsBuckets;
#else
        [ThreadStatic] private static ThreadLocalNativeArrayBuckets t_tlsBuckets;
#endif

        /// <summary>
        /// An array of per-core partitions. The slots are lazily initialized to avoid creating
        /// lots of overhead for unused array sizes.
        /// </summary>
#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
        private static readonly NativeArrayPoolPartitions?[] _buckets = new NativeArrayPoolPartitions[NumBuckets];
#else
        private static readonly NativeArrayPoolPartitions[] _buckets = new NativeArrayPoolPartitions[NumBuckets];
#endif

        /// <summary>Allocate a new <see cref="NativeArrayPoolPartitions"/> and try to store it into the <see cref="_buckets"/> array.</summary>
        private static NativeArrayPoolPartitions CreatePerCorePartitions(int bucketIndex)
        {
            var inst = new NativeArrayPoolPartitions();
            return Interlocked.CompareExchange(ref _buckets[bucketIndex], inst, null) ?? inst;
        }

        public static void ForceUseTlsCurrentThread()
        {
            var tlsBuckets = t_tlsBuckets;
            if (tlsBuckets is null)
            {
                InitializeTlsBuckets();
            }
        }

        public static void ForceReleaseTlsCurrentThread()
        {
            var tlsBuckets = t_tlsBuckets;
            if (tlsBuckets != null)
            {
                tlsBuckets.Dispose();
                t_tlsBuckets = null;
            }
        }

        public static void ShutdownUnityEditor()
        {
#if UNITY_EDITOR
            Utilities.PurgeAll(); 
#endif
        }

        public static NativeBuffer Rent(int minimumLength)
        {
            // Validate negative inputs early.
            ArgumentOutOfRangeExceptionNetCoreHelper.ThrowIfNegative(minimumLength, nameof(minimumLength));

            IntPtr buffer;

            // Get the bucket number for the array length. The result may be out of range of buckets,
            // either for too large a value or for 0 and negative values.
            var bucketIndex = Utilities.SelectBucketIndex(minimumLength);

            // First, try to get an array from TLS if possible.
            var tlsBuckets = t_tlsBuckets;
            if (tlsBuckets != null && (uint)bucketIndex < (uint)tlsBuckets.Length)
            {
                buffer = tlsBuckets.Buckets[bucketIndex];
                if (buffer != IntPtr.Zero)
                {
                    tlsBuckets.Buckets[bucketIndex] = IntPtr.Zero;
                    var capacity = Utilities.GetMaxSizeForBucket(bucketIndex);
                    return new NativeBuffer(buffer, capacity);
                }
            }

            // Next, try to get an array from one of the partitions.
            var perCoreBuckets = _buckets;
            if ((uint)bucketIndex < (uint)perCoreBuckets.Length)
            {
                var b = perCoreBuckets[bucketIndex];
                if (b != null)
                {
                    buffer = b.TryPop();
                    if (buffer != IntPtr.Zero)
                    {
                        // The buffer popped from the partition will have the bucket's capacity.
                        var capacity = Utilities.GetMaxSizeForBucket(bucketIndex);
                        return new NativeBuffer(buffer, capacity);
                    }
                }

                // No buffer available.  Ensure the length we'll allocate matches that of a bucket
                // so we can later return it.
                minimumLength = Utilities.GetMaxSizeForBucket(bucketIndex);
            }
            else if (minimumLength == 0)
            {
                // We allow requesting zero-length arrays (even though pooling such an array isn't valuable)
                // as it's a valid length array, and we want the pool to be usable in general instead of using
                // `new`, even for computed lengths. But, there's no need to log the empty array.  Our pool is
                // effectively infinite for empty arrays and we'll never allocate for rents and never store for returns.
                return NativeBuffer.Empty;
            }
            else
            {
                ArgumentOutOfRangeExceptionNetCoreHelper.ThrowIfNegative(minimumLength, nameof(minimumLength));
            }

            // For large arrays, we prefer to avoid the zero-initialization costs. However, as the resulting
            // arrays could end up containing arbitrary bit patterns, we only allow this for types for which
            // every possible bit pattern is valid.
            buffer = Utilities.Alloc(minimumLength);
            return new NativeBuffer(buffer, minimumLength);
        }

        public static void Return(NativeBuffer array, bool clearArray = false)
        {
            // Allow returning the Empty marker (Pointer == IntPtr.Zero && Capacity == 0) as a no-op.
            if (array.Pointer == IntPtr.Zero)
            {
                if (array.Capacity == 0)
                {
                    return;
                }

                throw new ArgumentNullException(nameof(array));
            }

            // Determine with what bucket this array length is associated
            var bucketIndex = Utilities.SelectBucketIndex(array.Capacity);
            var tlsBuckets = t_tlsBuckets;
            if (tlsBuckets is null && !Thread.CurrentThread.IsThreadPoolThread)
            {
                PushToPartition(bucketIndex, array.Pointer);
                return;
            }

            // Make sure our TLS buckets are initialized.  Technically we could avoid doing
            // this if the array being returned is erroneous or too large for the pool, but the
            // former condition is an error we don't need to optimize for, and the latter is incredibly
            // rare, given a max size of 1B elements.
            if (tlsBuckets is null)
            {
                tlsBuckets = InitializeTlsBuckets();
            }

            if ((uint)bucketIndex >= (uint)tlsBuckets.Length)
            {
                Utilities.Free(array.Pointer);
                return;
            }

            // Clear the array if the user requested it.
            if (clearArray)
            {
                unsafe
                {
                    new Span<byte>(array.Pointer.ToPointer(), array.Capacity).Clear();
                }
            }

            // Check to see if the buffer is the correct size for this bucket.
            if (array.Capacity != Utilities.GetMaxSizeForBucket(bucketIndex))
            {
                throw new ArgumentException(
                    "The buffer is not associated with this pool and may not be returned to it.", nameof(array));
            }

            // Store the array into the TLS bucket.  If there's already an array in it,
            // push that array down into the partitions, preferring to keep the latest
            // one in TLS for better locality.
            ref var tla = ref tlsBuckets.Buckets[bucketIndex];
            var prev = tla;
            tla = array.Pointer;
            if (prev != IntPtr.Zero)
            {
                PushToPartition(bucketIndex, prev);
            }
        }

        private static void PushToPartition(int bucketIndex, IntPtr pointer)
        {
            var perCoreBuckets = _buckets;
            if ((uint)bucketIndex >= (uint)perCoreBuckets.Length)
            {
                Utilities.Free(pointer);
                return;
            }

            var partitions = perCoreBuckets[bucketIndex] ?? CreatePerCorePartitions(bucketIndex);
            if (!partitions.TryPush(pointer))
            {
                Utilities.Free(pointer);
            }
        }

        private static ThreadLocalNativeArrayBuckets InitializeTlsBuckets()
        {
            Debug.Assert(t_tlsBuckets is null, $"Non-null {nameof(t_tlsBuckets)}");

            var tlsBuckets = new ThreadLocalNativeArrayBuckets(NumBuckets);
            t_tlsBuckets = tlsBuckets;

            return tlsBuckets;
        }
    }

    /// <summary>Wrapper for arrays stored in ThreadStatic buckets.</summary>
    internal sealed class ThreadLocalNativeArrayBuckets : CriticalFinalizerObject, IDisposable
    {
#if DEBUG
       public readonly Thread OwningThread;  
#endif
        public readonly IntPtr[] Buckets;

        public int Length => Buckets.Length;

        public ThreadLocalNativeArrayBuckets(int numBuckets)
        {
            Buckets = new IntPtr[numBuckets];
#if DEBUG
            OwningThread = Thread.CurrentThread;
#endif
        }

        ~ThreadLocalNativeArrayBuckets()
        {
            FreeBuckets();
        }

        private void FreeBuckets()
        {
            var buckets = Buckets;
            for (var i = 0; i < buckets.Length; i++)
            {
                var ptr = buckets[i];
                if (ptr != IntPtr.Zero)
                {
                    Utilities.Free(ptr);
                    buckets[i] = IntPtr.Zero;
                }
            }
        }

        public void Dispose()
        {
            FreeBuckets();
            GC.SuppressFinalize(this);
        }
    }

    // The following partition types are separated out of SharedArrayPool<T> to avoid
    // them being generic, in order to avoid the generic code size increase that would
    // result, in particular for Native AOT. The only thing that's necessary to actually
    // be generic is the return type of TryPop, and we can handle that at the access
    // site with a well-placed Unsafe.As.

    /// <summary>Provides a collection of partitions, each of which is a pool of arrays.</summary>
    internal sealed class NativeArrayPoolPartitions
    {
        /// <summary>The partitions.</summary>
        private readonly Partition[] _partitions;

        /// <summary>Initializes the partitions.</summary>
        public NativeArrayPoolPartitions()
        {
            // Create the partitions.  We create as many as there are processors, limited by our max.
            var partitions = new Partition[NativeArrayPoolStatics.s_partitionCount];
            for (var i = 0; i < partitions.Length; i++)
            {
                partitions[i] = new Partition();
            }

            _partitions = partitions;
        }

        /// <summary>
        /// Try to push the array into any partition with available space, starting with partition associated with the current core.
        /// If all partitions are full, the array will be dropped.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryPush(IntPtr array)
        {
            // Try to push on to the associated partition first.  If that fails,
            // round-robin through the other partitions.
            var partitions = _partitions;
            var index = (int)((uint)GetCurrentPartitionIndex() %
                              (uint)NativeArrayPoolStatics.s_partitionCount); // mod by constant in tier 1
            for (var i = 0; i < partitions.Length; i++)
            {
                if (partitions[index].TryPush(array))
                {
                    return true;
                }

                if (++index == partitions.Length)
                {
                    index = 0;
                }
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetCurrentPartitionIndex()
        {
#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
            return Thread.GetCurrentProcessorId();
#else
            return Thread.CurrentThread.ManagedThreadId;
#endif
        }

        /// <summary>
        /// Try to pop an array from any partition with available arrays, starting with partition associated with the current core.
        /// If all partitions are empty, null is returned.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IntPtr TryPop()
        {
            // Try to pop from the associated partition first.  If that fails, round-robin through the other partitions.
            IntPtr arr;
            var partitions = _partitions;
            var index = (int)((uint)GetCurrentPartitionIndex() %
                              (uint)NativeArrayPoolStatics.s_partitionCount); // mod by constant in tier 1
            for (var i = 0; i < partitions.Length; i++)
            {
                if ((arr = partitions[index].TryPop()) != IntPtr.Zero)
                {
                    return arr;
                }

                if (++index == partitions.Length)
                {
                    index = 0;
                }
            }

            return IntPtr.Zero;
        }
    }

    /// <summary>Provides a simple, bounded stack of arrays, protected by a lock.</summary>
    internal sealed class Partition
    {
        /// <summary>The arrays in the partition.</summary>
        private readonly IntPtr[] _arrays = new IntPtr[NativeArrayPoolStatics.s_maxArraysPerPartition];

        /// <summary>Number of arrays stored in <see cref="_arrays"/>.</summary>
        private int _count;

#if NET10_0_OR_GREATER
        private readonly System.Threading.Lock _lock = new ();
#endif

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryPush(IntPtr array)
        {
            var enqueued = false;
#if NET10_0_OR_GREATER
            _lock.Enter();
#else
            Monitor.Enter(this);
#endif
            var arrays = _arrays;
            var count = _count;
            if ((uint)count < (uint)arrays.Length)
            {
#if NET6_0_OR_GREATER
                Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(arrays), count) =
                    array; // arrays[count] = array, but avoiding stelemref
#else
                arrays[count] = array;
#endif
                _count = count + 1;
                enqueued = true;
            }
#if NET10_0_OR_GREATER
            _lock.Exit();
#else
            Monitor.Exit(this);
#endif
            return enqueued;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IntPtr TryPop()
        {
            var arr = IntPtr.Zero;
#if NET10_0_OR_GREATER
            _lock.Enter();
#else
            Monitor.Enter(this);
#endif
            var arrays = _arrays;
            var count = _count - 1;
            if ((uint)count < (uint)arrays.Length)
            {
                arr = arrays[count];
                arrays[count] = IntPtr.Zero;
                _count = count;
            }

#if NET10_0_OR_GREATER
            _lock.Exit();
#else
            Monitor.Exit(this);
#endif
            return arr;
        }
    }

    internal static class NativeArrayPoolStatics
    {
        /// <summary>Number of partitions to employ.</summary>
        internal static readonly int s_partitionCount = GetPartitionCount();

        /// <summary>The maximum number of arrays of a given size allowed to be cached per partition.</summary>
        internal static readonly int s_maxArraysPerPartition = GetMaxArraysPerPartition();

        /// <summary>Gets the maximum number of partitions to shard arrays into.</summary>
        /// <remarks>Defaults to int.MaxValue.  Whatever value is returned will end up being clamped to <see cref="Environment.ProcessorCount"/>.</remarks>
        private static int GetPartitionCount()
        {
            var partitionCount =
                TryGetInt32EnvironmentVariable("DOTNET_SYSTEM_BUFFERS_SHAREDARRAYPOOL_MAXPARTITIONCOUNT",
                    out var result) && result > 0
                    ? result
                    : int.MaxValue; // no limit other than processor count
            return Math.Min(partitionCount, Environment.ProcessorCount);
        }

        /// <summary>Gets the maximum number of arrays of a given size allowed to be cached per partition.</summary>
        /// <returns>Defaults to 32. This does not factor in or impact the number of arrays cached per thread in TLS (currently only 1).</returns>
        private static int GetMaxArraysPerPartition()
        {
            return TryGetInt32EnvironmentVariable("DOTNET_SYSTEM_BUFFERS_SHAREDARRAYPOOL_MAXARRAYSPERPARTITION",
                out var result) && result > 0
                ? result
                : 32; // arbitrary limit
        }

        /// <summary>Look up an environment variable and try to parse it as an Int32.</summary>
        /// <remarks>This avoids using anything that might in turn recursively use the ArrayPool.</remarks>
        private static bool TryGetInt32EnvironmentVariable(string variable, out int result)
        {
            // Avoid globalization stack, as it might in turn be using ArrayPool.

            if (Environment.GetEnvironmentVariable(variable) is string envVar &&
                envVar.Length > 0 && 
                envVar.Length <= 32) // arbitrary limit that allows for some spaces around the maximum length of a non-negative Int32 (10 digits)
            {
                var value = envVar.AsSpan().Trim(' ');
                if (!value.IsEmpty && value.Length <= 10)
                {
                    long tempResult = 0;
                    foreach (var c in value)
                    {
                        var digit = (uint)(c - '0');
                        if (digit > 9)
                        {
                            goto Fail;
                        }

                        tempResult = tempResult * 10 + digit;
                    }

                    if (tempResult >= 0 && tempResult <= int.MaxValue)
                    {
                        result = (int)tempResult;
                        return true;
                    }
                }
            }

            Fail:
            result = 0;
            return false;
        }
    }

    internal static class Utilities
    {
#if UNITY_EDITOR
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, GCHandle> s_handles 
            = new System.Collections.Concurrent.ConcurrentDictionary<IntPtr, GCHandle>();

        internal static void PurgeAll()
        {
            foreach (var kvp in s_handles)
            {
                if (kvp.Value.IsAllocated)
                {
                    kvp.Value.Free();
                }
            }
            s_handles.Clear();
        }
#endif
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static IntPtr Alloc(int minimumLength)
        {
#if UNITY_EDITOR
            var arr = new byte[minimumLength];
            var handle = GCHandle.Alloc(arr, GCHandleType.Pinned);
            var ptr = handle.AddrOfPinnedObject();
            s_handles.TryAdd(ptr, handle);
            return ptr;
#elif NET6_0_OR_GREATER
            unsafe
            {
                nuint alignment = 64;
                return (IntPtr)NativeMemory.AlignedAlloc((nuint)minimumLength, alignment);
            }
#else
            return Marshal.AllocHGlobal(minimumLength);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Free(IntPtr arr)
        {
            if (arr != IntPtr.Zero)
            {
#if UNITY_EDITOR
                if (s_handles.TryRemove(arr, out var handle))
                {
                    if (handle.IsAllocated)
                    {
                        handle.Free();
                    }
                }
#elif NET6_0_OR_GREATER
                unsafe
                {
                    NativeMemory.AlignedFree(arr.ToPointer());
                }
#else
                Marshal.FreeHGlobal(arr);
#endif
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int SelectBucketIndex(int bufferSize)
        {
            // Buffers are bucketed so that a request between 2^(n-1) + 1 and 2^n is given a buffer of 2^n
            // Bucket index is log2(bufferSize - 1) with the exception that buffers between 1 and 16 bytes
            // are combined, and the index is slid down by 3 to compensate.
            // Zero is a valid bufferSize, and it is assigned the highest bucket index so that zero-length
            // buffers are not retained by the pool. The pool will return the Array.Empty singleton for these.
            var target = (uint)bufferSize - 1 | 15;
#if NETCOREAPP3_1_OR_GREATER
            return System.Numerics.BitOperations.Log2(target) - 3;
#else
            var n = 31;
            if (target >= 65536)
            {
                n -= 16;
                target >>= 16;
            }

            if (target >= 256)
            {
                n -= 8;
                target >>= 8;
            }

            if (target >= 16)
            {
                n -= 4;
                target >>= 4;
            }

            if (target >= 4)
            {
                n -= 2;
                target >>= 2;
            }

            var lzc = n - (int)(target >> 1);
            return (31 - lzc) - 3;
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int GetMaxSizeForBucket(int binIndex)
        {
            var maxSize = 16 << binIndex;
            Debug.Assert(maxSize >= 0);
            return maxSize;
        }
    }

    internal static class ArgumentOutOfRangeExceptionNetCoreHelper
    {
        public static void ThrowIfNegative(int value, string paramName)
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(paramName, "Non-negative number required.");
            }
        }
    }
}
