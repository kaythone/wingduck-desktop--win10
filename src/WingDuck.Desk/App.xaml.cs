using System.Windows;
using WingDuck.Desk.Items;
using WingDuck.Desk.Settings;
using WingDuck.Desk.Shell;
using WingDuck.Desk.Windowing;

namespace WingDuck.Desk;

public partial class App : System.Windows.Application
{
    /// <summary>v1.1 条目 8：一条侧栏 = 一个窗口 + 一份自己的清单，成对生灭。</summary>
    private sealed record Bar(DockWindow Dock, ItemStore Store);

    private readonly List<Bar> _bars = [];
    private TrayIcon? _tray;
    private SettingsStore? _settingsStore;
    private AppSettings _settings = new();
    private SettingsWindow? _settingsWindow;
    private bool _shuttingDown;

    /// <summary>建栏过程中抑制落位回写：那几下 LayoutChanged 只是把磁盘上已有的几何又摆一遍。</summary>
    private bool _opening;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 两份进程各持一份清单，谁后 Save 谁覆盖对方——先让第二个进程退掉（DESIGN §10）
        if (!SingleInstance.TryAcquire())
        {
            SingleInstance.SignalExistingToShow();
            Shutdown();
            return;
        }

        // 出错的处置是"记日志 + 气泡 + 退出"，不弹裸堆栈框（DESIGN §10 末行）
        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            OnFatal("后台线程", args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()));

        CrashLog.Line($"启动 v{typeof(App).Assembly.GetName().Version}，配置目录 {AppPaths.Folder}" +
            (AppPaths.IsOverridden ? "（隔离实例）" : string.Empty));

        _settingsStore = new SettingsStore(AppPaths.Settings);
        _settings = _settingsStore.Load();
        SyncAutoStart();

        // Normalize 保证 Cards 非空且含 id=0，所以下面一定至少有一条栏、也一定有一条删不掉的原始栏
        foreach (var card in _settings.Cards.ToList()) OpenBar(card);

        _tray = new TrayIcon(_bars[0].Dock, ShowAllBars, () => _settings.AutoStart);
        _tray.SettingsRequested += ShowSettings;
        _tray.ExitRequested += Quit;
        // 托盘上点"新增卡片栏"没有"提意见的那条栏"可依，就接在最后一条后面
        _tray.AddCardRequested += () => AddCard(_settings.Cards[^1].Id);
        _tray.AutoStartToggleRequested += ToggleAutoStart;

        // 开机露面：静默进托盘在他看来等于"没启动"。展开一次让他看见侧栏贴着哪条边，
        // 豁免期满由那唯一的 60ms 节拍按正常手感收回（不新建定时器）。
        foreach (var bar in _bars) bar.Dock.HoldExpanded(DockMetrics.BootRevealMs);

        // 托盘钩子与唤醒监听都挂在原始那条上（它删不掉），所以标题也必须由它顶着 SingleInstance.WindowTitle
        SingleInstance.StartListening(_bars[0].Dock, () =>
        {
            // 第二个进程发完信号就退了，它自己不会再露面；这边不留一行日志，唤醒到底有没有收到无从判断
            CrashLog.Line("收到第二个实例的信号，侧栏露面");
            ShowAllBars();
        });

        WarnIfElevated();

        if (_bars.Any(b => b.Store.IsCorruptOnLoad))
            Say("收纳清单读坏了，已把它改名留底并另起一份空的；你的原文件都没动");
        else if (_bars.Any(b => b.Store.IsReadOnly))
            Say("这份清单来自更新的版本，这次只看不能改");
    }

    // ---- 侧栏的生灭 ----

    /// <summary>
    /// 照一条卡片设置建一条栏：清单按 id 分文件，几何从设置灌回来。
    /// 先以 0 不透明度走完整套落位再露面——Show 时窗口还是 XAML 默认的 80×320 空壳，
    /// 先亮了就会闪一下（T18 Step 3 明令不许闪）。
    /// </summary>
    private Bar OpenBar(CardSettings card)
    {
        var store = new ItemStore(AppPaths.ItemsFor(card.Id));
        store.Load();
        store.Validate(path => File.Exists(path) || Directory.Exists(path));

        var dock = new DockWindow(card.Id)
        {
            Store = store,
            EmptyHintSeen = _settings.EmptyHintSeen,
            VeilAlpha = _settings.VeilAlpha,
            AlongLength = card.AlongLength,
            Options = DockMetrics.ToOptions(_settings, card),
        };
        dock.NoticeRequested += Say;
        dock.HideRequested += () => _tray?.Balloon("已收到托盘，点托盘图标可以唤回");
        dock.LayoutChanged += () => RememberLayout(dock);
        dock.EmptyHintSeenChanged += seen => Commit(_settings with { EmptyHintSeen = seen });
        dock.AddCardRequested += AddCard;
        dock.DeleteRequested += DeleteCard;

        dock.Opacity = 0;
        _opening = true;
        try
        {
            dock.Show();
            if (card.Along != CardSettings.Unset) dock.ApplyAlongOffset(card.Along);
            dock.SnapTo(card.Edge.ToEdge());
            if (card.Pinned && card.X != CardSettings.Unset)
            {
                // 钉住的位置是用户亲手放的，先按边落位（顺带定下排列方向），再搬回去并转成钉住态
                dock.Left = card.X;
                dock.Top = card.Y;
                dock.SetPinned(true);
            }
            dock.Refresh();
        }
        finally
        {
            _opening = false;
        }
        dock.Opacity = 1;

        var bar = new Bar(dock, store);
        _bars.Add(bar);
        return bar;
    }

    /// <summary>右键"新增卡片栏"（条目 8）：同尺寸、紧贴提意见那条，边也照抄。</summary>
    private void AddCard(int fromCardId)
    {
        if (BarOf(fromCardId) is not { } source) return;
        var cards = _settings.Cards.ToList();
        var at = cards.FindIndex(c => c.Id == fromCardId);
        if (at < 0) return;

        // 紧贴要用它现在的末端，不是它上次落盘的位置：拖过但没触发保存就来点菜单时会贴错。
        // 磁盘值与实测值分两份拿：没定过位置的栏落盘是 Unset（留给下次重新居中），
        // 但"紧贴"必须有实数起点，所以那一步单独用窗口量出来的 AlongOffset。
        var stored = Snapshot(source.Dock, cards[at]);
        var live = stored with { Along = source.Dock.AlongOffset };
        cards[at] = stored;
        var fresh = CardLayout.Spawn(live, source.Store.Items.Count, CardLayout.NextId(cards), source.Dock.WorkAlong);
        cards.Add(fresh);
        Commit(_settings with { Cards = cards });
        OpenBar(fresh);
        CrashLog.Line($"新增侧栏 id={fresh.Id}（{fresh.Edge} 边，贴在 id={fromCardId} 后面）");
    }

    /// <summary>顶栏 ✕（条目 9）：确认框点头以后才真的删，而且删的只有我们自己的配置。</summary>
    private void DeleteCard(int id)
    {
        if (!CardDeleteConfirm.Deletable(id) || BarOf(id) is not { } bar) return;
        var cards = _settings.Cards.ToList();
        var at = cards.FindIndex(c => c.Id == id);
        if (at < 0) return;

        var dialog = new CardDeleteConfirm(bar.Store.Items.Count) { Owner = bar.Dock };
        if (dialog.ShowDialog() != true) return;

        var removed = Snapshot(bar.Dock, cards[at]);
        var removedLength = CardLayout.LengthOf(removed, bar.Store.Items.Count);
        bar.Dock.Hide();
        bar.Dock.Close();
        _bars.Remove(bar);

        cards.RemoveAt(at);
        // 紧贴着被删那条的同边栏整体前移（FR-16）；用户自己留出来的间隙不动
        var rest = CardLayout.CloseGap(cards, removed, removedLength, ItemCountOf, at);
        Commit(_settings with { Cards = rest });
        DeleteListFile(id);
        Say($"已删除这条侧栏。它收纳的 {bar.Store.Items.Count} 个快捷方式只是引用，原文件都在原来的位置");
    }

    /// <summary>删掉这条栏自己的清单文件。里面只有路径引用，没有任何用户文件（DESIGN §7.1）。</summary>
    private static void DeleteListFile(int cardId)
    {
        if (cardId == 0) return;
        try
        {
            var path = AppPaths.ItemsFor(cardId);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            // 残留一个没人读的 json 不影响能用，记一行就行
            CrashLog.Failure($"删除清单 items-{cardId}.json", ex);
        }
    }

    private Bar? BarOf(int cardId) => _bars.Find(b => b.Dock.CardId == cardId);

    private int ItemCountOf(CardSettings card) => BarOf(card.Id)?.Store.Items.Count ?? 0;

    /// <summary>托盘唤回：几条栏一起露面，每条都要各自的展开豁免期。</summary>
    private void ShowAllBars()
    {
        foreach (var bar in _bars)
        {
            bar.Dock.ShowFromTray();
            // 指针还停在托盘上，不豁免一下侧栏会在 400ms 内又收走（T16 Step 1 的"展开 3s"）
            bar.Dock.HoldExpanded(3000);
        }
    }

    // ---- 几何落盘 ----

    /// <summary>拖拽换边、拖边框改大小、钉住/解钉之后都要走这条：把实测几何写回那条栏的设置。</summary>
    private void RememberLayout(DockWindow dock)
    {
        if (_opening) return;
        var cards = _settings.Cards.ToList();
        var at = cards.FindIndex(c => c.Id == dock.CardId);
        if (at < 0) return;
        var next = Snapshot(dock, cards[at]);
        if (next == cards[at]) return;
        cards[at] = next;
        Commit(_settings with { Cards = cards });
    }

    /// <summary>
    /// 一条栏的实测几何 → <see cref="CardSettings"/>。落盘与"新栏紧贴上一条"共用这一份算法，
    /// 免得两处各自解释"末端在哪儿"而算出两个结果。
    /// </summary>
    private static CardSettings Snapshot(DockWindow dock, CardSettings at) => at with
    {
        // 钉住/悬浮态没有吸附边可言（_snap.Edge 被抹成 None），留它上一次贴过的那条边，解钉时才回得去
        Edge = dock.Edge == Edge.None ? at.Edge : dock.Edge.ToSettingName(),
        Pinned = dock.Pinned,
        Thickness = dock.ThicknessOverride,
        AlongLength = dock.AlongLength,
        // 用户没定过位置时不落实测值：那是我们算出来的中点，落成实数就等于禁止它以后重新居中
        Along = dock.AlongUserSet ? dock.AlongOffset : CardSettings.Unset,
        X = dock.Pinned ? dock.Left : CardSettings.Unset,
        Y = dock.Pinned ? dock.Top : CardSettings.Unset,
    };

    // ---- 提示 ----

    /// <summary>一句话同时落日志和弹气泡：日志是事后查证用的，气泡是当场要他看见的。</summary>
    private void Say(string message)
    {
        CrashLog.Line(message);
        _tray?.Balloon(message);
    }

    // ---- 设置（T17）----

    private void ShowSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(() => _settings, ApplyLive, Commit);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    /// <summary>拖滑块时走这条：只改运行中的行为，不碰磁盘。每条栏各拿自己的尺寸覆盖值。</summary>
    private void ApplyLive(AppSettings settings)
    {
        _settings = settings;
        foreach (var bar in _bars)
        {
            var card = settings.Cards.FirstOrDefault(c => c.Id == bar.Dock.CardId);
            bar.Dock.Options = DockMetrics.ToOptions(settings, card);
            bar.Dock.VeilAlpha = settings.VeilAlpha;
        }
    }

    /// <summary>落盘一次，并把顺带要同步的事做掉（自启开关）。</summary>
    private void Commit(AppSettings settings)
    {
        _settings = settings;
        _settingsStore?.Save(settings);
        SyncAutoStart();
    }

    private void SyncAutoStart()
    {
        try
        {
            AutoStart.Sync(_settings.AutoStart, CrashLog.Line);
        }
        catch (Exception ex)
        {
            // 注册表写不进去不影响侧栏能用，记一行就够，别为这个退掉
            CrashLog.Failure("自启同步", ex);
        }
    }

    /// <summary>托盘上的"开机自启"：走 Commit 那条老路（落 settings.json + 拧 Run 键），不另起一套判据。</summary>
    private void ToggleAutoStart()
    {
        Commit(_settings with { AutoStart = !_settings.AutoStart });
        // 面板可能正开着：让它跟着改勾，否则它关窗时会把手里那份旧值写回来
        _settingsWindow?.SyncAutoStartBox(_settings.AutoStart);
        Say(_settings.AutoStart
            ? "已开启开机自启，下次开机会自己露面"
            : "已关闭开机自启，开机后需要自己点托盘图标");
    }

    // ---- 降级与退出 ----

    /// <summary>管理员运行时桌面拖入会被 UIPI 静默拦掉，而且一句报错都没有（DESIGN §10）。</summary>
    private void WarnIfElevated()
    {
        try
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            if (new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                Say("请勿以管理员身份运行，否则从桌面拖入会被系统静默拦截");
        }
        catch (Exception ex)
        {
            CrashLog.Failure("权限检测", ex);
        }
    }

    private void OnUiException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        OnFatal("UI 线程", e.Exception);
        e.Handled = true;
    }

    private void OnFatal(string where, Exception ex)
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        CrashLog.Failure(where, ex);
        Say($"wingduck-desktop 出错了，日志在 {AppPaths.Log}");
        if (_tray is null)
            // 托盘还没起来就崩（启动期）：只剩一个 MessageBox 能让他看见日志在哪，不带堆栈
            System.Windows.MessageBox.Show($"wingduck-desktop 启动失败，日志在\n{AppPaths.Log}", "wingduck-desktop");
        _tray?.Dispose();
        SaveAllStores();
        Environment.ExitCode = 1;
        Shutdown();
    }

    private void Quit()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        CrashLog.Line("托盘退出");
        _tray?.Dispose();
        SaveAllStores();
        Shutdown();
    }

    /// <summary>退出前把每条栏的清单各写一次；一条写坏不该拖死其余几条。</summary>
    private void SaveAllStores()
    {
        foreach (var bar in _bars)
        {
            try { bar.Store.Save(); }
            catch (Exception ex) { CrashLog.Failure($"清单落盘 id={bar.Dock.CardId}", ex); }
        }
    }
}
