# 23 号工单：工具栏那排按钮收进「文件」「操作」两个菜单。
#
# ⚠️ **【96 号票】那两条缺陷是这一票修掉/改掉的，先看这一段再读下面的正文。**
#   · **「文件」那个下拉今天已经不存在了** —— 42 号票（`38a4f73`）拆了它（用户原话：
#     「操作还是保留吧，只把『文件』这个下拉给拆掉」），导入 MIDI… 搬去曲库窗口右上角（55），
#     导出被「另存为…」吸收（54），保存 / 另存为… 回到工具栏成两颗按钮。⇒ 本文档正文里
#     凡是说「文件菜单 / 四条 / 导入 / 导出」的段落**都是 23 号当年的形态**，
#     今天的判据在 §1 / §3 / §4 / §5 里（都带【96 号票】的记号）。
#   · **脚本开头的清场从前是「无条件关掉桌面上所有 MidiPerformer」** —— 96 号票拆掉了，
#     今天**只按 PID 收自己起的那个**（见 §「起一个没有载入曲子的实例」那段注释）。
#
# 验收项逐条落到量上：
#
#   · **工具栏那一横带上有什么** —— 数窗口最上面那一横带里所有能点的控件（Button / Edit /
#     MenuItem），不靠「找找看在不在」。今天这一排是 42 号的排法 B：五颗按钮 + 歌名框 +
#     一个菜单（操作）。
#   · **菜单里的条目、顺序、右边的快捷键字** —— 菜单项右边那个 `Ctrl+Z`
#     是 `MenuItem.InputGesture` 渲染出来的，UIA 里就是 `AcceleratorKey` 属性，
#     读得到。**它只管显示，真按键是窗口级的 OnWindowKeyDown 接的**（代码里那句注释），
#     所以两件事要分开验：§6 验显示，§7/§8 验真按下去有效。
#     （`Ctrl+S` 那条 42 号之后换了载体：按钮没有菜单右侧那一栏，键位写进了按钮提示句，
#      UIA 里读 `HelpText`。）
#   · **没载入曲子时 保存 / 另存为… 灰着、「操作」整组关着** —— 本脚本开头**特意起一个
#     没载入曲子的实例**来量这一条（`open-app.ps1` 一上来就载曲，量不到空状态）。
#   · **撤销 / 重做 不可用时灰、且点了没反应；可用时真能用** —— 不光验灰，
#     还各点一次真的菜单条目（撤销走菜单、重做走菜单），看状态确实翻过去。
#   · **Ctrl+S 能保存，和菜单那条等价** —— 代码证据是两边都走 `SaveAsync`；行为证据是三段哈希：
#     ① 先按一次 Ctrl+S 取**对照**（此刻是刚载入、什么都没动的状态 S0，存出来的字节记作 C）；
#     ② 改一处（轨头 `+1`）再存，字节 D ≠ C；③ 撤销回 S0 再存，字节**精确回到 C**。
#     ③ 才是正主：同一个状态两次都写出同一串字节、中间那个状态写出另一串，
#     说明存进文件的确实是手上这份谱面，而不是「保存本身就会重写点别的」。
#
#     **有个坑要说明白**：头一次跑的时候我拿**磁盘上原来那份**当参照，三条全红。
#     查下来那份文件是上一次构建写下的（340831 字节），当前这版写出来是另一串
#     —— 保存会按当前格式把整份重写，不是「没动就照抄」。拿它当参照必然假红，
#     而那证明的不是 23 号坏了，是格式换代了。现在脚本把这件事**报出来**（大小 + 第一个
#     不同的字节），但不拿它判。曲库文件是构建产物（`bin/Debug/net8.0/songs/`，不被 git 管），
#     可它是**用户的数据**，所以全程备份、结尾无条件还原，并断言还原后字节回到原样。
#
# 三个坑（前两个是这次现踩的，第三个是从 22 号工单继承的）：
#
#  1. **Avalonia 顶级菜单项没有 `ExpandCollapsePattern`**（报「不支持的模式」），
#     展开只能靠真鼠标点一下。而它的弹出层是**另一个顶层 HWND** —— 直接
#     `RootElement.FindAll(MenuItem)` 会把 Windows Terminal 自己那排
#     File/Edit/Selection/View/… 一起扫进来（实测 11 个），全混在一起。
#     解药：**从菜单按钮这个元素往下找**，弹出层在 UIA 里挂在它底下（实测），
#     不用去猜哪个顶层窗口是 app 的。
#  2. **`Ctrl+S` / `Ctrl+Z` / `Ctrl+Y` 在焦点落进 TextBox 时整块失效** ——
#     `OnWindowKeyDown` 开头那句「焦点是 TextBox 就让开」是整段让开的（刻意的：
#     输入框里的 Ctrl+Z 归它自己的撤销）。所以按键之前**必须先把焦点从输入框里弄出来**，
#     否则会量出「快捷键没接上」这个假故障。点一下轨头那颗 `+1` 就够（Button 让开）。
#  3. **窗口尺寸是尺子的一部分**（继承 22 号）：`SetWindowPos` 摆的是外框、
#     UIA 报的是里面那圈，实测差 x+13 y+58 宽−26 高−71，要摆两次。
#     而且脚本块的参数名不能叫 `$h` —— 外层 `$h` 是窗口句柄，`param` 会把它盖掉。
#
# 用法: pwsh -NoProfile -File verify-23.ps1     （脚本自己起 app、自己收尾）
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @"
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public class V23 {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x,int y,int cx,int cy,uint f);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, IntPtr e);
  [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr a, IntPtr b);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X,Y; }
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  /// 屏幕上那个点上**实际**是哪个窗口 —— 「点下去了但没有反应」时唯一说得清话的证人：
  /// 前台是它、坐标也对，可那个点上是别的窗口的话，点击就根本没进 app。
  public static string HitTest(int x, int y, IntPtr want) {
    POINT p; p.X = x; p.Y = y;
    IntPtr got = WindowFromPoint(p);
    return (got == want) ? "这个点是本窗口" : "这个点上不是它（句柄 " + got + (got == IntPtr.Zero ? "，点了个空" : "") + "）";
  }
  public static string Cursor() { POINT p; GetCursorPos(out p); return p.X + "," + p.Y; }
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  /// 「这个点上是谁」只说得出句柄，得再补一句它**是谁** —— 抢前台那类问题全靠这个才认得出来。
  public static string Describe(IntPtr h) {
    if (h == IntPtr.Zero) return "（空）";
    var c = new StringBuilder(256); GetClassName(h, c, 256);
    var t = new StringBuilder(256); GetWindowText(h, t, 256);
    uint pid; GetWindowThreadProcessIdOut(h, out pid);
    return "句柄 " + h + " 类名'" + c + "' 标题'" + t + "' PID " + pid;
  }
  // 同一个 API 要两种用法（Take 里丢弃 pid、Others 里要 pid），而 `out` 是签名的一部分，
  // 一个声明顶不了两种 —— 于是按 EntryPoint 再来一个。
  [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
  public static extern uint GetWindowThreadProcessIdOut(IntPtr h, out uint pid);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  /// 把窗口抬到**非 topmost 那一层的最上面**。
  ///
  /// 为什么光有 SetForegroundWindow 不够：实测过一次，app **已经是前台**了，
  /// 可 VS Code 还压在它上面 —— 两个都不是 WS_EX_TOPMOST，纯粹是 z 序排在那儿
  /// （app 的 GW_HWNDPREV 往上数第 4 个就是 VS Code 的主窗）。前台和「谁在最上面」
  /// 是两码事。这种状态下的要命之处是 `mouse_event` 把点击交给**光标底下那个窗口**，
  /// 也就是 VS Code：菜单弹一下就被关掉，前台还跟着换成 VS Code —— 一串假红就是这么来的。
  ///
  /// TOPMOST 再 NOTOPMOST 是「挤到非 topmost 层最上面」的老办法：
  /// 如果直接留着 TOPMOST，app 自己的弹出菜单（另一个顶层窗）有被压在主窗底下的风险，
  /// 所以落回 NOTOPMOST。SWP_NOACTIVATE 是别跟下面的抢前台，前台归 Take 管。
  public static void Raise(IntPtr h) {
    SetWindowPos(h, new IntPtr(-1), 0,0,0,0, 0x0001|0x0002|0x0010);
    SetWindowPos(h, new IntPtr(-2), 0,0,0,0, 0x0001|0x0002|0x0010);
  }
  /// 这个点上到底是哪个窗口。前台说得再对也不算数，**点击只认这个**。
  public static IntPtr At(int x, int y) { POINT p; p.X = x; p.Y = y; return WindowFromPoint(p); }
  /// 这个点上那个窗口属于哪个进程。
  /// 判「是不是 app」不判「是不是 $h」：菜单一弹出来，点上的就是 app 自己的
  /// 弹出层（另一个顶层窗、另一个句柄），死抠句柄相等会把正常的菜单点击判成假红。
  public static uint PidAt(int x, int y) {
    IntPtr w = At(x, y);
    if (w == IntPtr.Zero) return 0;
    uint pid; GetWindowThreadProcessIdOut(w, out pid); return pid;
  }
  public static bool Take(IntPtr h) {
    IntPtr fg = GetForegroundWindow();
    uint a = GetWindowThreadProcessId(fg, IntPtr.Zero), b = GetWindowThreadProcessId(h, IntPtr.Zero);
    AttachThreadInput(a, b, true);
    keybd_event(0x12,0,0,IntPtr.Zero); keybd_event(0x12,0,2,IntPtr.Zero);
    SetForegroundWindow(h);
    if (GetForegroundWindow() != h) ShowWindow(h, 9);
    AttachThreadInput(a, b, false);
    // 这一抬是无条件的。原先它挂在「前台不是 h」的分支里，于是「已经是前台、
    // 但被别的窗口压着」这种状态**永远不会被抬** —— 而那正是实测到的状态。
    Raise(h);
    SetFocus(h); System.Threading.Thread.Sleep(300);
    return GetForegroundWindow() == h;
  }
  public static void Click(int x, int y) {
    SetCursorPos(x, y); System.Threading.Thread.Sleep(150);
    mouse_event(0x0002, 0,0,0, IntPtr.Zero); mouse_event(0x0004, 0,0,0, IntPtr.Zero);
    System.Threading.Thread.Sleep(330);
  }
  // 按一下再松开。**不用 SendKeys**：它把键投给「当前有焦点的控件」，
  // 而这几条快捷键恰恰是「不管焦点在谁身上、窗口层先吃掉」（TextBox 除外）——
  // 要走就得走正常输入队列（keybd_event 进的就是前台窗口那条）。
  public static void Key(byte vk) {
    keybd_event(vk, 0, 0, IntPtr.Zero);
    System.Threading.Thread.Sleep(50);
    keybd_event(vk, 0, 2, IntPtr.Zero);
    System.Threading.Thread.Sleep(200);
  }
  // 带 Ctrl 的一下。VK_CONTROL = 0x11。
  public static void Ctrl(byte vk) {
    keybd_event(0x11, 0, 0, IntPtr.Zero); System.Threading.Thread.Sleep(50);
    keybd_event(vk, 0, 0, IntPtr.Zero);   System.Threading.Thread.Sleep(50);
    keybd_event(vk, 0, 2, IntPtr.Zero);   System.Threading.Thread.Sleep(50);
    keybd_event(0x11, 0, 2, IntPtr.Zero); System.Threading.Thread.Sleep(300);
  }
  /// app 进程还开着的、够大的可见顶层窗（除了主窗）。模态对话框、演奏器窗都躲在这里面。
  /// **带尺寸下限**：Avalonia 自己会开一些细碎的可见顶层窗（提示、拖拽层），
  /// 不滤掉的话「有没有弹窗」这条判据天天误报。
  public static List<IntPtr> Others(uint want, IntPtr skip) {
    var list = new List<IntPtr>();
    EnumWindows((h,l) => {
      uint pid; GetWindowThreadProcessIdOut(h, out pid);
      if (pid != want || h == skip || !IsWindowVisible(h)) return true;
      RECT r; GetWindowRect(h, out r);
      if ((r.R - r.L) < 80 || (r.B - r.T) < 60) return true;
      list.Add(h);
      return true;
    }, IntPtr.Zero);
    return list;
  }
}
"@

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$条件 = { param($t) New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $t) }

