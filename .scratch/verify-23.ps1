# 23 号工单：工具栏那排按钮收进「文件」「操作」两个菜单。
#
# 验收项逐条落到量上：
#
#   · **工具栏只剩两菜单 + 歌曲名框 + 演奏器按钮** —— 数窗口最上面那一横带里
#     所有能点的控件（Button / Edit / MenuItem），不靠「找找看在不在」。
#   · **两个菜单里的条目、顺序、右边的快捷键字** —— 菜单项右边那个 `Ctrl+S`
#     是 `MenuItem.InputGesture` 渲染出来的，UIA 里就是 `AcceleratorKey` 属性，
#     读得到。**它只管显示，真按键是窗口级的 OnWindowKeyDown 接的**（代码里那句注释），
#     所以两件事要分开验：§6 验显示，§7/§8 验真按下去有效。
#   · **没载入曲子时 保存 / 另存为 / 导出 灰着** —— 本脚本开头**特意起一个没载入曲子的实例**
#     来量这一条（`open-app.ps1` 一上来就载曲，量不到空状态）。
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

# ---------- 起一个**没有载入曲子**的实例 ----------
# 空状态（保存/另存为/导出 该是灰的）只在没载曲的时候存在，而 open-app.ps1 一上来就载曲，
# 所以这一条得脚本自己起进程来量。
$exe = Join-Path $PSScriptRoot '..\MidiPerformer.App\bin\Debug\net8.0\MidiPerformer.exe'
if (-not (Test-Path $exe)) { throw "没找到 $exe —— 先编译" }

Get-Process -Name MidiPerformer -EA SilentlyContinue | ForEach-Object {
  [void][V23]::ShowWindow($_.MainWindowHandle, 9)
  [void]$_.CloseMainWindow()
  if (-not $_.WaitForExit(8000)) { $_.Kill() }
}
Start-Sleep -Milliseconds 900

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

"`n=== 1. 还没载入曲子：三个「文件」条目该是灰的 ==="
$空文件 = 读菜单 '文件'
$空文件 | ForEach-Object { "  $($_.名)：启用=$($_.启用) 键='$($_.键)'" }
# **先断言菜单真的读出来了。** 空表上 `@(...)[0].启用` 是 `$null`，`-not $null` 是 `$true` ——
# 菜单没弹出来的话，下面每一条「是灰的」都会**白白通过**。踩过：第一次跑就是这么过的。
断言真 '空状态下「文件」菜单读得出四条' ($空文件.Count -eq 4) "$($空文件.Count) 条"
foreach ($名 in '保存', '另存为…', '导出') {
  $t = @($空文件 | Where-Object { $_.名 -eq $名 })[0]
  断言真 "没载曲时「$名」是灰的" ($t -and -not $t.启用) "启用=$(if ($t) { $t.启用 } else { '找不到' })"
}
$空操作 = 读菜单 '操作'
断言真 '空状态下「操作」菜单读得出两条' ($空操作.Count -eq 2) "$($空操作.Count) 条"
断言真 '没载曲时「撤销」是灰的' (@($空操作 | Where-Object { $_.名 -eq '撤销' })[0].启用 -eq $false) `
  "启用=$(@($空操作 | Where-Object { $_.名 -eq '撤销' })[0].启用)"
断言真 '没载曲时「重做」是灰的' (@($空操作 | Where-Object { $_.名 -eq '重做' })[0].启用 -eq $false) `
  "启用=$(@($空操作 | Where-Object { $_.名 -eq '重做' })[0].启用)"
断言真 '没载曲时播放键也是灰的（确认这真是个空实例）' `
  (-not (按编号 'PlayButton')[0].Current.IsEnabled) ''

"`n=== 2. 从曲库里点开一首 ==="
$行 = @(找类型 $CT::ListItem)
if ($行.Count -eq 0) { throw '曲库是空的' }
$目标 = $null
foreach ($it in $行) {
  foreach ($x in $it.FindAll($TS::Descendants, (& $条件 $CT::Text))) {
    if ($x.Current.Name -eq 'Carulli_Duetto_No2_Op4') { $目标 = $it }
  }
}
if (-not $目标) { throw '曲库里没有 Carulli_Duetto_No2_Op4' }
要前台 '从曲库点开一首'
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
if ((没别的窗) -ne 0) { throw '载入之后还有别的顶层窗开着 —— 它是模态的，会把后面所有点击按键都吃掉' }

