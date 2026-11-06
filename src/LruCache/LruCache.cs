using System.Diagnostics.CodeAnalysis;

namespace LruCache;

/// <summary>Why an entry left the cache.</summary>
public enum EvictionReason { Capacity, Expired, Removed, Replaced }

/// <summary>
/// Generic, thread-safe LRU cache with O(1) get/put/remove and optional per-entry or default TTL.
/// Implemented as a hash map plus an intrusive doubly linked list ordered by recency.
/// </summary>
public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private sealed class Node
    {
        public required TKey Key;
        public TValue Value = default!;
        public long ExpiresAtTicks = long.MaxValue; // TimeProvider timestamp; MaxValue = never
        public Node? Prev, Next;
    }

    private readonly object _gate = new();
    private readonly Dictionary<TKey, Node> _map;
    private readonly TimeProvider _time;
    private readonly TimeSpan? _defaultTtl;
    private readonly Action<TKey, TValue, EvictionReason>? _onEvicted;
    private Node? _head; // most recently used
    private Node? _tail; // least recently used
    private long _hits, _misses, _evictions;

    public LruCache(int capacity, TimeSpan? defaultTtl = null, TimeProvider? timeProvider = null,
        IEqualityComparer<TKey>? comparer = null, Action<TKey, TValue, EvictionReason>? onEvicted = null)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        if (defaultTtl is { } t && t <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(defaultTtl));
        Capacity = capacity;
        _defaultTtl = defaultTtl;
        _time = timeProvider ?? TimeProvider.System;
        _map = new Dictionary<TKey, Node>(comparer);
        _onEvicted = onEvicted;
    }

    public int Capacity { get; }

    /// <summary>Number of stored entries, including expired ones not yet purged.</summary>
    public int Count { get { lock (_gate) return _map.Count; } }

    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);
    public long Evictions => Interlocked.Read(ref _evictions);

    public bool TryGet(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        List<(TKey, TValue, EvictionReason)>? evicted = null;
        bool found;
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                if (IsExpired(node))
                {
                    RemoveNode(node);
                    (evicted = new()).Add((node.Key, node.Value, EvictionReason.Expired));
                    found = false;
                    value = default;
                }
                else
                {
                    MoveToFront(node);
                    value = node.Value;
                    found = true;
                }
            }
            else
            {
                value = default;
                found = false;
            }
        }
        if (found) Interlocked.Increment(ref _hits); else Interlocked.Increment(ref _misses);
        Notify(evicted);
        return found;
    }

    /// <summary>Inserts or replaces a value. A null <paramref name="ttl"/> uses the default TTL.</summary>
    public void Put(TKey key, TValue value, TimeSpan? ttl = null)
    {
        var effective = ttl ?? _defaultTtl;
        if (effective is { } e && e <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl));
        List<(TKey, TValue, EvictionReason)> evicted = new();
        lock (_gate)
        {
            var expires = effective is null ? long.MaxValue : Deadline(effective.Value);
            if (_map.TryGetValue(key, out var existing))
            {
                evicted.Add((existing.Key, existing.Value, EvictionReason.Replaced));
                existing.Value = value;
                existing.ExpiresAtTicks = expires;
                MoveToFront(existing);
            }
            else
            {
                var node = new Node { Key = key, Value = value, ExpiresAtTicks = expires };
                _map[key] = node;
                PushFront(node);
                while (_map.Count > Capacity)
                {
                    var victim = _tail!;
                    RemoveNode(victim);
                    // Prefer reporting a stale entry as expired rather than as a capacity eviction.
                    evicted.Add((victim.Key, victim.Value, IsExpired(victim) ? EvictionReason.Expired : EvictionReason.Capacity));
                    Interlocked.Increment(ref _evictions);
                }
            }
        }
        // Replacements are not reported through the callback to avoid noise; only real removals are.
        Notify(evicted.Where(x => x.Item3 != EvictionReason.Replaced).ToList());
    }

    /// <summary>Returns the cached value or creates it with <paramref name="factory"/>. The factory runs outside the lock.</summary>
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory, TimeSpan? ttl = null)
    {
        if (TryGet(key, out var existing)) return existing;
        var created = factory(key);
        Put(key, created, ttl);
        return created;
    }

    public bool Remove(TKey key)
    {
        Node? node;
        lock (_gate)
        {
            if (!_map.TryGetValue(key, out node)) return false;
            RemoveNode(node);
        }
        Notify(new() { (node.Key, node.Value, EvictionReason.Removed) });
        return true;
    }

    /// <summary>Checks for presence without affecting recency or hit statistics.</summary>
    public bool ContainsKey(TKey key)
    {
        lock (_gate) return _map.TryGetValue(key, out var n) && !IsExpired(n);
    }

    /// <summary>Removes all expired entries (O(n)). Call periodically if memory held by stale entries matters.</summary>
    public int PurgeExpired()
    {
        List<(TKey, TValue, EvictionReason)> evicted = new();
        lock (_gate)
        {
            for (var n = _tail; n is not null;)
            {
                var prev = n.Prev;
                if (IsExpired(n))
                {
                    RemoveNode(n);
                    evicted.Add((n.Key, n.Value, EvictionReason.Expired));
                }
                n = prev;
            }
        }
        Notify(evicted);
        return evicted.Count;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _head = _tail = null;
        }
    }

    /// <summary>Keys ordered from most to least recently used (a snapshot).</summary>
    public IReadOnlyList<TKey> KeysByRecency()
    {
        lock (_gate)
        {
            var list = new List<TKey>(_map.Count);
            for (var n = _head; n is not null; n = n.Next) list.Add(n.Key);
            return list;
        }
    }

    private long Deadline(TimeSpan ttl) => _time.GetTimestamp() + (long)(ttl.TotalSeconds * _time.TimestampFrequency);

    private bool IsExpired(Node n) => n.ExpiresAtTicks != long.MaxValue && _time.GetTimestamp() >= n.ExpiresAtTicks;

    private void Notify(List<(TKey, TValue, EvictionReason)>? items)
    {
        if (_onEvicted is null || items is null) return;
        foreach (var (k, v, r) in items) _onEvicted(k, v, r);
    }

    private void RemoveNode(Node node)
    {
        Unlink(node);
        _map.Remove(node.Key);
    }

    private void MoveToFront(Node node)
    {
        if (_head == node) return;
        Unlink(node);
        PushFront(node);
    }

    private void PushFront(Node node)
    {
        node.Prev = null;
        node.Next = _head;
        if (_head is not null) _head.Prev = node;
        _head = node;
        _tail ??= node;
    }

    private void Unlink(Node node)
    {
        if (node.Prev is not null) node.Prev.Next = node.Next; else _head = node.Next;
        if (node.Next is not null) node.Next.Prev = node.Prev; else _tail = node.Prev;
        node.Prev = node.Next = null;
    }
}