$fail = 0
function 断言真([string]$名字, [bool]$条件, [string]$原文) {
  if ($条件) { "  OK   $名字（$原文）" } else { "  FAIL $名字（$原文）"; $script:fail++ }
}

# ---------- 收尾：**只收自己起的这一个**（按 PID，不枚举进程）----------
# 96 号票加的。从前这里没有收尾 —— 头一句「脚本自己起 app、自己收尾」在当时是句空话，
# 桌面上每跑一趟就多一个孤儿实例（66 号票 G-②：孤儿实例把全批锁死过十几分钟）。
# 形状照 `uitest-lib.ps1` 的 `收窗口` 抄：ShowWindow → CloseMainWindow → 不肯退就 Kill，
# **全部作用在自己记下的那个进程对象上**（`$proc`），不枚举、不问别人的实例是谁。
function 收自己的实例 {
  if ($null -eq $proc) { return }        # 还没起到进程就炸了 —— 没有可收的
  if ($proc.HasExited) { return }
  if ($h) { [void][V23]::ShowWindow($h, 9) }   # 「窗口句柄还没拿到」也算没收尾的一条路走到这儿
  [void]$proc.CloseMainWindow()
  if (-not $proc.WaitForExit(8000)) { $proc.Kill(); [void]$proc.WaitForExit(4000) }
}
# **中途抛了也要收**（拽不到前台、窗口没摆成、曲库是空的…）。brief §5a：「脚本无论怎么炸，
# 桌上都不能有你的实例」。退出码 3 = 跑到一半断了 —— 那不是「全过」，run-all 那边记成红。
# 收尾本身再抛也不能把 exit 3 吞掉（否则桌上留个孤儿实例、退出码还是 0）。
trap {
  "`n抛了：$($_.Exception.Message)"
  try { 收自己的实例 } catch { "  收尾时又抛了：$($_.Exception.Message) —— 这个实例可能还留在桌上" }
  exit 3
}

# ---------- 起一个**没有载入曲子**的实例 ----------
# 空状态（保存 / 另存为… 该是灰的）只在没载曲的时候存在，而 open-app.ps1 一上来就载曲，
# 所以这一条得脚本自己起进程来量。
$exe = Join-Path $PSScriptRoot '..\..\MidiPerformer.App\bin\Debug\net8.0\MidiPerformer.exe'
if (-not (Test-Path $exe)) { throw "没找到 $exe —— 先编译" }

# 🔴 **这里从前是「无条件把所有 MidiPerformer 都关掉」**（96 号票拆掉的那一段）：
#
#     Get-Process -Name MidiPerformer -EA SilentlyContinue | ForEach-Object {
#       [void][V23]::ShowWindow($_.MainWindowHandle, 9)
#       [void]$_.CloseMainWindow()
#       if (-not $_.WaitForExit(8000)) { $_.Kill() }
#     }
#
# 它问都不问那实例是谁的，枚举的是「所有叫 MidiPerformer 的进程」—— **用户自己正开着的那一份、
# 别的 agent 正在验的那一份都在里面**，跑这一条就把人家的窗口收走了（66 号票 G-⑧ 记过，
# 一直没人负责修）。共用库早就改掉了这个写法：`uitest-lib.ps1:127-138` 那一段 ——
# 有实例在跑**只报出来、停手**（原文「先关掉再跑（这个脚本不替你关）」），它自己的
# `收窗口` 也只收 `$script:proc` 那一个 PID。
#
# 本脚本不点源那个库，所以照它那半边的意思办：**一个别人的实例都不碰**；
# 自己那一个按 PID 收（收尾在脚本末尾，`收自己的实例`）。
#
# ⚠️ 代价说明白：桌面上真有别人的实例时，这一条**不再替它清场** —— 两个实例会抢前台，
#    量出来的红**先怀疑这个**（那份红说的不是产品坏了，是桌上不干净）。
$proc = Start-Process -FilePath $exe -PassThru
$期限 = (Get-Date).AddSeconds(30)
do { Start-Sleep -Milliseconds 500; if ($proc.HasExited) { throw "窗口没起来，退出码 $($proc.ExitCode)" }; $proc.Refresh() }
while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $期限)
if ($proc.MainWindowHandle -eq 0) { throw '等不到窗口句柄' }
Start-Sleep -Seconds 3
$h = $proc.MainWindowHandle
$脚本PID = [uint32]$proc.Id
"起了个干净实例：PID $($proc.Id)"