function 读位置 { (按编号 'PositionText')[0].Current.Name }
$总小节 = if ((读位置) -match '/\s*(\d+)') { [int]$Matches[1] } else { -1 }
if ($总小节 -ne 124) { throw "载入的不是那一首（$总小节 小节）" }

"`n=== 3. 载入之后：三个变亮，撤销/重做还是灰 ==="
$有文件 = 读菜单 '文件'
$有文件 | ForEach-Object { "  $($_.名)：启用=$($_.启用) 键='$($_.键)'" }
foreach ($名 in '保存', '另存为…', '导出') {
  $t = @($有文件 | Where-Object { $_.名 -eq $名 })[0]
  断言真 "载了曲「$名」就亮了" ($t -and $t.启用) "启用=$(if ($t) { $t.启用 } else { '找不到' })"
}
$有操作 = 读菜单 '操作'
$有操作 | ForEach-Object { "  $($_.名)：启用=$($_.启用) 键='$($_.键)'" }
断言真 '载入状态下「操作」菜单读得出两条' ($有操作.Count -eq 2) "$($有操作.Count) 条"
断言真 '刚载入、什么都没动时「撤销」是灰的' (@($有操作 | Where-Object { $_.名 -eq '撤销' })[0].启用 -eq $false) ''
断言真 '刚载入、什么都没动时「重做」是灰的' (@($有操作 | Where-Object { $_.名 -eq '重做' })[0].启用 -eq $false) ''

"`n=== 4. 条目顺序与右边的快捷键字 ==="
断言真 '「文件」里四条，顺序是 导入 / 保存 / 另存为 / 导出' `
  ((@($有文件 | ForEach-Object { $_.名 }) -join '|') -eq '导入 MIDI…|保存|另存为…|导出') `
  (@($有文件 | ForEach-Object { $_.名 }) -join ' / ')
断言真 '「操作」里两条，顺序是 撤销 / 重做' `
  ((@($有操作 | ForEach-Object { $_.名 }) -join '|') -eq '撤销|重做') `
  (@($有操作 | ForEach-Object { $_.名 }) -join ' / ')
断言真 '「保存」右边写着 Ctrl+S' `
  ((@($有文件 | Where-Object { $_.名 -eq '保存' })[0].键) -eq 'Ctrl+S') `
  "'$(@($有文件 | Where-Object { $_.名 -eq '保存' })[0].键)'"
断言真 '「撤销」右边写着 Ctrl+Z' `
  ((@($有操作 | Where-Object { $_.名 -eq '撤销' })[0].键) -eq 'Ctrl+Z') `
  "'$(@($有操作 | Where-Object { $_.名 -eq '撤销' })[0].键)'"
断言真 '「重做」右边写着 Ctrl+Y' `
  ((@($有操作 | Where-Object { $_.名 -eq '重做' })[0].键) -eq 'Ctrl+Y') `
  "'$(@($有操作 | Where-Object { $_.名 -eq '重做' })[0].键)'"

"`n=== 5. 工具栏那一带上只剩什么 ==="
# 工具栏那一带 = **和两个菜单同一行**的那些控件。
#
# 不写成「窗口顶上 150 像素」：滚动的 `PART_LineUpButton`（ScrollBar 模板里那个箭头，
# name 就是 'Line up'）落在 102..134，正好在那一带里，于是「只剩一个按钮」会假红。
# 拿菜单自己的框当那一行的上下界，它是什么尺寸、在哪一行就都跟着走，不用手调数字。
$行参考 = 菜单按钮 '文件'
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
断言真 '工具栏上只剩一个按钮，就是「演奏器…」' `
  ($带内按钮.Count -eq 1 -and $带内按钮[0].名 -like '演奏器*') `
  "$($带内按钮.Count) 个：$(($带内按钮 | ForEach-Object { $_.名 }) -join ' / ')"
