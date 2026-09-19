using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WingDuck.Desk.Items;

/// <summary>
/// <c>%APPDATA%\wingduck-desktop\items.json</c> 的读写（DESIGN §7.1）。
/// 三条硬规矩：写走临时文件 + 原子替换；版本比本机新就只读不写；文件被改坏就备份重来而不是崩。
/// </summary>
public sealed class ItemStore
{
    /// <summary>本机能写的格式版本。读到时只接受 ≤ 该值。</summary>
    public const int SchemaVersionSupported = 1;

    private readonly string _filePath;
    private readonly ObservableCollection<DockItem> _items = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public ItemStore(string filePath) => _filePath = filePath;

    /// <summary>读到了比本机支持的更新的格式：只读，Save 变空操作，绝不把新格式数据写坏。</summary>
    public bool IsReadOnly { get; private set; }

    /// <summary>JSON 解析失败。UI 据此弹提示（措辞见 DESIGN §10）。</summary>
    public bool IsCorruptOnLoad { get; private set; }

    public IReadOnlyList<DockItem> Items => _items;

    private sealed record StoredItem(string Id, string Name, string Path, ItemKind Kind, int Order);

    private sealed record StoredFile(int SchemaVersion, List<StoredItem> Items);

    public void Load()
    {
        IsReadOnly = false;
        IsCorruptOnLoad = false;
        _items.Clear();

        if (!File.Exists(_filePath)) return;

        StoredFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<StoredFile>(File.ReadAllText(_filePath), JsonOptions);
        }
        catch (JsonException)
        {
            IsCorruptOnLoad = true;
            Quarantine();
            return;
        }
        catch (IOException)
        {
            IsCorruptOnLoad = true;      // 文件被别的进程占着读不动，按损坏同样处理
            return;
        }

        if (parsed is null)
        {
            IsCorruptOnLoad = true;
            Quarantine();
            return;
        }

        if (parsed.SchemaVersion > SchemaVersionSupported) IsReadOnly = true;
        var order = 0;
        foreach (var item in (parsed.Items ?? []).OrderBy(i => i.Order))
            _items.Add(new DockItem(item.Id, item.Name, item.Path, item.Kind, order++));
    }

    /// <summary>把坏文件改名成 items.json.bad-&lt;时间戳&gt;，起一个空侧栏。</summary>
    private void Quarantine()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath) ?? ".";
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var target = Path.Combine(dir, $"{Path.GetFileName(_filePath)}.bad-{stamp}");
            File.Move(_filePath, File.Exists(target) ? target + "-" + Guid.NewGuid().ToString("N")[..4] : target);
        }
        catch (IOException)
        {
            // 备份失败也只影响取证，不该让程序起不来
        }
    }

    public void Save()
    {
        if (IsReadOnly) return;   // 新格式的数据不能按旧格式覆盖写回

        var dto = new StoredFile(SchemaVersionSupported,
            _items.Select(i => new StoredItem(i.Id, i.Name, i.Path, i.Kind, i.Order)).ToList());
        var text = JsonSerializer.Serialize(dto, JsonOptions);

        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // 临时文件必须与目标同卷，File.Replace 才有效，所以建在配置目录内
        var temp = _filePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, text);
            if (File.Exists(_filePath)) File.Replace(temp, _filePath, null);
            else File.Move(temp, _filePath);
        }
        catch (IOException)
        {
            TryDelete(temp);
            throw;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    /// <summary>加入条目。同路径（忽略大小写）视为重复，返回 false 表示没加。</summary>
    public bool Add(DockItem item)
    {
        if (Contains(item.Path)) return false;
        _items.Add(item);
        Reindex();
        return true;
    }

    public void Remove(string id)
    {
        var removed = false;
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            if (_items[i].Id != id) continue;
            _items.RemoveAt(i);
            removed = true;
        }
        if (removed) Reindex();
    }

    public void Move(string id, int newIndex)
    {
        var from = IndexOf(id);
        if (from < 0) return;
        var item = _items[from];
        _items.RemoveAt(from);
        _items.Insert(Math.Clamp(newIndex, 0, _items.Count), item);
        Reindex();
    }

    public void Rename(string id, string newName)
    {
        var index = IndexOf(id);
        if (index < 0) return;
        _items[index] = _items[index] with { Name = newName };
    }

    /// <summary>按 Id 定位。ObservableCollection 没有 FindIndex，自己走一遍比先转成 List 少一次拷贝。</summary>
    private int IndexOf(string id)
    {
        for (var i = 0; i < _items.Count; i++)
            if (_items[i].Id == id) return i;
        return -1;
    }

    /// <summary>逐条查存在性。失效条目一律保留，只打 IsMissing 标记（DESIGN §7.1）。</summary>
    public void Validate(Func<string, bool> exists)
    {
        for (var i = 0; i < _items.Count; i++)
            _items[i] = _items[i] with { IsMissing = !exists(_items[i].Path) };
    }

    public bool Contains(string path)
        => _items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Order 由这里统一重排成 0..n-1，外部传进来的值一律不信。</summary>
    private void Reindex()
    {
        for (var i = 0; i < _items.Count; i++) _items[i] = _items[i] with { Order = i };
    }
}