if (-not [V23]::Take($h)) { throw '拽不到前台' }
$摆 = { param($摆X, $摆Y, $摆宽, $摆高)
  [void][V23]::SetWindowPos($h, [IntPtr]::Zero, $摆X, $摆Y, $摆宽, $摆高, 0x0004 -bor 0x0010)
  Start-Sleep -Milliseconds 700
  ($AE::FromHandle($h)).Current.BoundingRectangle
}
$r1 = & $摆 405 450 2360 1520
$win = & $摆 (810 - [int]$r1.X) (900 - [int]$r1.Y) (4720 - [int]$r1.Width) (3040 - [int]$r1.Height)
"窗口 $([int]$win.Width)x$([int]$win.Height) @ $([int]$win.X),$([int]$win.Y)"
if ([int]$win.Width -ne 2360 -or [int]$win.Height -ne 1520) { throw "窗口没摆成 2360x1520（摆完是 $([int]$win.Width)x$([int]$win.Height)）" }
# 这里**不能**只看一次 Take 的返回值就算数：后面那个「等它准备好」的循环要跑好几秒，
# 前台完全可能在这中间被别的窗口抢走（实测就是这样，见 要前台 上面那段）。
# 真正管用的关口是每次输入前的 要前台，这里只做一次起步检查。
if (-not [V23]::Take($h)) { throw '摆完窗口之后拽不到前台' }

# **等它真的能收鼠标，再开始点。**
#
# 这条是踩出来的：同一段代码，探针 `probe-menufirst.ps1` 前后跑了两次 —— 一次连点五次
# 一次都弹不出来（前台也是它、坐标也是对的），另一次头一下就是四条。
# 差别只在「点的时候窗口起来多久了」，所以这是个**启动竞态**，不是「没载曲时菜单不开」。
# 判据不看时钟，看**歌曲名框在不在 UIA 树里**（它在工具栏那一行上，它在了就说明这一行画完了），
# 然后再多给两秒让输入那套接上。
foreach ($等 in 1..20) {
  $root = $AE::FromHandle($h)
  $有 = @($root.FindAll($TS::Descendants, (& $条件 $CT::Edit)) | Where-Object { $_.Current.AutomationId -eq 'SongNameBox' })
  if ($有.Count) { break }
  Start-Sleep -Milliseconds 500
}
Start-Sleep -Seconds 2
$root = $AE::FromHandle($h)
function 找类型([object]$类型) { @($root.FindAll($TS::Descendants, (& $条件 $类型))) }
function 按编号([string]$id) {
  @(@(找类型 $CT::Text) + @(找类型 $CT::Button) + @(找类型 $CT::Edit)) |
    Where-Object { $_.Current.AutomationId -eq $id }
}
function 数轨 { @(找类型 $CT::Button | Where-Object { $_.Current.Name -eq '折叠' }).Count }