断言真 '工具栏上只剩一个输入框，就是歌曲名框' `
  ($带内编辑.Count -eq 1 -and $带内编辑[0].编号 -eq 'SongNameBox') `
  "$($带内编辑.Count) 个：$(($带内编辑 | ForEach-Object { "$($_.编号)='$($_.名)'" }) -join ' / ')"
# 按**集合**比，不排序 —— `Sort-Object` 在中文上有自己的排法（实测「操作」排在「文件」前面），
# 拿排序后的串去对「文件|操作」是自找的假红。
$带内菜单名 = @($带内菜单 | ForEach-Object { $_.名 })
断言真 '工具栏上正好两个顶级菜单：文件 / 操作' `
  ($带内菜单.Count -eq 2 -and ($带内菜单名 -contains '文件') -and ($带内菜单名 -contains '操作')) `
  "$($带内菜单.Count) 个：$($带内菜单名 -join ' / ')"
# 旧按钮的名字一个都不该在这一带里 —— 这条是「收进菜单」这句话的正主
$旧名字 = @('导入 MIDI', '保存', '另存为', '导出', '撤销', '重做')
$残留 = @($带内 | Where-Object { $n = $_.名; @($旧名字 | Where-Object { $n -like "$_*" }).Count -gt 0 })
断言真 '那一排旧按钮（导入/保存/另存为/导出/撤销/重做）一个都不在工具栏上了' `
  ($残留.Count -eq 0) $(if ($残留.Count) { ($残留 | ForEach-Object { $_.名 }) -join ' / ' } else { '一个都没有' })

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
"点了一下轨头 +1：移调读数 '$改前' → '$改后'（轨数 $轨数，$加一.Count 条轨各有一个）"
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
$曲库目录 = Join-Path (Split-Path $exe) 'songs'
$曲 = Join-Path $曲库目录 'Carulli_Duetto_No2_Op4.mproj'
if (-not (Test-Path $曲)) { throw "找不到 $曲 —— 曲库文件不在 $曲库目录" }
$备份 = "$曲.verify23-bak"
Copy-Item $曲 $备份 -Force
$原哈希 = (Get-FileHash $曲 -Algorithm SHA256).Hash
"曲库文件 $曲（$((Get-Item $曲).Length) 字节），已备份，原哈希 $($原哈希.Substring(0,12))…"

function 读哈希 { (Get-FileHash $曲 -Algorithm SHA256).Hash }
function 等文件写入([string]$旧哈希, [int]$最多毫秒 = 6000) {
  $等 = (Get-Date).AddMilliseconds($最多毫秒)
  do { Start-Sleep -Milliseconds 300 } while ((读哈希) -eq $旧哈希 -and (Get-Date) -lt $等)
  (读哈希)
}
try {
  # 对照组：**没改过**的时候按一次 Ctrl+S，把此刻的字节记作「基线」。
  #
  # 这里是这次跑出来的最有用的一条观察：**基线 ≠ 磁盘上原来那份**。
  # 那份文件是上一次构建写下的（大小 $((Get-Item $备份).Length) 字节），而当前这版写出来的
  # 是另一串字节 —— 也就是「存一次就会把整份文件按当前格式重写」，**不是**「什么都没动也照抄一遍」。
  # 所以下面所有判据都拿**这次的基线**当参照，不拿磁盘上那份：拿它当参照的话，
  # 「撤销之后存回去该等于原来那份」必然假红，而那证明的不是 23 号有问题，是格式换代了。
  $前 = 读哈希
  按Ctrl(0x53)
  Start-Sleep -Milliseconds 1500
  $对照 = 读哈希
  "对照：没改过时按 Ctrl+S，哈希 $($前.Substring(0,12))… → $($对照.Substring(0,12))…（$(if ($对照 -eq $前) { '一模一样' } else { '换了一串' })）"
  "  磁盘上原来那份 $((Get-Item $备份).Length) 字节，现在这份 $((Get-Item $曲).Length) 字节"
  # 差在哪儿？—— 只报不判：这是格式换代，不是 23 号的判据。
  $甲 = [IO.File]::ReadAllBytes($备份); $乙 = [IO.File]::ReadAllBytes($曲)
  $首个 = -1
  for ($i = 0; $i -lt [Math]::Min($甲.Length, $乙.Length); $i++) { if ($甲[$i] -ne $乙[$i]) { $首个 = $i; break } }
  "  第一个不同的字节在 $首个（$(if ($首个 -lt 0) { '共同前缀内完全一致' } else { '旧：' + [Text.Encoding]::ASCII.GetString($甲, $首个, [Math]::Min(40, $甲.Length - $首个)) + ' / 新：' + [Text.Encoding]::ASCII.GetString($乙, $首个, [Math]::Min(40, $乙.Length - $首个)) })）"
  断言真 '保存没弹出任何对话框' ((没别的窗) -eq 0) "$((没别的窗)) 个别的窗"

  # 正主：改一处、Ctrl+S、文件该变
  $r = $加一[0].Current.BoundingRectangle
  点 ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2)) "点菜单「$菜单」里的「$条目」"
  Start-Sleep -Milliseconds 900
  断言真 '要先真的改了谱面才谈得上保存' ((读移调) -ne $改前) "移调 '$(读移调)'"
  按Ctrl(0x53)
  $存后 = 等文件写入 $对照
  "改完按 Ctrl+S：哈希 $($对照.Substring(0,12))… → $($存后.Substring(0,12))…"
  断言真 'Ctrl+S 把改动写进文件了（字节确实变了）' ($存后 -ne $对照) ''

  # 撤销 + 再存一次：**该精确回到基线**。
  # 这一条才是「保存的确实是手上这份谱面」的正主 —— 上一轮改动被撤销之后，
  # 文档内容回到了对照那一刻的样子，那么再存一次就该一字不差地写出同一串字节。
  按Ctrl(0x5A)
  Start-Sleep -Milliseconds 600
  按Ctrl(0x53)
  $回后 = 等文件写入 $存后
  "撤销之后再按 Ctrl+S：哈希 $($存后.Substring(0,12))… → $($回后.Substring(0,12))…"
  断言真 '撤销之后存回去，字节精确回到对照那一刻（存的确实是手上这份谱面，来回自洽）' ($回后 -eq $对照) `
    "$($回后.Substring(0,12))… 对基线 $($对照.Substring(0,12))…"
} finally {
  # 曲库文件是**用户的数据**（虽然躺在 bin 里）。不管上面成没成，无条件还原。
  Copy-Item $备份 $曲 -Force
  Remove-Item $备份 -Force
  $还原 = (Get-FileHash $曲 -Algorithm SHA256).Hash
  "已还原 $曲：哈希 $($还原.Substring(0,12))…$(if ($还原 -eq $原哈希) { '（和原样一致）' } else { '（！！！和原样不一致）' })"
  if ($还原 -ne $原哈希) { $script:fail++; "  FAIL 曲库文件没能还原成原样" }
}

