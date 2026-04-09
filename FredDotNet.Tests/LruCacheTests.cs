using NUnit.Framework;
using FredDotNet;

namespace FredDotNet.Tests;

[TestFixture]
public class LruCacheTests
{
    [Test]
    public void BasicSetAndGet()
    {
        var cache = new LruCache<string, int>(10);
        cache.Set("a", 1);
        cache.Set("b", 2);

        Assert.That(cache.TryGet("a", out int val1), Is.True);
        Assert.That(val1, Is.EqualTo(1));

        Assert.That(cache.TryGet("b", out int val2), Is.True);
        Assert.That(val2, Is.EqualTo(2));

        Assert.That(cache.Count, Is.EqualTo(2));
    }

    [Test]
    public void CapacityEviction_OldestEvicted()
    {
        var cache = new LruCache<int, string>(3);
        cache.Set(1, "one");
        cache.Set(2, "two");
        cache.Set(3, "three");

        // Cache is full (3/3). Adding a 4th should evict key 1 (oldest).
        cache.Set(4, "four");

        Assert.That(cache.Count, Is.EqualTo(3));
        Assert.That(cache.TryGet(1, out _), Is.False); // evicted
        Assert.That(cache.TryGet(2, out var v2), Is.True);
        Assert.That(v2, Is.EqualTo("two"));
        Assert.That(cache.TryGet(3, out _), Is.True);
        Assert.That(cache.TryGet(4, out _), Is.True);
    }

    [Test]
    public void LruOrdering_AccessedItemNotEvicted()
    {
        var cache = new LruCache<int, string>(3);
        cache.Set(1, "one");
        cache.Set(2, "two");
        cache.Set(3, "three");

        // Access key 1, making it most recently used
        cache.TryGet(1, out _);

        // Adding key 4 should evict key 2 (now the LRU), not key 1
        cache.Set(4, "four");

        Assert.That(cache.Count, Is.EqualTo(3));
        Assert.That(cache.TryGet(1, out _), Is.True);  // accessed, so not evicted
        Assert.That(cache.TryGet(2, out _), Is.False); // LRU, evicted
        Assert.That(cache.TryGet(3, out _), Is.True);
        Assert.That(cache.TryGet(4, out _), Is.True);
    }

    [Test]
    public void UpdateExistingKey_NewValueReturned()
    {
        var cache = new LruCache<string, int>(5);
        cache.Set("key", 100);

        Assert.That(cache.TryGet("key", out int v1), Is.True);
        Assert.That(v1, Is.EqualTo(100));

        cache.Set("key", 200);

        Assert.That(cache.TryGet("key", out int v2), Is.True);
        Assert.That(v2, Is.EqualTo(200));

        // Count should still be 1
        Assert.That(cache.Count, Is.EqualTo(1));
    }

    [Test]
    public void TryGet_ReturnsFalseForMissingKey()
    {
        var cache = new LruCache<string, string>(10);
        cache.Set("exists", "yes");

        Assert.That(cache.TryGet("missing", out _), Is.False);
    }

    [Test]
    public void TryGet_ReturnsFalseForEvictedKey()
    {
        var cache = new LruCache<int, int>(2);
        cache.Set(1, 10);
        cache.Set(2, 20);

        // Evict key 1
        cache.Set(3, 30);

        Assert.That(cache.TryGet(1, out _), Is.False);
    }

    [Test]
    public void Clear_EmptiesTheCache()
    {
        var cache = new LruCache<string, int>(10);
        cache.Set("a", 1);
        cache.Set("b", 2);
        cache.Set("c", 3);

        Assert.That(cache.Count, Is.EqualTo(3));

        cache.Clear();

        Assert.That(cache.Count, Is.EqualTo(0));
        Assert.That(cache.TryGet("a", out _), Is.False);
        Assert.That(cache.TryGet("b", out _), Is.False);
        Assert.That(cache.TryGet("c", out _), Is.False);
    }

    [Test]
    public void ThreadSafety_ConcurrentReadsAndWritesDontCrash()
    {
        var cache = new LruCache<int, int>(50);

        // Pre-populate
        for (int i = 0; i < 50; i++)
            cache.Set(i, i * 10);

        // Hammer it from multiple threads
        Parallel.For(0, 1000, i =>
        {
            int key = i % 100;
            if (i % 3 == 0)
            {
                cache.Set(key, i);
            }
            else
            {
                cache.TryGet(key, out _);
            }
        });

        // Just verify it didn't crash and count is within bounds
        Assert.That(cache.Count, Is.GreaterThan(0));
        Assert.That(cache.Count, Is.LessThanOrEqualTo(50));
    }

    [Test]
    public void Constructor_ThrowsOnZeroCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LruCache<string, string>(0));
    }

    [Test]
    public void Constructor_ThrowsOnNegativeCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LruCache<string, string>(-1));
    }

    [Test]
    public void UpdateExistingKey_MovesToFront()
    {
        var cache = new LruCache<int, string>(3);
        cache.Set(1, "one");
        cache.Set(2, "two");
        cache.Set(3, "three");

        // Update key 1 (moves it to front)
        cache.Set(1, "ONE");

        // Adding key 4 should evict key 2 (now LRU), not key 1
        cache.Set(4, "four");

        Assert.That(cache.TryGet(1, out var v), Is.True);
        Assert.That(v, Is.EqualTo("ONE"));
        Assert.That(cache.TryGet(2, out _), Is.False); // evicted
    }
}