# ---------- 菜单：展开 / 读条目 / 点条目 ----------
# 顶级菜单按钮（`文件` / `操作`）。它俩是 MenuItem，但**没有 ExpandCollapsePattern**，
# 只能真点一下才弹出（见文件头第 1 个坑）。
function 菜单按钮([string]$名) {
  @(找类型 $CT::MenuItem) | Where-Object { $_.Current.Name -eq $名 } | Select-Object -First 1
}
# **要重试。** 刚起完进程那一下点菜单经常弹不出来（实测第一次 `读菜单 '文件'` 返回空表，
# 于是「没载曲时三个条目是灰的」三条一起假红 —— 看着像按钮没灰，其实菜单压根没开）。
# 弹不出来就再点一次；重试之前先按 Esc —— 万一是「弹开了但 UIA 没看见」，
# 直接再点一下等于把它关掉，越试越开不了。
function 展开([string]$名, [int]$最多 = 6) {
  for ($次 = 1; $次 -le $最多; $次++) {
    $b = 菜单按钮 $名
    if (-not $b) { throw "找不到菜单「$名」" }
    $r = $b.Current.BoundingRectangle
    要前台 "展开「$名」第 $次"
    [V23]::Click([int]$r.X + [int]($r.Width / 2), [int]$r.Y + [int]($r.Height / 2))
    # **点完马上轮询，不要睡一大觉再回来看。**
    #
    # 这条是量出来的关键：这台机器上有个窗口（句柄 262180，见下面的诊断）会在一秒多之后
    # 把前台抢走，而 **Avalonia 一失活就把弹出层收掉**。原来这里是「睡 700~1300 毫秒再看」，
    # 于是每一次都恰好落在收掉之后 —— 六次全空，看着像「菜单压根打不开」。
    # 现在每 100 毫秒看一眼，弹出层一出现就读，抢前台那一下还没发生。
    $项 = @()
    foreach ($等 in 1..12) {
      Start-Sleep -Milliseconds 100
      # 从**菜单按钮**往下找，不去 RootElement 里捞（那里混着 Windows Terminal 自己那排菜单）
      $项 = @($b.FindAll($TS::Descendants, (& $条件 $CT::MenuItem)))
      if ($项.Count) { return $项 }
    }
    # 点空了就把当时的实情报出来 —— 少了这几行，「菜单没弹开」和「弹到别处去了」
    # 和「点在了空坐标上」三种情况在输出里长得一模一样，只能靠猜。
    #
    # **必须走 Write-Host，不能用字符串。** 函数里写进管道的每一行都是它的**返回值**：
    # 上一版这里是 `"    [展开…]"`，于是六次失败攒出六个字符串被当成「六条菜单项」返回，
    # 第 1 节打印出来是六行空白（字符串上没有 `.名` 这个属性），报「读得出四条（6 条）」——
    # 诊断信息自己把现场搅了。（和 22 号那次 `[void](函数)` 吞掉整条管道是同一类坑。）
    Write-Host "    [展开「$名」第 $次没成] 按钮框=$([int]$r.X),$([int]$r.Y) $([int]$r.Width)x$([int]$r.Height)；按钮启用=$($b.Current.IsEnabled)；点在 $([int]$r.X + [int]($r.Width / 2)),$([int]$r.Y + [int]($r.Height / 2))；光标现在在 $([V23]::Cursor())"
    Write-Host "        现在的前台：$([V23]::Describe([V23]::GetForegroundWindow()))；app 是 $([V23]::Describe($h))"
    [V23]::Key(0x1B); Start-Sleep -Milliseconds 300
  }
  @()
}
# **点之前必须把前台拽回来，而且拽不回来就抛。**
#
# 这条是这次最花时间的一个坑。现象：第 1 节连点六次一次都弹不出来，而同一个脚本往下跑
# 到第 3 节就正常了；更早的探针里同一段代码还跑出过「头一下就是四条」。
# 加上「这个点上到底是哪个窗口」这一问（`WindowFromPoint`）才看清：
#
#     前台是它=False；点在 476,500 → 这个点上不是它（句柄 262180）
#
# —— 前台**根本不是 app**，鼠标那一下打在了另一个窗口上，所以菜单当然不开。
# 它为什么丢的前台：`Take()` 在 `展开` 里是用 `[void]` 调的，**返回值被丢了**，
# 失败一声不吭；刚起完进程那会儿前台又被别的窗口抢走（脚本是 `Start-Process` 起的 app，
# 前台随后被终端那类窗口拿回去了）。往下走到第 3 节之所以好了，是因为第 2 节
# 用 UIA 的 `SetFocus` + `SendKeys` 载曲子，那把前台**顺带**还给了 app —— 纯属侥幸。
#
# 所以：每次输入之前都拽一次并**当场验**，拽不回来直接抛。宁可红，不要量出一堆假数。
# 【2026-09-20 补】上面那段「拽前台」还是不牢。又逮到一次，量出来的原话是：
#     前台：[21128] Avalonia-… / 'MIDI 演奏器'        ← 前台**就是** app
#     476,500 上是：[6100] Chrome_RenderWidgetHostHWND / 'Chrome Legacy Window'
#     窗口正中 1585,1210 上是：[6100] 同上
# 两个都不是 WS_EX_TOPMOST，纯粹是 VS Code 的主窗在 z 序上压着 app
# （顺着 app 的 GW_HWNDPREV 往上数，第 4 个就是 '● 需要的功能.txt … Visual Studio Code'）。
#
# 也就是说：**前台是 app ≠ 点击会进 app**。鼠标走的是「光标底下那个窗口」，
# 前台只是键盘的去处。所以判据不能只有 GetForegroundWindow，得连点上是谁一起判。
# 现在的 `Take` 每次都会无条件把窗口抬到非 topmost 层的最上面（原来那一抬挂在
# 「前台不是 h」的分支里，而实测到的恰恰是「前台是 h、却被压着」，于是永远不抬）。
function 要点上([int]$横, [int]$纵, [string]$谁) {
  if ([V23]::PidAt($横, $纵) -ne $脚本PID) {
    $在 = [V23]::Describe([V23]::At($横, $纵))
    throw "「$谁」要点的 $横,$纵 上不是 app（是 $在）—— 点击会打到那个窗口上，量出来全是假的"
  }
}
function 要前台([string]$谁) {
  for ($i = 1; $i -le 6; $i++) {
    [void][V23]::Take($h)
    if ([V23]::GetForegroundWindow() -eq $h) {
      Start-Sleep -Milliseconds 250
      # 前台稳住了还不算数，窗口正中那一点也得真属于 app（被别的窗口盖住就不属于）
      $中横 = [int]($win.X + $win.Width / 2)
      $中纵 = [int]($win.Y + $win.Height / 2)
      if ([V23]::GetForegroundWindow() -eq $h -and [V23]::PidAt($中横, $中纵) -eq $脚本PID) { return }
      Write-Host ("    [「$谁」前台是它了，但窗口正中 $中横,$中纵 上压着 " +
                  "$([V23]::Describe([V23]::At($中横, $中纵))) —— 再抬一次]")
    }
    Start-Sleep -Milliseconds 500
  }
  throw ("「$谁」之前没能让 app 既在前台、又没被压住（前台是 " +
         "$([V23]::Describe([V23]::GetForegroundWindow()))，app 是 $([V23]::Describe($h))，" +
         "窗口正中那点是 $([V23]::Describe([V23]::At([int]($win.X + $win.Width/2), [int]($win.Y + $win.Height/2)))))" +
         " —— 再往下点/按都会打到别的窗口上，量出来全是假的")
}
function 收菜单 { [V23]::Key(0x1B); Start-Sleep -Milliseconds 500 }   # 0x1B = Esc
# 鼠标和键盘都走这两个口子，谁也别绕过「要前台」。
# 点还多一道：**要点的那个点**上必须真属于 app。窗口正中没被压住，不代表左上角
# 那个菜单按钮没被压住 —— 验到点上才作数，这一道就是 2026-09-20 那次假红的对症药。
function 点([int]$横, [int]$纵, [string]$谁) { 要前台 $谁; 要点上 $横 $纵 $谁; [V23]::Click($横, $纵) }
function 按Ctrl([byte]$键) { 要前台 "Ctrl+$('{0:X2}' -f $键)"; [V23]::Ctrl($键) }
function 读菜单([string]$名) {
  $项 = 展开 $名
  $o = @($项 | ForEach-Object {
    [pscustomobject]@{ 名 = $_.Current.Name; 启用 = $_.Current.IsEnabled; 键 = $_.Current.AcceleratorKey }
  })
  收菜单
  $o
}
# 点一条菜单项。**优先走 UIA 的 Invoke，不走鼠标。**
#
# 顶级菜单按钮没有 Invoke（探针量过：「不支持的模式」），但**弹出层里那些条目有**。
# 走 Invoke 的好处是绕开了鼠标那一整套脆弱环节：不用移光标、不用 SetCursorPos、
# 也不受「这个点上现在是哪个窗口」影响。而真鼠标那条路在这儿是**真的会输**：
# 从「弹出层出现」到「鼠标按下去」中间要过 要前台（可能几百毫秒），
# 这段时间里前台一旦被抢走，Avalonia 立刻把弹出层收掉，那一下鼠标就点在空气上了 ——
# 表现出来是「点了撤销，什么都没发生」（实测踩到过一次，见文件头）。
# 鼠标那条留着兜底，走不通再退回去。
function 点条目([string]$菜单, [string]$条目) {
  $项 = 展开 $菜单
  $t = @($项 | Where-Object { $_.Current.Name -eq $条目 })[0]
  if (-not $t) { throw "「$菜单」里没有「$条目」" }
  $怎么点的 = 'Invoke'
  try {
    $t.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  } catch {
    $怎么点的 = "真鼠标（Invoke 不行：$($_.Exception.Message)）"
    $r = $t.Current.BoundingRectangle
    [V23]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
  }
  Write-Host "    [点菜单项] 「$菜单」→「$条目」走的是 $怎么点的"
  Start-Sleep -Milliseconds 900
  [V23]::Key(0x1B); Start-Sleep -Milliseconds 400
}
function 条目状态([string]$菜单, [string]$条目) {
  $o = 读菜单 $菜单
  $t = @($o | Where-Object { $_.名 -eq $条目 })[0]
  if (-not $t) { throw "「$菜单」里没有「$条目」" }
  $t.启用
}
function 没别的窗 { [V23]::Others($脚本PID, $h).Count }

"`n=== 1. 还没载入曲子：存盘那两颗该是灰的，「操作」整组该是关着的 ==="
# 【96 号票重写过这一节：它测的那件事今天还在，只是换了位置 —— 乙案的路 1】
# 23 号当年量的是「文件」下拉里那四条（导入 MIDI… / 保存 / 另存为… / 导出）的灰亮，
# 而 **42 号票（38a4f73）把这个下拉拆掉了**：保存 / 另存为… 回到工具栏上成了两颗按钮，
# 导入 MIDI… 搬去曲库窗口右上角（55 号），导出被「另存为…」吸收掉（54 号）。
# ⇒ 「没载曲时存盘那几样灰着」这件事仍然成立、仍然要量，**只是落点在两颗按钮上**，
#   所以改成去新位置读（路 1）。（「一个『文件』菜单里排着四条」这个形态本身按路 3 删掉，
#   理由见 §4。）
$空保存 = 按编号 'SaveButton'
$空另存为 = 按编号 'SaveAsButton'
# **先断言这两样真读到了。** 读不到的话下面那两条会**白白通过**（`-not $null` 那个坑 23 号踩过：
# 菜单没弹出来，一堆「是灰的」全过）。存在性判据必须站在灰亮判据前面，这是那一节的教训。
断言真 '工具栏上读得到「保存」按钮' ($空保存.Count -eq 1) "$($空保存.Count) 个"
断言真 '工具栏上读得到「另存为…」按钮' ($空另存为.Count -eq 1) "$($空另存为.Count) 个"
断言真 '没载曲时「保存」是灰的' ($空保存.Count -eq 1 -and -not $空保存[0].Current.IsEnabled) `
  "启用=$(if ($空保存.Count) { $空保存[0].Current.IsEnabled } else { '找不到' })"
断言真 '没载曲时「另存为…」是灰的' ($空另存为.Count -eq 1 -and -not $空另存为[0].Current.IsEnabled) `
  "启用=$(if ($空另存为.Count) { $空另存为[0].Current.IsEnabled } else { '找不到' })"
# 「操作」那一组在空态下**整组灰**（42 号：`OperationMenu.IsEnabled = _song is not null`）。
# 而它一灰，**弹出层根本打不开** —— 90 号在真机上量过原话：头 能点=False、弹出层 0 条。
# ⇒ 这一节不问「撤销 / 重做那两条灰不灰」（问不到），问的是那件事本身：**这道门关着**。
$空操作头 = 菜单按钮 '操作'
断言真 '空状态下「操作」那个头在场' ($null -ne $空操作头) $(if ($空操作头) { '在' } else { '找不到' })
断言真 '没载曲时「操作」整组是灰的（弹出层打不开 ⇒ 撤销 / 重做都够不着）' `
  ($null -ne $空操作头 -and -not $空操作头.Current.IsEnabled) `
  "启用=$(if ($空操作头) { $空操作头.Current.IsEnabled } else { '找不到' })"
