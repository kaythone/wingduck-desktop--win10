; wingduck-desktop 安装包脚本（T21）
; 编译："$ISCC installer/wingduck-desktop.iss"（$ISCC 由 tools/env.sh 暴露）
; 前置：先跑 dotnet publish src/WingDuck.Desk -c Release -r win-x64 --self-contained `
;       -p:PublishSingleFile=true -o dist/portable

#define MyAppName "wingduck-desktop"
#define MyAppVersion "0.2.3"
; 系统下限 = DESIGN §2.4 的兼容性结论：Win10 1607（build 14393）起。
; 外面包一层 #ifndef：取证时可以从命令行临时抬高门槛编一份"没有系统能满足"的临时包，
; 验证那条拒绝分支真的会触发，而不用改这一行。实测用的值是 -DMyMinVersion=10.0.65535
; —— build 位最多到 65535，写 10.0.99999 ISCC 直接报错。
; 不包的话脚本里的 #define 会盖掉命令行那份（实测：2026-09-18 第一版就是这么静默失效的）。
#ifndef MyMinVersion
  #define MyMinVersion "10.0.14393"
#endif
; 注意写法：define 的值里不再做 {#…} 展开（ISPP 不会展开，会把字面量原样带进文件名），
; 要拼接只能用表达式
#define MySetupBaseFilename MyAppName + "-setup"

[Setup]
; AppId 一旦发布就不能改：升级靠它认"这是同一个程序"，改了就会装出第二份
AppId={#MyAppName}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppName}
VersionInfoVersion={#MyAppVersion}

; Win10 1607 以下直接拒绝安装，而不是让人双击了没反应再回来问。
; 两层卡点见 DESIGN §2.4：.NET 8 桌面运行时不支持 Win8.1 及更早，我们的 P/Invoke 又用到 Win10 才有的 GetDpiForWindow
MinVersion={#MyMinVersion}

; 装进用户目录（DESIGN §7.1）：不需要管理员，也躲开 Program Files 的虚拟化与 UIPI 拦截
DefaultDirName={localappdata}\Programs\{#MyAppName}
DisableDirPage=auto
DisableProgramGroupPage=yes
DefaultGroupName={#MyAppName}

; 普通用户权限运行（NFR-7 / ADR-4）。这条不是风格问题：以管理员装/跑，
; 从资源管理器往侧栏拖文件会被系统静默拦掉（UIPI），一句报错都没有
PrivilegesRequired=lowest

; 只出 64 位包（产物是 win-x64）。x64compatible 含 ARM64 上的 x64 模拟
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir=..\dist
OutputBaseFilename={#MySetupBaseFilename}
SetupIconFile=..\assets\wingduck.ico
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={{app}}\{#MyAppName}.exe

Compression=lzma2
SolidCompression=yes
WizardStyle=modern

; 升级/卸载前让 Setup 自己处理"程序正在运行"：它靠 Restart Manager 认被占用的 exe，
; 不靠 AppMutex（那个名字是 Local\ 前缀的，跨会话查不准）
CloseApplications=yes
RestartApplications=no

[Languages]
; 简体中文向导（用户 2026-09-17 第 4 条）。官方 Inno 包不带 ChineseSimplified.isl——它是站外翻译，
; 所以这份跟着仓库走：installer\Languages\ChineseSimplified.isl，取自 jrsoftware/issrc 的 main 分支，
; 头部标 "Inno Setup version 6.5.0+"，本机 6.7.3 编译通过。
; 只留一条语言：多一条开局就会弹语言选择框，他要的是"双击就往下走"。
; 路径不带 compiler: 前缀——那个前缀是相对 Inno 安装目录的，指不到仓库里这份。
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"

[Messages]
; MinVersion 不满足时，Inno 6.7 走的是这条（实测：改 WinVersionTooLowError 完全不生效，
; 弹出来的仍是 .isl 自带的"此程序不支持当前计算机运行的 Windows 版本"——只说了不支持，
; 没说要什么版本、该怎么办）。这里换成一句能照着走的话；不写 %1/%2，免得猜参数顺序；%n = 换行。
WindowsVersionNotSupported=wingduck-desktop 需要 Windows 10 版本 1607 或更高（Windows 11 也可以）。%n%n程序基于 .NET 8 桌面运行时，Win8.1、7、Vista、XP 跑不起来，所以安装在这里停下。

[Tasks]
; 桌面快捷方式默认不勾：桌面是用户自己摆的东西，安装器不该往上堆
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
; pdb 是调试符号，装出去没用还白占 32KB
Source: "..\dist\portable\*"; DestDir: "{app}"; Excludes: "*.pdb"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppName}.exe"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppName}.exe"; Tasks: desktopicon

[Registry]
; 自启项由程序自己在首次启动时按 settings.json 写（DESIGN §7.2 autoStart 默认 true），
; 安装器不代写，免得出现"注册表说开了、设置里说关了"的两张皮。
; 但卸载必须删干净：exe 都没了还留着 Run 值，每次登录都弹一次"找不到文件"
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: none; ValueName: "{#MyAppName}"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppName}.exe"; Description: "立即运行 {#MyAppName}"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 只清安装目录里的东西。**绝不列 %APPDATA%\wingduck-desktop**：
; 那里存的是用户挑的快捷方式引用（items.json），重装不该让他重挑一遍（DESIGN §7.1）
Type: filesandordirs; Name: "{localappdata}\Programs\{#MyAppName}"
