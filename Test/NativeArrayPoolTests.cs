namespace Test;
using System;
using Xunit;
using JustCopy;
using System.Runtime.InteropServices;

public class NativeArrayPoolTests
{
    [Fact]
    public void Rent_ReturnsMinimumRequestedSize()
    {
        // Arrange
        var requestedSize = 1024;

        // Act
        var buffer = NativeArrayPool.Rent(requestedSize);

        try
        {
            // Assert
            Assert.NotEqual(IntPtr.Zero, buffer.Pointer);
            Assert.True(buffer.Capacity >= requestedSize);
        }
        finally
        {
            NativeArrayPool.Return(buffer);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(1024 * 1024)] // 1MB
    public void Rent_And_Return_SpecificSizes(int size)
    {
        var buffer = NativeArrayPool.Rent(size);
        Assert.NotNull(buffer.Pointer == IntPtr.Zero && size > 0 ? null : "Success");
        NativeArrayPool.Return(buffer);
    }

    [Fact]
    public void Rent_ZeroLength_ReturnsEmptyBuffer()
    {
        var buffer = NativeArrayPool.Rent(0);
        Assert.Equal(IntPtr.Zero, buffer.Pointer);
        Assert.Equal(0, buffer.Capacity);
    }

    [Fact]
    public void MultiThreaded_Rent_Return_NoCorruption()
    {
        const int ThreadCount = 50;
        const int Iterations = 1000;

        Parallel.For(0, ThreadCount, _ =>
        {
            for (var i = 0; i < Iterations; i++)
            {
                var size = (i % 1024) + 1;
                var buffer = NativeArrayPool.Rent(size);

                // 메모리에 직접 쓰기 시도 (Access Violation 체크)
                unsafe
                {
                    var ptr = (byte*)buffer.Pointer;
                    ptr[0] = 0xAA;
                    ptr[buffer.Capacity - 1] = 0xBB;
                }

                NativeArrayPool.Return(buffer);
            }
        });
    }

    [Fact]
    public void Return_WithClearArray_ZeroesMemory()
    {
        // 1. 대여 후 값을 채움
        var buffer = NativeArrayPool.Rent(1024);
        unsafe
        {
            var ptr = (byte*)buffer.Pointer;
            for (var i = 0; i < 1024; i++)
            {
                ptr[i] = 0xFF;
            }
        }

        // 2. Clear 옵션과 함께 반납
        NativeArrayPool.Return(buffer, clearArray: true);

        // 3. 동일한 크기를 다시 대여 (같은 버킷에서 나올 확률이 매우 높음)
        var newBuffer = NativeArrayPool.Rent(1024);

        try
        {
            unsafe
            {
                var ptr = (byte*)newBuffer.Pointer;
                for (var i = 0; i < 1024; i++)
                {
                    if (ptr[i] != 0)
                    {
                        Assert.Fail("Memory was not cleared");
                    }
                }
            }
        }
        finally
        {
            NativeArrayPool.Return(newBuffer);
        }
    }

    [Fact]
    public void Return_InvalidBuffer_ThrowsArgumentException()
    {
        // 잘못된 Capacity를 가진 버퍼 (버킷 인덱싱 불일치 유도)
        var invalidBuffer = new NativeBuffer(Marshal.AllocHGlobal(100), 123);
        
        try
        {
            Assert.Throws<ArgumentException>(() => NativeArrayPool.Return(invalidBuffer));
        }
        finally
        {
            Marshal.FreeHGlobal(invalidBuffer.Pointer);
        }
    }
}
