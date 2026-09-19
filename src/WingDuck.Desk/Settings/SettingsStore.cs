using System.Text.Json;
using System.Text.Json.Serialization;
using WingDuck.Desk.Windowing;

namespace WingDuck.Desk.Settings;

/// <summary>
/// `%APPDATA%\wingduck-desktop\settings.json` 的全部内容（DESIGN §7.2）。
/// 全局手感项一律要出现在设置窗口里——用户看不见就等于没有；
/// <see cref="Cards"/> 是唯一例外：每条栏的边、尺寸、钉住由界面上的拖拽与按钮就地改，不挤进设置窗口。
/// </summary>
public sealed record AppSettings
{
    /// <summary>色罩不透明度 0–255。默认 158 ≈ 62%。</summary>
    [JsonPropertyName("veilAlpha")]
    public int VeilAlpha { get; init; } = 158;

    [JsonPropertyName("collapseDelayMs")]
    public int CollapseDelayMs { get; init; } = DockMetrics.CollapseDelayMs;

    [JsonPropertyName("expandHoverMs")]
    public int ExpandHoverMs { get; init; } = DockMetrics.ExpandHoverMs;

    [JsonPropertyName("recollapseMs")]
    public int RecollapseMs { get; init; } = DockMetrics.RecollapseMs;

    /// <summary>
    /// v1.0 的顶层吸附边。<b>v1.1 起降级为只读迁移字段</b>：老文件没有 <see cref="Cards"/> 时用它造出原始那条，
    /// 此后一律以 <c>Cards[0].Edge</c> 为准，保存时写回 null（于是这个键从文件里消失）。
    /// 不留镜像是刻意的：镜像会在下次保存时把手改的值悄悄吃掉。
    /// </summary>
    [JsonPropertyName("edge")]
    public string? LegacyEdge { get; init; }

    [JsonPropertyName("autoStart")]
    public bool AutoStart { get; init; } = true;

    /// <summary>空态提示是否看过（T14 Step 5：第一次成功收录后永久不再提示）。</summary>
    [JsonPropertyName("emptyHintSeen")]
    public bool EmptyHintSeen { get; init; }

    /// <summary>每条侧栏一份（v1.1），顺序即创建顺序。<b>Normalize 保证非空且含 id=0。</b></summary>
    [JsonPropertyName("cards")]
    public IReadOnlyList<CardSettings> Cards { get; init; } = [];
}

/// <summary>
/// <c>%APPDATA%\wingduck-desktop\settings.json</c> 里的一条侧栏（DESIGN §7.2，v1.1）。
/// 尺寸两项 <b>0 = 自适应</b>：交叉方向按图标算（竖排 80 / 横排 92），沿边方向按条目数算；
/// 用户拖过边框之后才落成非零值。坐标一律 DIP，与 <c>DockWindow</c> 的 Left/Top/Width/Height 同单位。
/// </summary>
public sealed record CardSettings
{
    /// <summary>0 = 原始那条（清单文件 <c>items.json</c>，不给删）；N &gt; 0 用 <c>items-N.json</c>。</summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("edge")]
    public string Edge { get; init; } = "Left";

    /// <summary>钉住：不吸附、不自动收起、停在 X/Y（DESIGN §8.2 第五态）。</summary>
    [JsonPropertyName("pinned")]
    public bool Pinned { get; init; }

    [JsonPropertyName("thickness")]
    public double Thickness { get; init; }

    [JsonPropertyName("alongLength")]
    public double AlongLength { get; init; }

    /// <summary>
    /// 吸附态沿边起点偏移（相对工作区，DIP）。多条侧栏"紧贴上一条"靠它错开。
    /// <see cref="Unset"/> = 还没定过，开机用窗口当前位置；<b>0 是有意义的落点</b>（贴着工作区起点），不能当未设。
    /// </summary>
    [JsonPropertyName("along")]
    public double Along { get; init; } = Unset;

    /// <summary>钉住态的左上角。<see cref="Unset"/> 表示还没定过位置。</summary>
    [JsonPropertyName("x")]
    public double X { get; init; } = Unset;

    [JsonPropertyName("y")]
    public double Y { get; init; } = Unset;

