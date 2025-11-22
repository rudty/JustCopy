namespace Test;

using JustCopy;

public class MpscBlockingQueueTest
{
    public static int GetPopCount<T>(MpscBlockingQueue<T> c)
    {
        var count = 0;
        while (c.TryTake(out _))
        {
            count += 1;
        }

        return  count;
    }

    [Fact]
    public void AddAndTake()
    {
        var collection = new MpscBlockingQueue<int>();
        for (var i = 0; i < 10; ++i)
        {
            collection.Add(i);
        }

        for (var i = 0; i < 10; ++i)
        {
            var takeValue = collection.Take();
            Assert.Equal(i, takeValue);
        }

        Assert.Equal(0, GetPopCount(collection));
    }

    [Fact]
    public void AddAndTryTake()
    {
        var collection = new MpscBlockingQueue<int>();
        for (var i = 0; i < 10; ++i)
        {
            collection.Add(i);
        }

        for (var i = 0; i < 10; ++i)
        {
            var success = collection.TryTake(out var takeValue);
            Assert.True(success);
            Assert.Equal(i, takeValue);
        }

        Assert.Equal(0, GetPopCount(collection));
    }

    [Fact]
    public void SingleConsumerMultiProducer()
    {
        var collection = new MpscBlockingQueue<int>();

        var t1 = new Thread(() =>
        {
            for (var i = 0; i < 10000; ++i)
            {
                collection.Add(i);
            }
        });
        t1.Start();

        var t2 = new Thread(() =>
        {
            for (var i = 10000; i < 20000; ++i)
            {
                collection.Add(i);
            }
        });
        t2.Start();

        var t3 = new Thread(() =>
        {
            for (var i = 20000; i < 30000; ++i)
            {
                collection.Add(i);
            }
        });
        t3.Start();

        var outCount = 0;
        var outSum = 0L;
        for (var i = 0; i < 30000; ++i)
        {
            var takeResult = collection.TryTake(out var item, 3000);
            Assert.True(takeResult);
            outCount += 1;
            outSum += item;
        }

        Assert.Equal(30000, outCount);

        const int expectSum = (30000 * 29999) / 2;
        Assert.Equal(expectSum, outSum);
        Assert.Equal(0, GetPopCount(collection));
    }
}