断言真 '没载曲时播放键也是灰的（确认这真是个空实例）' `
  (-not (按编号 'PlayButton')[0].Current.IsEnabled) ''
# 最后补一颗**必须是亮的**：「歌曲库」—— 它是上面那一片「是灰的」的阳性对照
# （全灰里得有一点亮，才说明量到的是状态，不是「整窗没了」）。这不是我编的哨兵，
# 是 42 号票验收原话：「空状态：… `保存`/`另存为…`/`操作`/`演奏` 灰掉，**「歌曲库」亮着**」。
#
# ⚠️ **别拿它当「提权框没开着」的反证 —— 它证明不了，96 号票量过了**：启动那颗提权框
# （「要以管理员身份重启吗？」）开着的时候，逐秒读 25 秒，读数是
# `SaveButton=False  SaveAsButton=False  LibraryButton=True  PlayButton=False`，一动不动。
# 也就是说那颗框**不动 UIA 的 `IsEnabled`**（它吃的是**输入** —— 点击、按键）。
# ⇒ 它既不会让上面那些「是灰的」白白通过，也不会把这一颗变灰；
#   真正会被它咬到的是后面那些**真点击、真按键**，防它在 §2 末尾那句「没别的窗」。
$空歌曲库 = 按编号 'LibraryButton'
断言真 '空状态下「歌曲库」是亮的（42 号的阳性对照：一片灰里这一点必须亮）' `
  ($空歌曲库.Count -eq 1 -and $空歌曲库[0].Current.IsEnabled) `
  "$($空歌曲库.Count) 个，启用=$(if ($空歌曲库.Count) { $空歌曲库[0].Current.IsEnabled } else { '找不到' })"

# 把曲库窗口开出来（模态），返回**它**的顶层窗句柄。
# 两句都是 96 号票现踩出来的：
#  · **点它走 UIA 的 Invoke，不走鼠标** —— 那颗按钮是普通 Button，InvokePattern 现成的；
#    鼠标那条路要先拽前台、再验「这个点上是谁」，而它一按就开模态窗，前面那些步骤全是白搭。
#  · **认窗口不认「第几个」**：`[V23]::Others` 是按**我记下的那个 PID** 筛本进程的顶层窗
#    （不是枚举别人的进程），再按 UIA 名字认「歌曲库」—— 这个名字就是 XAML 里的 Title。
function 开曲库窗口([int]$最多秒 = 20) {
  $b = 按编号 'LibraryButton'
  if ($b.Count -ne 1) { throw "工具栏上找不到「歌曲库」那颗按钮（读到 $($b.Count) 个）" }
  $b[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  $期限 = (Get-Date).AddSeconds($最多秒)
  do {
    Start-Sleep -Milliseconds 400
    foreach ($wh in [V23]::Others($脚本PID, $h)) {
      if ($AE::FromHandle($wh).Current.Name -eq '歌曲库') { return $wh }
    }
  } while ((Get-Date) -lt $期限)
  throw "点了「歌曲库」但 $最多秒 秒里没等出那个窗口"
}

"`n=== 2. 从曲库里点开一首 ==="
# 【96 号票重写过这一节：它测的那件事今天还在，只是换了窗口 —— 乙案的路 1】
# 这一段从前是 `找类型 $CT::ListItem` —— 在**主窗口**的 UIA 树里找曲库的行。
# 可 **40 号票（9ed0d4f）把曲库从主窗口里的一块面板升成了独立的模态窗口**
# （`SongLibraryWindow`，标题「歌曲库」），行从此住在**另一个顶层窗**里，主窗口的树里
# 一条都没有 ⇒ 从 40 号落地那一刻起，这一段在**任何环境下**都必然抛「曲库是空的」，
# 后面 §3–§8 一节都跑不到（和「文件」菜单那条一个病：一条恒红的门，只是它抛而不是红）。
# ⇒ 改成先把窗口开出来（工具栏「歌曲库」那颗按钮），再去**那个窗口自己的根**上找行。
$库 = 开曲库窗口
$库根 = $AE::FromHandle($库)
$行 = @($库根.FindAll($TS::Descendants, (& $条件 $CT::ListItem)))
"曲库窗口 hwnd=$库，列着 $($行.Count) 行"
断言真 '曲库窗口开出来了，里面列着曲子' ($行.Count -gt 0) `
  "$($行.Count) 行$(if ($行.Count -eq 0) { '（一行都没有 ⇒ 看 bin\Debug\net8.0\songs 里有没有 .mid）' })"
if ($行.Count -eq 0) { throw '曲库是空的（窗口开着，一行都没有）' }
$目标 = $null
foreach ($it in $行) {
  foreach ($x in $it.FindAll($TS::Descendants, (& $条件 $CT::Text))) {
    if ($x.Current.Name -eq 'Carulli_Duetto_No2_Op4') { $目标 = $it }
  }
}
if (-not $目标) { throw '曲库里没有 Carulli_Duetto_No2_Op4' }
# 模态窗才是现在的本窗口 —— **前台要拽的是它，不是 $h**（$h 这会儿被它压着、还是禁用态，
# 拽 $h 只会拽来一个量不出东西的窗口）。Take 是通用函数，收句柄，不挑是谁。
if (-not [V23]::Take($库)) { throw '拽不到曲库窗口的前台' }
[void]$目标.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 400
[void]$目标.SetFocus()
Start-Sleep -Milliseconds 400
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
Start-Sleep -Seconds 4
$轨数 = 数轨
"载入完：$轨数 条轨"
if ($轨数 -lt 1) { throw '没载进来 —— 一条轨都没有' }
断言真 '曲库点开之后真的载进来了' ($轨数 -ge 1) "$轨数 条轨"
# 【96 号票改过这一条：同一件事，但把「撞上就抛」改成「先等它走，到点还开着再点名抛」】
# 它防的是**模态框把后面的点击按键吃掉**（§3 起要真点菜单项、真按 Ctrl+Z / Ctrl+S）。
# 会在这儿露头的是启动那颗提权框（「要以管理员身份重启吗？」）：它归 60/67 号那颗
# **驱动层**的看门狗去按，**本脚本一个字节不动它**。看门狗是轮询的，桌上有好几个实例时
# 它得排队按 —— 我那一个就可能**到这一刻还没被按掉**（96 号实测撞到过一次：§2 在这儿抛了；
# 探针一开，还开着的那一个顶层窗正是「要以管理员身份重启吗？」）。
# 那不是产品坏了，是**看门狗还没轮到**，所以给它一个上限去走：走了就往下跑，
# 到点还开着才抛 —— 抛的时候**把还开着的是谁报出来**（原来是「还有别的顶层窗开着」，
# 查起来只能靠猜）。
function 等没别的窗([int]$最多秒 = 25) {
  $期限 = (Get-Date).AddSeconds($最多秒)
  do {
    $开 = @([V23]::Others($脚本PID, $h))
    if ($开.Count -eq 0) { return @() }
    Start-Sleep -Milliseconds 500
  } while ((Get-Date) -lt $期限)
  $开
}
$还开着 = @(等没别的窗)
if ($还开着.Count -ne 0) {
  # 报的时候**句柄 + 类名 + 标题 + UIA 名 + 尺寸**全给上，别只给一个名字：
  # 实测撞到过一个 UIA 名是**空串**的顶层窗（`Describe` 那三样才认得出它是谁）。
  $名字 = ($还开着 | ForEach-Object {
    $u = try { $AE::FromHandle($_).Current.Name } catch { '（UIA 读不到）' }
    "$([V23]::Describe($_)) 尺寸 $([V23]::Size($_)) UIA名='$u'"
  }) -join ' / '
  throw "载入之后还有别的顶层窗开着：$名字 —— 模态框会把后面所有点击按键都吃掉"
}
"  载入之后没有别的顶层窗（模态框都关了）"

function 读位置 { (按编号 'PositionText')[0].Current.Name }
$总小节 = if ((读位置) -match '/\s*(\d+)') { [int]$Matches[1] } else { -1 }
if ($总小节 -ne 124) { throw "载入的不是那一首（$总小节 小节）" }

"`n=== 3. 载入之后：存盘那两颗变亮，「操作」这道门开 ==="
# 【96 号票】落点跟 §1 一样改成两颗按钮（理由见 §1 抬头）。
# **元素重新取一次**：载曲子那一趟会重建一批自动化 peer，§1 拿到的那个引用不能当 §3 的读数用。
$有保存 = 按编号 'SaveButton'
$有另存为 = 按编号 'SaveAsButton'
断言真 '载了曲「保存」就亮了' ($有保存.Count -eq 1 -and $有保存[0].Current.IsEnabled) `
  "启用=$(if ($有保存.Count) { $有保存[0].Current.IsEnabled } else { '找不到' })"
断言真 '载了曲「另存为…」就亮了' ($有另存为.Count -eq 1 -and $有另存为[0].Current.IsEnabled) `
  "启用=$(if ($有另存为.Count) { $有另存为[0].Current.IsEnabled } else { '找不到' })"