    /// <summary>用 -1 而不是 NaN 当"未设"：System.Text.Json 默认写不出 NaN，而 -1 永远不是合法窗口坐标。</summary>
    public const double Unset = -1;
}

public static class AppSettingsExtensions
{
    /// <summary>边名 → 吸附边。认不出来一律 Edge.None（由调用方决定回落到哪条边）。</summary>
    public static Edge ToEdge(this string? name) => name switch
    {
        "Left" => Edge.Left,
        "Right" => Edge.Right,
        "Top" => Edge.Top,
        "Bottom" => Edge.Bottom,
        _ => Edge.None,
    };

    public static string ToSettingName(this Edge edge) => edge switch
    {
        Edge.Left or Edge.Right or Edge.Top or Edge.Bottom => edge.ToString(),
        _ => "Left",
    };
}

/// <summary>设置读写：真文件、原子替换、坏文件静默回默认（设置不值得像清单那样改名留底）。</summary>
public sealed class SettingsStore
{
    private readonly string _path;

    public SettingsStore(string path) => _path = path;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // 迁移字段（LegacyEdge）抹成 null 之后就该从文件里消失，而不是留一个 "edge": null 让人以为还有用
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return Normalize(new AppSettings());
            var read = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Json);
            return Normalize(read ?? new AppSettings());
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return Normalize(new AppSettings());
        }
    }

    public void Save(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $"settings.json.tmp-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(Normalize(settings), Json));
            if (File.Exists(_path)) File.Replace(temp, _path, null);
            else File.Move(temp, _path);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    /// <summary>
    /// 把越界值夹回合法区间。上限不是随手写的：收起延时 5s 以上等于"永不收起"，
    /// 收回延时短于展开确认会抖动画风，所以各自留了一条能用的下限。
    /// v1.1 多了一条硬保证：<b>返回值一定带非空 <c>Cards</c>，且里面有 id=0 那条</b>——
    /// 老文件（只有顶层 <c>edge</c>）在这里完成迁移，之后 <c>edge</c> 被抹成 null，保存时就不再落盘。
    /// </summary>
    public static AppSettings Normalize(AppSettings s)
    {
        var cards = s.Cards.Where(c => c.Id >= 0).ToList();
        if (cards.Count == 0)
            cards.Add(new CardSettings { Edge = ValidEdge(s.LegacyEdge) });
        else if (!cards.Any(c => c.Id == 0))
            cards[0] = cards[0] with { Id = 0 };   // 文件被手改坏了：最小那条认领原始侧栏

        // id 撞号会让两条侧栏共用一份 items 文件、互相覆盖，所以撞了就顺延
        var seen = new HashSet<int>();
        for (var i = 0; i < cards.Count; i++)
        {
            var id = cards[i].Id;
            while (!seen.Add(id)) id++;
            cards[i] = cards[i] with
            {
                Id = id,
                Edge = ValidEdge(cards[i].Edge),
                Thickness = cards[i].Thickness <= 0 ? 0 : Math.Clamp(cards[i].Thickness, DockMetrics.MinThickness, DockMetrics.MaxThickness),
                AlongLength = cards[i].AlongLength <= 0 ? 0 : Math.Clamp(cards[i].AlongLength, DockMetrics.MinAlongLength, 100_000),
                Along = cards[i].Along < 0 ? CardSettings.Unset : cards[i].Along,
                X = cards[i].X < CardSettings.Unset ? CardSettings.Unset : cards[i].X,
                Y = cards[i].Y < CardSettings.Unset ? CardSettings.Unset : cards[i].Y,
            };
        }

        return s with
        {
            VeilAlpha = Math.Clamp(s.VeilAlpha, 0, 255),
            CollapseDelayMs = Math.Clamp(s.CollapseDelayMs, 50, 5000),
            ExpandHoverMs = Math.Clamp(s.ExpandHoverMs, 0, 2000),
            RecollapseMs = Math.Clamp(s.RecollapseMs, 100, 10000),
            Cards = cards,
            LegacyEdge = null,
        };
    }

    private static string ValidEdge(string? name) => name.ToEdge() == Edge.None ? "Left" : name!;
}
