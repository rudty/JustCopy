namespace Test;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using JustCopy;

public unsafe class NativeByteMemoryManagerTest
{
    [Fact]
    public void PinTest()
    {
        var m = new NativeByteMemoryManager(1024);
        Assert.Equal(1,m.ReferenceCount);
        var memoryHandle = m.Pin();
        Assert.Equal(2, m.ReferenceCount);
        var b = (byte*)memoryHandle.Pointer;

        for (var i = 0; i < 1024; ++i)
        {
            b[i] = (byte)i;
        }

        m.Unpin();
        Assert.Equal(1, m.ReferenceCount);

        IDisposable d = m;
        d.Dispose();
        Assert.Equal(0, m.ReferenceCount);
    }

    [Fact]
    public void SpanTest()
    {
        var m = new NativeByteMemoryManager(1024);
        var span = m.GetSpan();
        Assert.Equal(1024, span.Length);
        for (var i = 0; i < 1024; ++i)
        {
            span[i] = (byte)i;
        }
    }

    [Fact]
    public void MemoryTest()
    {
        var m = new NativeByteMemoryManager(1024);
        var memory = m.Memory;
        Assert.Equal(1024, memory.Length);
        var span = memory.Span;
        Assert.Equal(1024, span.Length);
        for (var i = 0; i < 1024; ++i)
        {
            span[i] = (byte)i;
        }
    }
}
