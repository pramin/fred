namespace FredDotNet;

/// <summary>
/// A thread-safe, generic LRU (Least Recently Used) cache.
/// Uses a dictionary for O(1) lookups and a linked list for O(1) eviction ordering.
/// </summary>
public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map;
    private readonly LinkedList<(TKey Key, TValue Value)> _list;
    private readonly object _lock = new();

    /// <inheritdoc />
    public LruCache(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");

        _capacity = capacity;
        _map = new Dictionary<TKey, LinkedListNode<(TKey, TValue)>>(capacity);
        _list = new LinkedList<(TKey, TValue)>();
    }

    /// <summary>
    /// Tries to get a value by key. If found, moves it to the front (most recently used).
    /// </summary>
    public bool TryGet(TKey key, out TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                // Move to front (most recently used)
                _list.Remove(node);
                _list.AddFirst(node);
                value = node.Value.Value;
                return true;
            }

            value = default!;
            return false;
        }
    }

    /// <summary>
    /// Adds or updates a key-value pair. If at capacity, evicts the least recently used item.
    /// </summary>
    public void Set(TKey key, TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                // Update existing: remove old node, add new at front
                _list.Remove(existing);
                var newNode = _list.AddFirst((key, value));
                _map[key] = newNode;
                return;
            }

            // Evict LRU if at capacity
            if (_map.Count >= _capacity)
            {
                var last = _list.Last!;
                _map.Remove(last.Value.Key);
                _list.RemoveLast();
            }

            // Add new entry at front
            var node = _list.AddFirst((key, value));
            _map[key] = node;
        }
    }

    /// <summary>
    /// Returns the number of items currently in the cache.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _map.Count;
            }
        }
    }

    /// <summary>
    /// Removes all items from the cache.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _list.Clear();
        }
    }
}