$有操作 = 读菜单 '操作'
$有操作 | ForEach-Object { "  $($_.名)：启用=$($_.启用) 键='$($_.键)'" }
断言真 '载入状态下「操作」菜单读得出两条' ($有操作.Count -eq 2) "$($有操作.Count) 条"
断言真 '刚载入、什么都没动时「撤销」是灰的' (@($有操作 | Where-Object { $_.名 -eq '撤销' })[0].启用 -eq $false) ''
断言真 '刚载入、什么都没动时「重做」是灰的' (@($有操作 | Where-Object { $_.名 -eq '重做' })[0].启用 -eq $false) ''

"`n=== 4. 条目顺序与右边的快捷键字 ==="
# 【96 号票】这一节原来第一条是「「文件」里四条，顺序是 导入 MIDI… / 保存 / 另存为… / 导出」。
# **按路 3 整段删掉**，理由（不是「红了就删」，是它测的那件事今天有意不成立）：
#   · 42 号票（38a4f73）**拆掉了「文件」这个下拉**（`MainWindow.axaml` 里现在只有
#     `OperationMenu`，没有「文件」顶级菜单）—— 用户原话：「操作还是保留吧，只把『文件』这个下拉给拆掉」；
#   · 导入 MIDI… 搬去了曲库窗口右上角（55 号），导出被「另存为…」吸收（54 号），
#     保存 / 另存为… 回到工具栏成按钮（42 号）。
# ⇒ 「一个『文件』菜单里排着四条」这个**形态本身**被后续票取消了 ⇒ 留着它是一条恒红的门。
#   今天这件事的判据在 `ToolbarLayoutTests.cs`（排法B的次序 / 存盘组里是保存和另存为两颗 /
#   导入那颗从工具栏撤干净了 / 文件那个下拉拆掉了）；这一节只留「操作」那两条（形态没变）。
断言真 '「操作」里两条，顺序是 撤销 / 重做' `
  ((@($有操作 | ForEach-Object { $_.名 }) -join '|') -eq '撤销|重做') `
  (@($有操作 | ForEach-Object { $_.名 }) -join ' / ')
断言真 '「撤销」右边写着 Ctrl+Z' `
  ((@($有操作 | Where-Object { $_.名 -eq '撤销' })[0].键) -eq 'Ctrl+Z') `
  "'$(@($有操作 | Where-Object { $_.名 -eq '撤销' })[0].键)'"
断言真 '「重做」右边写着 Ctrl+Y' `
  ((@($有操作 | Where-Object { $_.名 -eq '重做' })[0].键) -eq 'Ctrl+Y') `
  "'$(@($有操作 | Where-Object { $_.名 -eq '重做' })[0].键)'"
# 「保存」那条键位（原来是菜单项右侧的 `AcceleratorKey`）：42 号之后它**换了载体** ——
# 按钮没有菜单右侧那一栏，键位写进了按钮的提示句里（路 1 / 路 2：换位置 + 换形态）。
# Avalonia 把 ToolTip 露成 UIA 的 `HelpText`，不用悬停就读得到（verify-25 / 26 都这么读）。
$保存提示 = if ($有保存.Count -eq 1) { $有保存[0].Current.HelpText } else { '' }
断言真 '「保存」的提示里写着 Ctrl+S（键位从菜单右侧搬到了按钮提示里）' `
  ($保存提示 -like '*Ctrl+S*') "'$保存提示'"

"`n=== 5. 工具栏那一带上只剩什么 ==="
# 工具栏那一带 = **和两个菜单同一行**的那些控件。
#
# 不写成「窗口顶上 150 像素」：滚动的 `PART_LineUpButton`（ScrollBar 模板里那个箭头，
# name 就是 'Line up'）落在 102..134，正好在那一带里，于是「只剩一个按钮」会假红。
# 拿菜单自己的框当那一行的上下界，它是什么尺寸、在哪一行就都跟着走，不用手调数字。
# 【96 号票】锚原来是「文件」那个下拉，**它已经没了**（42 号拆的）⇒ 锚改到今天这一排里
# 仍在的那个菜单头「操作」上。带的上下界跟着它走，跟原来一样不靠手调数字。
$行参考 = 菜单按钮 '操作'
$行上 = $行参考.Current.BoundingRectangle.Y - 6
$行下 = $行参考.Current.BoundingRectangle.Y + $行参考.Current.BoundingRectangle.Height + 6
# 把这一带里**所有能点的东西**列出来，「只剩这几个」才有意义 —— 只验「菜单在」
# 的话，旧按钮原封不动照样能过。
$带内 = @()
foreach ($类型 in @($CT::Button, $CT::Edit, $CT::MenuItem, $CT::ComboBox, $CT::Menu)) {
  foreach ($e in 找类型 $类型) {
    $r = $e.Current.BoundingRectangle
    if ([double]::IsInfinity($r.X)) { continue }
    if ($r.Y -ge $行上 -and ($r.Y + $r.Height) -le $行下) {
      $带内 += [pscustomobject]@{ 类型 = $e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''; 名 = $e.Current.Name; 编号 = $e.Current.AutomationId; 框 = "$([int]$r.X - [int]$win.X),$([int]$r.Y - [int]$win.Y) $([int]$r.Width)x$([int]$r.Height)" }
    }
  }
}
$带内 | Sort-Object 框 | ForEach-Object { "  [$($_.类型)] name='$($_.名)' id='$($_.编号)' 框=$($_.框)" }

$带内按钮 = @($带内 | Where-Object { $_.类型 -eq 'Button' })
$带内编辑 = @($带内 | Where-Object { $_.类型 -eq 'Edit' })
$带内菜单 = @($带内 | Where-Object { $_.类型 -eq 'MenuItem' })
# 【96 号票】这三条断言原来量的是 23 号那个排法（工具栏上只剩「演奏器…」一颗按钮 + 两个顶级菜单
# 文件 / 操作）。42 号票的**排法 B** 有意把这一排改成了
# `歌曲库 | [保存│另存为…] | 操作 ▾ | 演奏 | 打开日志文件夹`（90 号把「打开日志文件夹」从
# 「操作」里搬到了台面上）⇒ 断言改成**测今天这个形态**（乙案的路 2）。
# **按集合比，不排序** —— `Sort-Object` 在中文上有自己的排法（实测「操作」排在「文件」前面），
# 拿排序后的串去对是自找的假红。
$期望按钮 = @('歌曲库', '保存', '另存为…', '演奏', '打开日志文件夹')
$带内按钮名 = @($带内按钮 | ForEach-Object { $_.名 })
断言真 '工具栏上那几颗按钮就是 歌曲库 / 保存 / 另存为… / 演奏 / 打开日志文件夹' `
  ($带内按钮.Count -eq $期望按钮.Count -and @($期望按钮 | Where-Object { $带内按钮名 -notcontains $_ }).Count -eq 0) `
  "$($带内按钮.Count) 个：$($带内按钮名 -join ' / ')"