"`n=== 8. 「演奏器…」那颗按钮还在，点了还是开一个窗 ==="
$演奏 = @(找类型 $CT::Button | Where-Object { $_.Current.Name -like '演奏器*' })[0]
if (-not $演奏) { throw '找不到「演奏器…」按钮' }
$开前 = 没别的窗
$r = $演奏.Current.BoundingRectangle
点 ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2)) '点工具栏那颗「演奏器…」'
Start-Sleep -Seconds 2
$开后 = [V23]::Others($脚本PID, $h)
"点之前 $开前 个别的窗，点之后 $($开后.Count) 个"
断言真 '点「演奏器…」开出了一个新窗口' ($开后.Count -gt $开前) "$开前 → $($开后.Count)"
if ($开后.Count) {
  $演根 = $AE::FromHandle($开后[0])
  "  那个窗：'$($演根.Current.Name)'"
  断言真 '开出来的窗是演奏器那个' ($演根.Current.Name -like '*演奏器*') "'$($演根.Current.Name)'"
  [void][V23]::PostMessage($开后[0], 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)   # WM_CLOSE
  Start-Sleep -Seconds 2
  断言真 '关掉之后它真的没了' ((没别的窗) -eq 0) "$((没别的窗)) 个"
}

""
if ($fail) { "$fail 条不过"; exit 1 } else { '全过' }
