namespace Test;

using System.Collections.Concurrent;
using System.Diagnostics;
using Xunit.Abstractions;
using JustCopy;
using System.Runtime.CompilerServices;

public class AutoResetEventSlimTest
{
    private readonly ITestOutputHelper output;

    public AutoResetEventSlimTest(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    public void SetAndWait()
    {
        var autoResetEvent = new AutoResetEventSlim();
        autoResetEvent.Set();
        if (!autoResetEvent.WaitOne(0))
        {
            Assert.Fail();
        }
    }

    [Fact]
    public void WaitThreadSetMain()
    {
        var autoResetEvent = new AutoResetEventSlim();
        var end = false;
        var t = new Thread(() =>
        {
            for (var i = 0; i < 100000; ++i)
            {
                autoResetEvent.WaitOne();
            }

            Volatile.Write(ref end, true);
        });
        t.Start();

        while (!Volatile.Read(ref end))
        {
            autoResetEvent.Set();
        }

        if (!t.Join(1000))
        {
            Assert.Fail("join fail");
        }
    }

    [Fact]
    public void WaitThread10SetMain()
    {
        //var autoResetEvent = new AutoResetEvent(false);
        var autoResetEvent = new AutoResetEventSlim();
        var endCount = 0;
        const int threadCount = 10;
        var threads = new Thread[threadCount];
        for (var i = 0; i < threadCount; ++i)
        {
            var t = new Thread(() =>
            {
                for (var j = 0; j < 100000; ++j)
                {
                    autoResetEvent.WaitOne();
                }

                Interlocked.Increment(ref endCount);
            });
            t.Start();
            threads[i] = t;
        }

        while (Volatile.Read(ref endCount) < threadCount)
        {
            autoResetEvent.Set();
        }

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < threadCount; ++i)
        {
            threads[i].Join(100000);
            output.WriteLine($"end {i} Elapsed:{stopwatch.ElapsedMilliseconds}ms");
        }
    }
}