using System.Text.Json.Serialization;

namespace WingDuck.Desk.Items;

/// <summary>条目类型，决定点击行为与图标兜底（DESIGN §7.1）。</summary>
public enum ItemKind
{
    Shortcut,
    Exe,
    Folder,
    File,
}

/// <summary>
/// 一条侧栏条目。<see cref="Path"/> 存的是<b>拖进来的那个文件本身</b>——
/// 快捷方式原样存 <c>.lnk</c>，绝不解析成目标路径，否则用户删掉快捷方式后条目就莫名其妙了。
/// </summary>
public sealed record DockItem(string Id, string Name, string Path, ItemKind Kind, int Order)
{
    /// <summary>运行期状态：文件当前是否还在。失效条目保留并置灰，不静默删除（DESIGN §7.1）。</summary>
    [JsonIgnore]
    public bool IsMissing { get; init; }
}