断言真 '工具栏上只剩一个输入框，就是歌曲名框' `
  ($带内编辑.Count -eq 1 -and $带内编辑[0].编号 -eq 'SongNameBox') `
  "$($带内编辑.Count) 个：$(($带内编辑 | ForEach-Object { "$($_.编号)='$($_.名)'" }) -join ' / ')"
$带内菜单名 = @($带内菜单 | ForEach-Object { $_.名 })
断言真 '工具栏上只剩一个顶级菜单：操作' `
  ($带内菜单.Count -eq 1 -and ($带内菜单名 -contains '操作')) `
  "$($带内菜单.Count) 个：$($带内菜单名 -join ' / ')"
# ⛔ 这里原来还有一条「那一排旧按钮（导入/保存/另存为/导出/撤销/重做）一个都不在工具栏上了」。
# **96 号票按路 3 删掉它**：42 号票**有意**把「保存 / 另存为…」放回了工具栏（存盘组）——
# 那条断言的前提（「收进菜单」）已经被 42 号反转，留着它是一条**恒红**的门。
# 那件事今天的判据在 `MidiPerformer.Tests/Visual/ToolbarLayoutTests.cs` 里
# （「排法B的次序」「导入那颗从工具栏撤干净了」「文件那个下拉拆掉了」）。

"`n=== 6. 撤销 / 重做：灰的时候点不动，亮的时候真能用 ==="
# 先把焦点从输入框里弄出去（点一下轨头那颗 `+1`，Button 不在让开之列）。
$加一 = @(找类型 $CT::Button | Where-Object { $_.Current.Name -eq '+1' })
if ($加一.Count -lt 1) { throw '找不到轨头的 +1 按钮' }
$移调读 = @(按编号 'TransposeText')
function 读移调 { $移调读[0].Current.Name }
$改前 = 读移调
$r = $加一[0].Current.BoundingRectangle
点 ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2)) '点轨头那颗 +1（既改谱面，也把键盘焦点从输入框里挪出来）'
Start-Sleep -Milliseconds 900
$改后 = 读移调
"点了一下轨头 +1：移调读数 '$改前' → '$改后'（轨数 $轨数，'$改后' 那一颗是 $($加一.Count) 个 +1 里的第一个）"
断言真 '点「+1」确实改了谱面（移调读数变了）' ($改后 -ne $改前) "'$改前' → '$改后'"
断言真 '改了之后「撤销」就亮了' ((条目状态 '操作' '撤销') -eq $true) ''
断言真 '改了之后「重做」还是灰的（没有可重做的）' ((条目状态 '操作' '重做') -eq $false) ''

# 点一次**灰着**的重做：该什么都不发生
$点前位置 = 读位置
点条目 '操作' '重做'
断言真 '灰着的「重做」点下去没反应（状态不动、谱面不动、不弹窗）' `
  (((条目状态 '操作' '撤销') -eq $true) -and ((条目状态 '操作' '重做') -eq $false) -and `
   ((读位置) -eq $点前位置) -and ((没别的窗) -eq 0)) `
  "撤销=$(条目状态 '操作' '撤销') 重做=$(条目状态 '操作' '重做') 读数 $点前位置→$(读位置) 别的窗 $((没别的窗)) 个"

点条目 '操作' '撤销'
断言真 '点菜单里的「撤销」真的撤销了（撤销变灰、重做变亮）' `
  (((条目状态 '操作' '撤销') -eq $false) -and ((条目状态 '操作' '重做') -eq $true)) `
  "撤销=$(条目状态 '操作' '撤销') 重做=$(条目状态 '操作' '重做')"
断言真 '撤销之后移调读数回到原样' ((读移调) -eq $改前) "'$(读移调)'（原是 '$改前'）"

点条目 '操作' '重做'
断言真 '点菜单里的「重做」真的重做了（重做变灰、撤销变亮）' `
  (((条目状态 '操作' '撤销') -eq $true) -and ((条目状态 '操作' '重做') -eq $false)) `
  "撤销=$(条目状态 '操作' '撤销') 重做=$(条目状态 '操作' '重做')"
断言真 '重做之后移调读数又回到改完的样子' ((读移调) -eq $改后) "'$(读移调)'（改完是 '$改后'）"

# 键盘那一半：Ctrl+Z / Ctrl+Y（焦点此刻在轨头那颗按钮上，不是输入框，让开那一句不生效）
按Ctrl(0x5A)
断言真 'Ctrl+Z 和菜单里那条是一回事（撤销变灰、重做变亮）' `
  (((条目状态 '操作' '撤销') -eq $false) -and ((条目状态 '操作' '重做') -eq $true)) `
  "撤销=$(条目状态 '操作' '撤销') 重做=$(条目状态 '操作' '重做')"
按Ctrl(0x59)
断言真 'Ctrl+Y 和菜单里那条是一回事（重做变灰、撤销变亮）' `
  (((条目状态 '操作' '撤销') -eq $true) -and ((条目状态 '操作' '重做') -eq $false)) `
  "撤销=$(条目状态 '操作' '撤销') 重做=$(条目状态 '操作' '重做')"
按Ctrl(0x5A)
断言真 '再 Ctrl+Z 回到干净（撤销变灰）' ((条目状态 '操作' '撤销') -eq $false) ''
断言真 '这一步之后谱面回到原样（移调读数 = 起点）' ((读移调) -eq $改前) "'$(读移调)'（原是 '$改前'）"

"`n=== 7. Ctrl+S 保存，和菜单里那条等价 ==="
# 【96 号票】这一节的**判据一个字没改**，改的是它盯的那份文件 —— 还是乙案的路 1（换了位置）。
# 23 号当年盯的是 `songs\<名字>.mproj`。今天「保存」写的是**两份**（`SongCache.Save` 原文）：
#   ① `songs\<名字>.mid`          —— 你的文件（`MidiWriter` 把移调叠进音高，导出成标准 MIDI）
#   ② `songs\.work\<名字>.mproj`  —— 本程序自己的缓存（**移调、删光的轨、轨的身份都住这儿**）
# 而顶层那个 `songs\<名字>.mproj` 是**旧布局留下的**（`SongLibrary.Write` 今天一个调用者都没有），
# 现程序根本不碰它 ⇒ 拿它当判据**必然恒红**：我实测 Ctrl+S 连按三下，它的哈希一动不动，
# 而 app 日志里三条「已存进曲库」（`%LOCALAPPDATA%\MidiPerformer\logs\`，20:24:20 / 20:24:25 / 20:24:34）。
# ⇒ 判据挪到**缓存**上：模型就落在它里面，移调变没变在那儿看得见。
$曲库目录 = Join-Path (Split-Path $exe) 'songs'
$曲 = Join-Path $曲库目录 'Carulli_Duetto_No2_Op4.mid'
$缓存 = Join-Path (Join-Path $曲库目录 '.work') 'Carulli_Duetto_No2_Op4.mproj'
foreach ($必在 in @($曲, $缓存)) { if (-not (Test-Path $必在)) { throw "找不到 $必在" } }
# **两份都要备份、都要还原** —— 保存把两份都重写了；只还一份等于把用户的数据留在改过的状态。
# （修前只备份了顶层那个 `.mproj`，而保存压根不碰它 ⇒ 那段「无条件还原」是空的，
#  真正被重写的 `.mid` 没人管。这是顺手补的，不是新需求：那一段本来就写着「无论如何都还原」。）
$备份 = "$曲.verify23-bak"
$缓存备份 = "$缓存.verify23-bak"
Copy-Item $曲 $备份 -Force
Copy-Item $缓存 $缓存备份 -Force
$原哈希 = (Get-FileHash $曲 -Algorithm SHA256).Hash
$缓存原哈希 = (Get-FileHash $缓存 -Algorithm SHA256).Hash
"你的文件 $曲（$((Get-Item $曲).Length) 字节，原哈希 $($原哈希.Substring(0,12))…）—— 已备份"
"缓存 $缓存（$((Get-Item $缓存).Length) 字节，原哈希 $($缓存原哈希.Substring(0,12))…）—— 已备份"

