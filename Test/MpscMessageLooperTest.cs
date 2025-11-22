namespace Test;

using JustCopy;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit.Abstractions;

public class MpscMessageLooperTest
{
    class MyLooper : MpscMessageLooper<int>
    {
        private CancellationTokenSource source = new();
        public int? ReceiveFirstItem { get; private set; }
        public List<int> ReceivedItems { get; private set; } = new();

        private MyLooper()
        {
        }

        public static MyLooper Create()
        {
            var l = new MyLooper();
            _ = l.Start(CancellationToken.None);
            return l;
        }

        public void ResetCurrent()
        {
            source = new CancellationTokenSource();
            ReceiveFirstItem = null;
        }

        public override void OnItemReceived(int item)
        {
            if (!ReceiveFirstItem.HasValue)
            {
                ReceiveFirstItem = item;
            }

            ReceivedItems.Add(item);
            source.Cancel();
        }

        public async Task WaitToReadAsync()
        {
            try
            {
                await Task.Delay(-1, source.Token);
            }
            catch (TaskCanceledException)
            {
            }
        }
    }

    private readonly ITestOutputHelper output;

    public MpscMessageLooperTest(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    public async Task WaitAndWrite()
    {
        var looper = MyLooper.Create();
        _ = Task.Run(async () =>
        {
            await Task.Delay(1000);
            looper.Add(3);
        });

        await looper.WaitToReadAsync();

        var outResult = looper.ReceiveFirstItem;
        Assert.True(outResult.HasValue);
        Assert.Equal(3, outResult);
    }

    [Fact]
    public async Task WaitAndWrite10()
    {
        var looper = MyLooper.Create();
        for (var i = 0; i < 10; ++i)
        {
            var current = i;
            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                looper.Add(current);
            });

            await looper.WaitToReadAsync();
            var outResult = looper.ReceiveFirstItem;
            Assert.True(outResult.HasValue);
            Assert.Equal(current, outResult);
            looper.ResetCurrent();
        }
    }

    [Fact]
    public void SingleConsumerMultiProducer()
    {
        var looper = MyLooper.Create();

        var t1 = new Thread(() =>
        {
            for (var i = 0; i < 10000; ++i)
            {
                looper.Add(i);
            }
        });
        t1.Start();

        var t2 = new Thread(() =>
        {
            for (var i = 10000; i < 20000; ++i)
            {
                looper.Add(i);
            }
        });
        t2.Start();

        var t3 = new Thread(() =>
        {
            for (var i = 20000; i < 30000; ++i)
            {
                looper.Add(i);
            }
        });
        t3.Start();

        var beginTime = DateTime.Now;
        while (true)
        {
            var receivedItemsCount = looper.ReceivedItems.Count;
            if (receivedItemsCount == 30000)
            {
                break;
            }

            var now = DateTime.Now;
            if (now - beginTime > TimeSpan.FromMilliseconds(3000))
            {
                Assert.Fail("now - begin > 3000");
                break;
            }
        }

        const int expectSum = (30000 * 29999) / 2;
        Assert.Equal(expectSum, looper.ReceivedItems.Sum());
    }
}
