using System.Windows;

namespace WingDuck.Desk.Items;

/// <summary>
/// 拖放收录判定（DESIGN §9.2）。这里有一条数据安全的硬约束：
/// <see cref="AllowedEffect"/> 只能是 <see cref="DragDropEffects.Link"/>。
/// 从资源管理器拖文件时回报 Move 或 Copy，shell 会<b>真的把用户文件搬走或复制一份</b>——
/// 本项目只收纳引用，永远不许产生这个副作用。
/// </summary>
public static class DropIn
{
    /// <summary>唯一允许回报给拖放源的效果。引用收录 = 链接语义。</summary>
    public const DragDropEffects AllowedEffect = DragDropEffects.Link;

    /// <summary>
    /// 只收真实文件系统路径。以 <c>::</c> 开头的是 shell 命名空间项（回收站、这台电脑、
    /// 控制面板……），它们没有磁盘路径，存进去下次启动就是一条无法解析的死引用。
    /// </summary>
    public static bool IsAccepted(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("::", StringComparison.Ordinal)) return false;
        return File.Exists(path) || Directory.Exists(path);
    }

    /// <summary>类型只看磁盘上的实际形态与扩展名，决定点击行为与图标兜底。</summary>
    public static ItemKind Classify(string path)
    {
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return ItemKind.Shortcut;
        if (Directory.Exists(path)) return ItemKind.Folder;
        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return ItemKind.Exe;
        return ItemKind.File;
    }

    /// <summary>
    /// 把一次拖放的结果筛成可加入的条目：过滤不可收录项、与已有条目去重、批内去重。
    /// 返回的条目 <c>Order</c> 从 0 起，最终序号由 <see cref="ItemStore"/> 统一重排。
    /// </summary>
    public static IReadOnlyList<DockItem> Accept(
        string[] dropped, string[] existingPaths, Func<string, string> displayName)
    {
        var taken = new HashSet<string>(existingPaths, StringComparer.OrdinalIgnoreCase);
        var accepted = new List<DockItem>();
        foreach (var raw in dropped)
        {
            if (!IsAccepted(raw)) continue;
            var path = Path.GetFullPath(raw);
            if (!taken.Add(path)) continue;
            accepted.Add(new DockItem(NewId(), displayName(path), path, Classify(path), accepted.Count));
        }
        return accepted;
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];
}