function 读哈希 { (Get-FileHash $缓存 -Algorithm SHA256).Hash }      # 判据盯的是缓存
function 读mid哈希 { (Get-FileHash $曲 -Algorithm SHA256).Hash }     # 你的文件那份：只报不判
function 等文件写入([string]$旧哈希, [int]$最多毫秒 = 8000) {
  $等 = (Get-Date).AddMilliseconds($最多毫秒)
  do { Start-Sleep -Milliseconds 300 } while ((读哈希) -eq $旧哈希 -and (Get-Date) -lt $等)
  (读哈希)
}
try {
  # ---------- 热身：先改一处、再撤销 ----------
  # 【96 号票加的】不是为了量什么，是为了把文件头里那个**粘性**的 `Edited` 标记落实下来
  # （`MainWindow.axaml.cs`：一编辑就置 `true`，只有 `LoadSong` 会清它 —— 存盘不清）。
  # 不热身的话：基线那次保存写出去的是 `Edited=false`，而改动撤销之后 `Edited` 仍是 `true`
  # ⇒ 最后那条「撤销回去的字节该精确等于基线」**必然红**，而且**每一趟都红** ——
  # 红的原因不是谱面变了，是那个标记变了（等于又造了一条恒红的门）。
  # 热身之后三次保存的头部信息一个样，比的才是谱面本身。
  $r0 = $加一[0].Current.BoundingRectangle
  点 ([int]($r0.X + $r0.Width / 2)) ([int]($r0.Y + $r0.Height / 2)) '热身：点一下 +1（只为把「编辑过」那个标记落实下来）'
  Start-Sleep -Milliseconds 800
  按Ctrl(0x5A)
  Start-Sleep -Milliseconds 800
  "热身：改一处再撤销回来，移调回到 '$(读移调)'（这一条不断言，它只是下面三条判据的前提）"

  # 对照组：**没改过**的时候按一次 Ctrl+S，把此刻的缓存字节记作「基线」。
  #
  # 拿**这次的基线**当参照，不拿盘上原来那份：头一次保存会按当前格式与当前头部信息整份重写，
  # 字节本来就该变 —— 拿原来那份当参照必然假红（23 号当年就在这一段踩过一次，
  # 那次的教训写在下面这两行读数里，保留）。
  $前 = 读哈希
  $前mid = 读mid哈希
  按Ctrl(0x53)
  Start-Sleep -Milliseconds 1500
  $对照 = 读哈希
  $对照mid = 读mid哈希
  "对照：没改过时按 Ctrl+S —— 缓存 $($前.Substring(0,12))… → $($对照.Substring(0,12))…（$(if ($对照 -eq $前) { '一模一样' } else { '换了一串' })）"
  "  你的文件（.mid）$($前mid.Substring(0,12))… → $($对照mid.Substring(0,12))…（只报不判：它是导出的 MIDI，字节对不对不是这一节的判据）"
  "  盘上原来那份：缓存 $((Get-Item $缓存备份).Length) 字节 / .mid $((Get-Item $备份).Length) 字节；现在：$((Get-Item $缓存).Length) / $((Get-Item $曲).Length)"
  断言真 '保存没弹出任何对话框' ((没别的窗) -eq 0) "$((没别的窗)) 个别的窗"

  # 正主：改一处、Ctrl+S、**缓存**该变（移调就落在缓存里）
  $r = $加一[0].Current.BoundingRectangle
  点 ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2)) '点轨头那颗 +1（改一处谱面，好让保存有东西可写）'
  Start-Sleep -Milliseconds 900
  断言真 '要先真的改了谱面才谈得上保存' ((读移调) -ne $改前) "移调 '$(读移调)'"
  按Ctrl(0x53)
  $存后 = 等文件写入 $对照
  "改完按 Ctrl+S：缓存 $($对照.Substring(0,12))… → $($存后.Substring(0,12))…"
  断言真 'Ctrl+S 把改动写进文件了（缓存字节确实变了）' ($存后 -ne $对照) ''

  # 撤销 + 再存一次：**该精确回到基线**。
  # 这一条才是「保存的确实是手上这份谱面」的正主 —— 上一轮改动被撤销之后，
  # 文档内容回到了对照那一刻的样子，那么再存一次就该一字不差地写出同一串字节。
  按Ctrl(0x5A)
  Start-Sleep -Milliseconds 600
  按Ctrl(0x53)
  $回后 = 等文件写入 $存后
  "撤销之后再按 Ctrl+S：缓存 $($存后.Substring(0,12))… → $($回后.Substring(0,12))…"
  断言真 '撤销之后存回去，字节精确回到对照那一刻（存的确实是手上这份谱面，来回自洽）' ($回后 -eq $对照) `
    "$($回后.Substring(0,12))… 对基线 $($对照.Substring(0,12))…"
} finally {
  # 曲库那两份都是**用户的数据**（虽然躺在 bin 里）。不管上面成没成，无条件还原。
  Copy-Item $备份 $曲 -Force
  Copy-Item $缓存备份 $缓存 -Force
  Remove-Item $备份 -Force
  Remove-Item $缓存备份 -Force
  $还原 = (Get-FileHash $曲 -Algorithm SHA256).Hash
  $缓存还原 = (Get-FileHash $缓存 -Algorithm SHA256).Hash
  "已还原 $曲：哈希 $($还原.Substring(0,12))…$(if ($还原 -eq $原哈希) { '（和原样一致）' } else { '（！！！和原样不一致）' })"
  "已还原 $缓存：哈希 $($缓存还原.Substring(0,12))…$(if ($缓存还原 -eq $缓存原哈希) { '（和原样一致）' } else { '（！！！和原样不一致）' })"
  if ($还原 -ne $原哈希) { $script:fail++; "  FAIL 你的文件（.mid）没能还原成原样" }
  if ($缓存还原 -ne $缓存原哈希) { $script:fail++; "  FAIL 缓存（.work）没能还原成原样" }
}

"`n=== 8. 「演奏」那颗按钮还在，点了还是开一个窗 ==="
# 【96 号票】这一节从前找的是名字以「演奏器…」开头的那颗按钮 —— **40 号票（9ed0d4f）
# 把它改名成「演奏」了**（`MainWindow.axaml`：`Content="演奏"`），于是「找不到」直接抛。
# 现在按 **AutomationId `PerformerButton`** 找（名字会改，id 不会）。
# 注意**窗口**还叫「演奏器」—— 改名的是工具栏那颗按钮，不是那个窗口
# （`PerformerWindow.axaml`：`Title="演奏器"`），下面那条断言照旧。
$演奏 = @(按编号 'PerformerButton')[0]
if (-not $演奏) { throw '找不到工具栏那颗「演奏」按钮（PerformerButton）' }
$开前 = 没别的窗
$r = $演奏.Current.BoundingRectangle
点 ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2)) '点工具栏那颗「演奏」'
Start-Sleep -Seconds 2
$开后 = [V23]::Others($脚本PID, $h)
"点之前 $开前 个别的窗，点之后 $($开后.Count) 个"
断言真 '点「演奏」开出了一个新窗口' ($开后.Count -gt $开前) "$开前 → $($开后.Count)"
if ($开后.Count) {
  $演根 = $AE::FromHandle($开后[0])
  "  那个窗：'$($演根.Current.Name)'"
  断言真 '开出来的窗是演奏器那个' ($演根.Current.Name -like '*演奏器*') "'$($演根.Current.Name)'"
  [void][V23]::PostMessage($开后[0], 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)   # WM_CLOSE
  Start-Sleep -Seconds 2
  断言真 '关掉之后它真的没了' ((没别的窗) -eq 0) "$((没别的窗)) 个"
}

"`n=== 收尾：只收自己起的那个实例（别人的一个都不碰）==="
收自己的实例
$自己还在 = -not $proc.HasExited
"  自己起的 PID $($proc.Id)：$(if ($自己还在) { '还在（没收掉）' } else { '已收掉' })"
断言真 '收尾把自己起的实例收掉了' (-not $自己还在) "PID $($proc.Id) 还在=$自己还在"

""
if ($fail) { "$fail 条不过" } else { '全过' }
# 81 号票：修前这一行是 `if ($fail) { "$fail 条不过"; exit 1 } else { '全过' }` ——
# 绿的半边是「走到底自然退 0」，红的半边才显式 exit。裁决行 + 末尾统一 exit，两头都传得出去。
"==== uitest 裁决 不过=$fail"
exit $fail
