namespace LruCache.Tests;

public class LruCacheTests
{
    [Fact]
    public void Get_ReturnsStoredValue()
    {
        var c = new LruCache<string, int>(2);
        c.Put("a", 1);
        Assert.True(c.TryGet("a", out var v));
        Assert.Equal(1, v);
        Assert.False(c.TryGet("zzz", out _));
    }

    [Fact]
    public void Evicts_LeastRecentlyUsed()
    {
        var c = new LruCache<string, int>(2);
        c.Put("a", 1);
        c.Put("b", 2);
        c.Put("c", 3);
        Assert.False(c.ContainsKey("a"));
        Assert.True(c.ContainsKey("b"));
        Assert.True(c.ContainsKey("c"));
    }

    [Fact]
    public void Get_RefreshesRecency()
    {
        var c = new LruCache<string, int>(2);
        c.Put("a", 1);
        c.Put("b", 2);
        c.TryGet("a", out _);
        c.Put("c", 3); // evicts b
        Assert.True(c.ContainsKey("a"));
        Assert.False(c.ContainsKey("b"));
        Assert.Equal(new[] { "c", "a" }, c.KeysByRecency());
    }

    [Fact]
    public void Put_ExistingKey_ReplacesAndRefreshes()
    {
        var c = new LruCache<string, int>(2);
        c.Put("a", 1);
        c.Put("b", 2);
        c.Put("a", 10);
        c.Put("c", 3); // evicts b
        Assert.True(c.TryGet("a", out var v));
        Assert.Equal(10, v);
        Assert.False(c.ContainsKey("b"));
        Assert.Equal(2, c.Count);
    }

    [Fact]
    public void Capacity_One_Works()
    {
        var c = new LruCache<int, int>(1);
        c.Put(1, 1);
        c.Put(2, 2);
        Assert.False(c.ContainsKey(1));
        Assert.True(c.ContainsKey(2));
    }

    [Fact]
    public void Remove_And_Clear()
    {
        var c = new LruCache<int, int>(3);
        c.Put(1, 1); c.Put(2, 2);
        Assert.True(c.Remove(1));
        Assert.False(c.Remove(1));
        Assert.Equal(1, c.Count);
        c.Clear();
        Assert.Equal(0, c.Count);
        c.Put(5, 5); // list still consistent after clear
        Assert.True(c.ContainsKey(5));
    }

    [Fact]
    public void InvalidArguments_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LruCache<int, int>(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LruCache<int, int>(1, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LruCache<int, int>(1).Put(1, 1, TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Statistics_AreTracked()
    {
        var c = new LruCache<int, int>(1);
        c.Put(1, 1);
        c.TryGet(1, out _);
        c.TryGet(2, out _);
        c.Put(2, 2);
        Assert.Equal(1, c.Hits);
        Assert.Equal(1, c.Misses);
        Assert.Equal(1, c.Evictions);
    }

    [Fact]
    public void GetOrAdd_RunsFactoryOnlyOnMiss()
    {
        var c = new LruCache<int, string>(2);
        var calls = 0;
        Assert.Equal("v1", c.GetOrAdd(1, k => { calls++; return "v" + k; }));
        Assert.Equal("v1", c.GetOrAdd(1, k => { calls++; return "other"; }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void EvictionCallback_ReportsReasons()
    {
        var log = new List<(int, EvictionReason)>();
        var c = new LruCache<int, int>(1, onEvicted: (k, _, r) => log.Add((k, r)));
        c.Put(1, 1);
        c.Put(2, 2);
        c.Remove(2);
        Assert.Equal(new[] { (1, EvictionReason.Capacity), (2, EvictionReason.Removed) }, log);
    }
}

public class LruCacheTtlTests
{
    [Fact]
    public void Entry_Expires_AfterDefaultTtl()
    {
        var time = new ManualTime();
        var c = new LruCache<string, int>(5, TimeSpan.FromSeconds(10), time);
        c.Put("a", 1);
        time.Advance(TimeSpan.FromSeconds(9));
        Assert.True(c.TryGet("a", out _));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(c.TryGet("a", out _));
        Assert.Equal(0, c.Count);
    }

    [Fact]
    public void PerEntryTtl_OverridesDefault()
    {
        var time = new ManualTime();
        var c = new LruCache<string, int>(5, TimeSpan.FromSeconds(100), time);
        c.Put("short", 1, TimeSpan.FromSeconds(1));
        c.Put("long", 2);
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(c.ContainsKey("short"));
        Assert.True(c.ContainsKey("long"));
    }

    [Fact]
    public void Reading_DoesNotExtendTtl()
    {
        var time = new ManualTime();
        var c = new LruCache<string, int>(5, TimeSpan.FromSeconds(10), time);
        c.Put("a", 1);
        time.Advance(TimeSpan.FromSeconds(6));
        c.TryGet("a", out _);
        time.Advance(TimeSpan.FromSeconds(6));
        Assert.False(c.TryGet("a", out _));
    }

    [Fact]
    public void PurgeExpired_RemovesOnlyStale()
    {
        var time = new ManualTime();
        var reasons = new List<EvictionReason>();
        var c = new LruCache<int, int>(10, timeProvider: time, onEvicted: (_, _, r) => reasons.Add(r));
        c.Put(1, 1, TimeSpan.FromSeconds(1));
        c.Put(2, 2, TimeSpan.FromSeconds(1));
        c.Put(3, 3);
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(2, c.PurgeExpired());
        Assert.Equal(1, c.Count);
        Assert.All(reasons, r => Assert.Equal(EvictionReason.Expired, r));
    }

    [Fact]
    public void Put_RefreshesTtl()
    {
        var time = new ManualTime();
        var c = new LruCache<string, int>(5, TimeSpan.FromSeconds(10), time);
        c.Put("a", 1);
        time.Advance(TimeSpan.FromSeconds(8));
        c.Put("a", 2);
        time.Advance(TimeSpan.FromSeconds(8));
        Assert.True(c.TryGet("a", out var v));
        Assert.Equal(2, v);
    }
}

public class LruCacheConcurrencyTests
{
    [Fact]
    public void ParallelAccess_KeepsInvariants()
    {
        var c = new LruCache<int, int>(100);
        Parallel.For(0, 8, worker =>
        {
            var rng = new Random(worker);
            for (var i = 0; i < 20000; i++)
            {
                var k = rng.Next(500);
                switch (rng.Next(4))
                {
                    case 0: c.Put(k, k); break;
                    case 1: c.Remove(k); break;
                    default:
                        if (c.TryGet(k, out var v)) Assert.Equal(k, v);
                        break;
                }
            }
        });
        Assert.InRange(c.Count, 0, 100);
        Assert.Equal(c.Count, c.KeysByRecency().Count);
        Assert.Equal(c.Count, c.KeysByRecency().Distinct().Count());
    }
}
