using System.Numerics;

namespace DeleteMan.Engine;

/// <summary>
/// An append-only, chunked vector that many threads can push to and read from concurrently (the C# counterpart of
/// the <c>boxcar::Vec</c> the Rust scanner used).
/// </summary>
/// <remarks>
/// Elements live in buckets of doubling size (32, 64, 128, ...). A published bucket is never reallocated or moved, so
/// readers index it without locks while writers keep appending. A push reserves an index with one interlocked
/// increment, installs the bucket on first use with a compare-exchange, and publishes the element with a volatile
/// write; until then the slot reads as <see langword="null"/>.
/// </remarks>
/// <typeparam name="T">Element type (a reference type, so an unwritten slot is distinguishable).</typeparam>
internal sealed class Arena<T>
    where T : class
{
    private const int FirstBucketBits = 5;
    private const int FirstBucketSize = 1 << FirstBucketBits;

    /// <summary>Enough buckets for every non-negative <see cref="int"/> index.</summary>
    private const int BucketCount = 32 - FirstBucketBits;

    private readonly T?[]?[] _buckets = new T?[BucketCount][];
    private int _reserved;

    /// <summary>Number of indices handed out so far (every one of them is readable once its push returns).</summary>
    public int Count => Volatile.Read(ref _reserved);

    /// <summary>The element at <paramref name="index"/>.</summary>
    /// <exception cref="IndexOutOfRangeException">No element was published at that index.</exception>
    public T this[int index] => TryGet(index) ?? throw new IndexOutOfRangeException();

    /// <summary>Appends an element and returns its index.</summary>
    public int Push(T item)
    {
        var index = Interlocked.Increment(ref _reserved) - 1;
        if (index < 0)
        {
            throw new InvalidOperationException("arena is full");
        }
        var (bucket, offset) = Locate(index);
        var slots = Volatile.Read(ref _buckets[bucket]);
        if (slots is null)
        {
            var fresh = new T?[FirstBucketSize << bucket];
            slots = Interlocked.CompareExchange(ref _buckets[bucket], fresh, null) ?? fresh;
        }
        Volatile.Write(ref slots[offset], item);
        return index;
    }

    /// <summary>The element at <paramref name="index"/>, or <see langword="null"/> if none was published there.</summary>
    public T? TryGet(int index)
    {
        if (index < 0)
        {
            return null;
        }
        var (bucket, offset) = Locate(index);
        var slots = Volatile.Read(ref _buckets[bucket]);
        return slots is null ? null : Volatile.Read(ref slots[offset]);
    }

    private static (int Bucket, int Offset) Locate(int index)
    {
        // Bucket b holds indices [32·(2^b − 1), 32·(2^(b+1) − 1)); shifting by 32 makes that a power-of-two range.
        var shifted = (uint)index + FirstBucketSize;
        var log = BitOperations.Log2(shifted);
        return (log - FirstBucketBits, (int)(shifted - (1u << log)));
    }
}
