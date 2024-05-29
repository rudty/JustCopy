namespace JustCopy
{
    using System;
    using System.Buffers;
    using System.Threading;

    public sealed unsafe class NativeByteMemoryManager : MemoryManager<byte>
    {
        private int referenceCount = 1;
        private readonly byte* pointer;
        public int Count { get; }

        public int ReferenceCount => referenceCount;

        public override Memory<byte> Memory { get; }

        public NativeByteMemoryManager(int size)
        {
            Count = size;
            Memory = CreateMemory(size);
            pointer = Alloc(size);
        }

#if NET6_0_OR_GREATER
        private static byte* Alloc(int size)
        {
            return (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)size);
        }

        private static void Free(byte* b)
        {
            System.Runtime.InteropServices.NativeMemory.Free(b);
        }
#else
        private static byte* Alloc(int size)
        {
            return (byte*)System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
        }

        private static void Free(byte* b)
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal((IntPtr)b);
        }
#endif

        public override Span<byte> GetSpan() => new Span<byte>(pointer, Count);

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            Interlocked.Increment(ref referenceCount);
            return new MemoryHandle(pointer + elementIndex, default, this);
        }

        public override void Unpin()
        {
            if (Interlocked.Decrement(ref referenceCount) == 0)
            {
                Free(pointer);
            }
        }

        protected override void Dispose(bool disposing)
        {
            Unpin();
        }
    }
}
