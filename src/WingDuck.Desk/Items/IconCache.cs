using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WingDuck.Desk.Interop;

namespace WingDuck.Desk.Items;

/// <summary>
/// 48×48 图标缓存（DESIGN §3.3）。取图只有一条实测可用的路：
/// <c>SHCreateItemFromParsingName</c> → <c>IShellItemImageFactory.GetImage(48,48)</c>。
/// 容量默认 200，按 NFR-1 记账约 1.8MB（见 docs/PROJECT-MEMORY.md）。
/// </summary>
public sealed class IconCache
{
    private const int PixelSize = 48;

    private readonly LruCache<string, ImageSource> _cache;

    static IconCache()
    {
        // 线程可能已经被 CLR 初始化成 MTA，返回值一律忽略：能调通就行
        _ = Win32.CoInitializeEx(IntPtr.Zero, Win32.COINIT_APARTMENTTHREADED);
    }

    public IconCache(int capacity = 200) => _cache = new LruCache<string, ImageSource>(capacity);

    public int Count => _cache.Count;

    /// <summary>取图标。取不到时回落到首字母色块，绝不抛异常——图标没了不影响启动。</summary>
    public ImageSource Get(string path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            if (_cache.TryGet(path, out var cached)) return cached;
            var loaded = Load(path);
            if (loaded is not null)
            {
                _cache.Put(path, loaded);
                return loaded;
            }
        }
        return Fallback(path);   // 兜底图不缓存：文件后来出现时还要能拿到真图标
    }

    private static ImageSource? Load(string path)
    {
        var hbitmap = IntPtr.Zero;
        object? item = null;
        try
        {
            var iid = Win32.IID_IShellItemImageFactory;
            Win32.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item);
            if (item is not Win32.IShellItemImageFactory factory) return null;
            if (factory.GetImage(new Win32.SIZE(PixelSize, PixelSize), Win32.SIIGBF.ResizetoFit, out hbitmap) != 0
                || hbitmap == IntPtr.Zero)
                return null;

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            if (source.CanFreeze) source.Freeze();   // 冻结后才能跨线程用，也省下变更通知的开销
            return source;
        }
        catch (Exception)
        {
            return null;      // shell 说不清为什么失败，降级成色块就够
        }
        finally
        {
            if (hbitmap != IntPtr.Zero) _ = Win32.DeleteObject(hbitmap);   // 漏这行就是 GDI 泄漏，直接顶破 NFR-1
            if (item is not null) _ = Marshal.ReleaseComObject(item);
        }
    }

    private static readonly Color[] Palette =
    {
        // Flat UI Colors 的 au 系深色，与色罩 #1B262C 同一族（DESIGN §8.1）
        Color.FromRgb(0x34, 0x49, 0x5E), Color.FromRgb(0x5D, 0x6D, 0x7E), Color.FromRgb(0x16, 0xA0, 0x85),
        Color.FromRgb(0x39, 0x3F, 0x80), Color.FromRgb(0x4B, 0x60, 0x92), Color.FromRgb(0x7F, 0x8C, 0x8D),
        Color.FromRgb(0x2C, 0x3E, 0x50), Color.FromRgb(0x18, 0xBC, 0x9B),
    };

    private static ImageSource Fallback(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var letter = (name.Length > 0 ? name[0] : '?').ToString().ToUpperInvariant();
        var fill = new SolidColorBrush(Palette[(StableHash(name) & 0x7FFFFFFF) % Palette.Length]);
        fill.Freeze();

        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            dc.DrawRoundedRectangle(fill, null, new Rect(0, 0, PixelSize, PixelSize), 8, 8);
            var text = new FormattedText(
                letter, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 22, Brushes.White, 1.0)
            {
                TextAlignment = TextAlignment.Center,
                MaxTextWidth = PixelSize,
            };
            dc.DrawText(text, new Point(0, (PixelSize - text.Height) / 2));
        }
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static int StableHash(string value)
    {
        unchecked
        {
            var hash = 17;
            foreach (var c in value) hash = hash * 31 + c;
            return hash;
        }
    }
}
