namespace WingDuck.Desk.Items;

/// <summary>
/// 定容量最近最少使用缓存。图标缓存用它兜住内存上限（DESIGN §12.3：条目可能上百个，
/// 但屏幕上一次最多十几个，其余留着就是白占）。非线程安全，只在 UI 线程用。
/// </summary>
public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _map = new();
    private readonly LinkedList<Entry> _order = new();     // 头 = 最近使用

    private readonly record struct Entry(TKey Key, TValue Value);

    public LruCache(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), "容量必须为正数");
        _capacity = capacity;
    }

    public int Count => _map.Count;

    public bool TryGet(TKey key, out TValue value)
    {
        if (_map.TryGetValue(key, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
        value = default!;
        return false;
    }

    public void Put(TKey key, TValue value)
    {
        if (_map.TryGetValue(key, out var existing))
        {
            existing.Value = new Entry(key, value);
            _order.Remove(existing);
            _order.AddFirst(existing);
            return;
        }

        var node = _order.AddFirst(new Entry(key, value));
        _map[key] = node;
        if (_map.Count > _capacity)
        {
            var oldest = _order.Last;
            if (oldest is not null)
            {
                _order.RemoveLast();
                _map.Remove(oldest.Value.Key);
            }
        }
    }
}
