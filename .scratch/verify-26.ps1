# 26 号工单的实机验证：屏幕上所有写死快捷键的地方和实际键位对上了没有。
#
# 用法: pwsh -NoProfile -File verify-26.ps1     （脚本自己起 app、自己收尾）
#
# 驱动是 27 号那一份（浮层闸门、清场、摆窗那套的来龙去脉见 verify-25.ps1 抬头），
# 音符的屏幕位置借 21 号那份像素扫描（见下面「找音符」）。这一票特有的量法写在本文件
# 下半段开头的方框里，两条最要紧的先记在这儿：
#   · **省略号 UIA 里读不到**（TextTrimming 只影响渲染，元素的 Name 永远是全文），
#     所以那条只能截图存盘由人眼看；脚本量的是它的必要条件。
#   · **读数栏得先有内容**才有得挤 —— 先载一首、选中一个音，再拉窄。
#
# 前提：**非提权**。本脚本不点「开始演奏」（26 号不碰那条路），
# 但同样不许提权跑：提权的话 app 继承了高权限令牌，以后谁在这个脚本上加一句
# 「按一下开始试试」就会真的往当前前台窗口发合成按键。宁可从一开始就拦住。

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @"
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public class V25 {
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
  /// 挪光标 / 按一下，拆成两步是为了能在中间「先停到别处去」—— 见 PowerShell 那边的 `点`。
  ///
  /// 为什么要先停到别处：**Avalonia 的悬浮提示是 app 自己的另一个顶层窗**（量到过：
  /// 类名 Avalonia-…、标题空、框 547,843 296x60，就贴在被悬停的那一格下面）。
  /// 它**不吃 hit test** —— `WindowFromPoint` 照报主窗、PID 闸门也照过，可只要它开着，
  /// **第一下点击就什么都不做，第二下才生效**（4 处独立复现，全在「光标刚在同一格上停过」之后）。
  /// 所以每次点之前先把光标挪到窗口里一块没有提示的空地上停够时间，把那张提示关掉，
  /// 再挪回目标立刻按下去（60ms，短到开不出新的提示）。
  public static void Move(int x, int y) { SetCursorPos(x, y); }
  public static void Press() {
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


# 双击要自己来：V25::Click 中间睡 330 毫秒，两次拼起来超过系统双击间隔，
# 那是两下单击不是双击。这个类只干「两下挨得够近的按」这一件事。
Add-Type @'
using System; using System.Runtime.InteropServices;
public class D25 {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  // 「app 自己还开着哪些顶层窗、各在哪」—— 排查悬浮提示那类「进程号对得上、但点在它身上」的窗
  public static int[] RectOf(IntPtr h) { RECT r; GetWindowRect(h, out r); return new int[] { r.L, r.T, r.R - r.L, r.B - r.T }; }
  // 「两下挨得够近的按」这一段。停车（把悬浮提示关掉）由 PowerShell 那边统一做，
  // 这里只负责按下、松开、再按下、再松开 —— 两下之间的间隔必须小于系统双击间隔，
  // 而 V25::Press 中间睡 330ms，两次拼起来就超了，那不是双击是两下单击。
  public static void DoublePress() {
    for (int i = 0; i < 2; i++) {
      mouse_event(0x0002,0,0,0,IntPtr.Zero); mouse_event(0x0004,0,0,0,IntPtr.Zero);
      System.Threading.Thread.Sleep(60);
    }
    System.Threading.Thread.Sleep(400);
  }
}
'@

# ---------- 起一个干净实例 ----------
$exe = Join-Path $PSScriptRoot '..\MidiPerformer.App\bin\Debug\net8.0\MidiPerformer.exe'
if (-not (Test-Path $exe)) { throw "没找到 $exe —— 先编译" }
Get-Process -Name MidiPerformer -EA SilentlyContinue | ForEach-Object {
  [void][V25]::ShowWindow($_.MainWindowHandle, 9)
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

if (-not [V25]::Take($h)) { throw '拽不到前台' }
$摆 = { param($摆X, $摆Y, $摆宽, $摆高)
  [void][V25]::SetWindowPos($h, [IntPtr]::Zero, $摆X, $摆Y, $摆宽, $摆高, 0x0004 -bor 0x0010)
  Start-Sleep -Milliseconds 700
  ($AE::FromHandle($h)).Current.BoundingRectangle
}
$r1 = & $摆 405 450 2360 1520
$win = & $摆 (810 - [int]$r1.X) (900 - [int]$r1.Y) (4720 - [int]$r1.Width) (3040 - [int]$r1.Height)
"窗口 $([int]$win.Width)x$([int]$win.Height) @ $([int]$win.X),$([int]$win.Y)"
if ([int]$win.Width -ne 2360 -or [int]$win.Height -ne 1520) { throw "窗口没摆成 2360x1520（摆完 $([int]$win.Width)x$([int]$win.Height)）" }
foreach ($等 in 1..20) {
  $root = $AE::FromHandle($h)
  if (@($root.FindAll($TS::Descendants, (& $条件 $CT::Edit)) | Where-Object { $_.Current.AutomationId -eq 'SongNameBox' }).Count) { break }
  Start-Sleep -Milliseconds 500
}
Start-Sleep -Seconds 2
$root = $AE::FromHandle($h)

# 「停车点」= 窗口里那块没有悬浮提示的空地（卷帘区中部）：每次点之前先停这儿，
# 把上一下遗留下来的悬浮提示关掉。挑卷帘区是因为整条工具栏、轨头、曲库行上的元素
# 大多挂着 ToolTip，而卷帘区只有悬停读数、没有 ToolTip。
$停车点 = @([int]($win.X + $win.Width * 0.6), [int]($win.Y + $win.Height * 0.45))

# ---------- 闸门：前台 + 点上（这两个坑的来龙去脉见 verify-23.ps1 抬头）----------
# 点上干不干净：**只看 PID 是不够的** —— 这一条是这一票里最贵的一个坑，量了五支探针才钉死。
# 菜单的弹出层、存盘/打开的那个原生框（类名 #32770）都是 **app 自己的顶层窗**，
# PID 和主窗一模一样，光比 PID 的闸门全放行。实测到的现场：焦点被放开之后敲了一个
# 没人接的回车，「文件」菜单的弹出层就开在 429,527 296x244 那一块，正好盖住曲库行；
# 第 10 节要点的 708,737 上，UIA 读出来是 `MenuItem「导出」` —— 那一击点的是导出，
# app 弹出「导出 MIDI」的存盘框把整行盖住，后面单击双击全打在框里，那四条红全是这么来的。
# 所以要问的不是「这是不是 app 的窗」，而是「这是不是**主窗**」。
function 净了([int]$横, [int]$纵) {
  if ($横 -lt 0) { return $true }
  if ([V25]::PidAt($横, $纵) -ne $脚本PID) { return $false }
  return ([V25]::At($横, $纵) -eq $h)
}

# app 自己开着的、**该收掉**的浮层：菜单弹出层（里面有 MenuItem）和原生对话框（类名 #32770）。
# 悬浮提示不算 —— 它不是浮层，是跟着光标走的一张纸，把光标挪开就散了；
# 而且**绝对不能拿 Esc 去收它**：改名框开着的时候按 Esc 会把改名取消掉（§7/§8/§9 全废）。
function 浮层([object[]]$别窗) {
  $要收 = @()
  foreach ($w in $别窗) {
    # GetClassName 是 DllImport 那个三参数的（要 StringBuilder 的老签名），直接丢句柄过去
    # 只会得到「找不到参数计数为 1 的重载」—— 而且那是**非终止**错误：脚本照跑，
    # `$类` 是 $null，于是浮层永远收不出东西，还一路绿灯。
    $缓冲 = New-Object System.Text.StringBuilder 256
    [void][V25]::GetClassName($w, $缓冲, 256)
    $类 = $缓冲.ToString()
    $是菜单 = $false
    try {
      $el = $AE::FromHandle($w)
      $是菜单 = @($el.FindAll($TS::Descendants, (& $条件 $CT::MenuItem))).Count -gt 0
    } catch { }
    if ($是菜单 -or $类 -like '#32770*') { $要收 += $w }
  }
  , $要收
}

# 点/按键之前把场子清干净：抢前台 → 收浮层（Esc）→ 点上只能压着主窗。
# 清不干净就抛 —— 宁可当场红，也不要静悄悄地打在别人的控件上（那正是上面那个坑的形状）。
function 清场([int]$横, [int]$纵, [string]$谁) {
  for ($i = 1; $i -le 6; $i++) {
    要前台 $谁
    if (净了 $横 $纵) {
      $要收 = 浮层 @([V25]::Others($脚本PID, $h))
      if ($要收.Count -eq 0) { return }
      $说 = 'app 还开着 ' + $要收.Count + ' 个浮层（' + (($要收 | ForEach-Object { [V25]::Describe($_) }) -join ' / ') + '）'
      Write-Host "    ↺「$谁」之前先收一下：$说（第 $i 次，Esc）"
      [V25]::Key(0x1B)
    } else {
      $在 = [V25]::At($横, $纵)
      $at = $AE::FromPoint([System.Windows.Point]::new([double]$横, [double]$纵))
      $读到 = ''
      if ($at) {
        $读到 = '，UIA 读到 ' + ($at.Current.ControlType.ProgrammaticName -replace 'ControlType\.', '') + '「' + $at.Current.Name + '」'
      }
      Write-Host "    ↺「$谁」之前先收一下：$横,$纵 上压着 $([V25]::Describe($在))$读到（第 $i 次）"
      if ($at -and $at.Current.ControlType -eq $CT::MenuItem) { [V25]::Key(0x1B) }
      [V25]::Move($停车点[0], $停车点[1]); Start-Sleep -Milliseconds 700
    }
    Start-Sleep -Milliseconds 400
  }
  throw "「$谁」之前清不干净：$横,$纵 上还压着 $([V25]::Describe([V25]::At($横, $纵)))，或者 app 还开着浮层"
}


function 要前台([string]$谁) {
  for ($i = 1; $i -le 6; $i++) {
    [void][V25]::Take($h)
    if ([V25]::GetForegroundWindow() -eq $h) {
      Start-Sleep -Milliseconds 250
      $中横 = [int]($win.X + $win.Width / 2); $中纵 = [int]($win.Y + $win.Height / 2)
      if ([V25]::GetForegroundWindow() -eq $h -and [V25]::PidAt($中横, $中纵) -eq $脚本PID) { return }
      Write-Host "    [「$谁」前台是它了，但窗口正中压着 $([V25]::Describe([V25]::At($中横, $中纵))) —— 再抬一次]"
    }
    Start-Sleep -Milliseconds 500
  }
  throw "「$谁」之前没能让 app 既在前台、又没被压住（前台是 $([V25]::Describe([V25]::GetForegroundWindow()))）"
}
function 点([int]$横, [int]$纵, [string]$谁) {
  清场 $横 $纵 $谁
  # 先停到窗口里一块没有悬浮提示的空地上，把可能正开着的那张提示关掉，
  # 再挪到目标立刻按下 —— 不这么做的话「第一下点击什么都不做」会一路假红（见 V25::Move 的注释）。
  [V25]::Move($停车点[0], $停车点[1]); Start-Sleep -Milliseconds 500
  [V25]::Move($横, $纵); Start-Sleep -Milliseconds 60
  [V25]::Press()
}
function 双击([int]$横, [int]$纵, [string]$谁) {
  清场 $横 $纵 $谁
  [V25]::Move($停车点[0], $停车点[1]); Start-Sleep -Milliseconds 500
  [V25]::Move($横, $纵); Start-Sleep -Milliseconds 60
  [D25]::DoublePress()
}
function 按([byte]$键, [string]$谁) { 清场 -1 -1 $谁; [V25]::Key($键) }

# ---------- 取数 ----------
function 找类型([object]$类型) { @($root.FindAll($TS::Descendants, (& $条件 $类型))) }
function 文本([string]$含) { @(找类型 $CT::Text | Where-Object { $_.Current.Name -like "*$含*" }) }
function 按钮([string]$名) { @(找类型 $CT::Button | Where-Object { $_.Current.Name -eq $名 }) }
function 按编号([string]$id) {
  @(@(找类型 $CT::Text) + @(找类型 $CT::Button) + @(找类型 $CT::Edit) + @(找类型 $CT::ComboBox)) |
    Where-Object { $_.Current.AutomationId -eq $id }
}
# 轨数用「折叠」按钮数：每条轨的头上都有一颗（和 verify-23 同一把尺子）
function 数轨 { @(找类型 $CT::Button | Where-Object { $_.Current.Name -eq '折叠' }).Count }
function 曲库行 { @(找类型 $CT::ListItem) }
function 行里的文字($行) { @($行.FindAll($TS::Descendants, (& $条件 $CT::Text))) }
function 行里的框($行) { @($行.FindAll($TS::Descendants, (& $条件 $CT::Edit))) }
function 找行([string]$曲名) {
  @(曲库行 | Where-Object {
    $t = 行里的文字 $_
    $t.Count -gt 0 -and $t[0].Current.Name -eq $曲名
  }) | Select-Object -First 1
}
function 行名([object]$行) { (@(行里的文字 $行) | ForEach-Object { $_.Current.Name }) -join '/' }
function 取值($e) { $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
function 设值($e, [string]$v) { $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($v) }
function 矩形($e) { $e.Current.BoundingRectangle }
function 中心($e) { $r = 矩形 $e; @([int]($r.X + $r.Width/2), [int]($r.Y + $r.Height/2)) }
function 曲名框 { (按编号 'SongNameBox')[0] }
# 「这一行选中了没有」不能读 `$e.Current.IsSelected` —— 那个属性**不在**
# AutomationElementInformation 上（它在 SelectionItemPattern 里），读出来是 $null，
# 而不带值的 $null 在断言里恒假：那会变成一条**永远红**的假断言。
# 也只能拿它当尺子，因为「点了行内边距之后这一行有没有选中」正是这一票要验的东西。
function 选中了($行) {
  try { $行.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected }
  catch { $false }
}
# 行里「不是名字文字」的那一格：右边那格小字（'没动过' / '改过' / '读不出来'）。
# 它是 TextBlock（不是 TextBox 也不是 Button）—— 单击只是选中、双击会打开、
# 而且**没有 ToolTip**（ToolTip 只挂在名字和删除上），所以是行内最干净的一个落点。
function 小字($行) { (行里的文字 $行)[1] }
function 曲名格($行) { (行里的文字 $行)[0] }

# ======================================================================
# 26 号工单要量的东西
#
# 七条验收里，**六条能在这台机器上量**（提示行的字、窄窗出省略号、悬停看全文、
# 播放/暂停与 ↻ 重头播放 的 ToolTip、撤销重做的键位只在一处、改名按钮连 ToolTip 一起没了），
# 第七条「和实际键位**逐条**对得上」的**语义**那一半不在这儿量：它在
# `ShortcutHintTests.提示里的每个手势在按键那一段里都真的绑着`（拿提示里每个手势
# 去 `OnWindowKeyDown` 的方法体里找绑定，两张表共 8 对）—— 那是机器测过的。
# 这里量的是**屏幕上真的出现了那句话**，而且和代码里那一份是同一个字符串。
#
# **36 号改写了这一票的「提示行」那两条**（分层 + 撤销/重做撤出），跟着动了四处：
#   · 提示行从**一整行**变成**两层**：没选中音 = 走带那一层（§1，那时还没载曲子），
#     选中了音 = 编辑那一层（§2 选中之后那一句）。所以脚本里存三份：
#     `$预期走带` / `$预期编辑` / `$预期全文`（前两个拼出来的）。
#   · 屏上那一层之外，**ToolTip 是两行合起来的全文** —— 它就是 UIA 里的 `HelpText`，
#     不用悬停就能读（§1 直接断言），§4 那次悬停量的是「那张纸真的弹出来、纸上真是这句」。
#   · `量读数行` 多一个 `$期望` 参数：宽窗/窄窗两次量的都该是编辑那一层（两次都已经选中了音）。
#   · §6 那条正对照**反过来了**：26 号当年它证明「提示行的 ToolTip 里就有这对键位」，
#     36 号之后提示行自己也得干净 —— 现在钉的是**没有**。
#
# 两个量法上的坑，先说清楚：
#   · **省略号 UIA 里看不见。** `TextTrimming` 只影响渲染：元素的 `Name` 永远是全文，
#     裁掉的那半行不会以任何属性露面。所以「出没出省略号」只能**看**——
#     本脚本在最小宽度下把那一行截下来存成 PNG（`shot26-min.png`），由人眼看。
#     脚本能量的是它的**必要条件**：那一行真的被挤窄了（窄窗 vs 全宽两次量宽度）、
#     单行（不是换行撑成两行）、右边缘不出窗、左边缘不压读数栏。
#   · **读数条得先有内容。** 读数栏那半行只在「悬停/选中了一个音」时才占位
#     （`ReadoutDetail` 初始 `IsVisible=False`，藏起来的元素**不进 UIA 树**）——
#     空状态下量「挤不挤」等于量了一块本来就 0 宽的东西。
#     所以先双击曲库载一首、再**选中**一个音（选中兜底，鼠标挪开读数条也还在），
#     然后才拉窄窗口。音符的屏幕位置由脚本自己截图扫出来，不写死坐标
#     （扫法借 21 号那份：亮度 > 60 + 横向连续段 ≥ 24px，小节线是竖的只有 2px 宽）。
# ======================================================================

function 找文本([string]$名字) {
  @(找类型 $CT::Text | Where-Object { $_.Current.Name -eq $名字 }) | Select-Object -First 1
}

# 摆窗之后 `$win` / `$停车点` 都得跟着更新 —— 清场、要前台、点这几把闸门读的都是它俩，
# 拿旧窗口的坐标去「停车」会停到窗口外，于是悬浮提示关不掉、第一下点击被吃掉。
function 摆窗([int]$横, [int]$纵, [int]$宽, [int]$高) {
  $r = & $摆 $横 $纵 $宽 $高
  $script:win = $r
  $script:停车点 = @([int]($r.X + $r.Width * 0.6), [int]($r.Y + $r.Height * 0.45))
  $r
}

function 截图([string]$路径, [int]$横, [int]$纵, [int]$宽, [int]$高, [int]$倍 = 1) {
  # 裁到虚拟屏幕以内：CopyFromScreen 越界会抛，而悬浮提示那张纸常常有一半在屏幕外。
  # 字号 12.5 在这台机器上（200%）本来就渲染成 25px 高，1 倍截出来就看得清，不用放大。
  $屏 = [System.Windows.Forms.SystemInformation]::VirtualScreen
  $横 = [Math]::Max([int]$屏.X, [int]$横); $纵 = [Math]::Max([int]$屏.Y, [int]$纵)
  $宽 = [Math]::Min([int]$屏.X + [int]$屏.Width, $横 + [int]$宽) - $横
  $高 = [Math]::Min([int]$屏.Y + [int]$屏.Height, $纵 + [int]$高) - $纵
  if ($宽 -lt 4 -or $高 -lt 4) { Write-Host '    [截图跳过：框在屏幕外]'; return }
  $bmp = New-Object System.Drawing.Bitmap([int]$宽, [int]$高)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen([int]$横, [int]$纵, 0, 0, $bmp.Size)
  $g.Dispose()
  if ($倍 -gt 1) {
    $大 = New-Object System.Drawing.Bitmap([int]($宽 * $倍), [int]($高 * $倍))
    $g2 = [System.Drawing.Graphics]::FromImage($大)
    $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $g2.DrawImage($bmp, 0, 0, [int]($宽 * $倍), [int]($高 * $倍))
    $g2.Dispose(); $bmp.Dispose(); $bmp = $大
  }
  $bmp.Save($路径, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
}

# ---------- 找音符的像素位置（借 verify-21.ps1 那份扫法）----------
# 上界用轨头「01」往下 110px（跳过轨头那一排**和标尺**：标尺上小节号的连续段也够长，
# 会被当成音符，悬上去什么都读不到），下界用轨头「02」往上 14px。
function 找音符 {
  $e1 = 找文本 '01'
  if (-not $e1) { throw '找不到轨头「01」—— 卷帘没起来？' }
  $e2 = 找文本 '02'

  $bmp = New-Object System.Drawing.Bitmap([int]$win.Width, [int]$win.Height)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen([int]$win.X, [int]$win.Y, 0, 0, $bmp.Size)
  $g.Dispose()

  $top = [int]($e1.Current.BoundingRectangle.Y - $win.Y) + 110
  $bot = if ($e2) { [int]($e2.Current.BoundingRectangle.Y - $win.Y) - 14 }
         else { [int]($win.Height) - 240 }      # 单轨的曲子没有「02」，退到读数栏上方
  $left = [int]($e1.Current.BoundingRectangle.X - $win.X) + 4
  $right = [int]($win.Width) - 20

  $行 = @()
  for ($y = $top; $y -lt $bot; $y += 2) {
    $最好 = 0; $最好X = -1; $连 = 0; $起 = -1
    for ($x = $left; $x -lt $right; $x += 2) {
      $c = $bmp.GetPixel($x, $y)
      if ((0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B) -gt 60) {
        if ($连 -eq 0) { $起 = $x }
        $连 += 2
      } else {
        if ($连 -gt $最好) { $最好 = $连; $最好X = $起 }
        $连 = 0
      }
    }
    if ($连 -gt $最好) { $最好 = $连; $最好X = $起 }
    if ($最好 -ge 24) { $行 += ,@{ Y = $y; X = $最好X; 宽 = $最好 } }
  }
  $bmp.Dispose()

  $并 = @()
  foreach ($b in $行) {
    if ($并.Count -gt 0 -and ($b.Y - $并[-1].末) -le 4) {
      $m = $并[-1]; $m.末 = $b.Y; $m.行数++
      if ($b.宽 -gt $m.宽) { $m.宽 = $b.宽; $m.X = $b.X; $m.Y = $b.Y }
    } else {
      $并 += @{ Y = $b.Y; 末 = $b.Y; X = $b.X; 宽 = $b.宽; 行数 = 1 }
    }
  }
  @($并 | Where-Object { $_.行数 -ge 3 } | ForEach-Object {
      @{ X = [int]($win.X + $_.X + $_.宽 / 2); Y = [int]($win.Y + $_.Y) }
    })
}

# 36 号工单起提示行**分两层**，所以脚本里也存两份，外加两行合起来的那份全文。
# 撤销 / 重做不在任何一份里（用户：「不需要单独写，将它们作为快捷键，直接放到『操作』里面」）——
# 它们现在由 §6 守着：屏幕上只剩「操作」菜单项右侧那一处。
$预期走带 = '空格 播放/暂停 · Shift + 空格 回跳一小节并播放 · Ctrl + ↑ ↓ 换轨'
$预期编辑 = '← → 移时间（一格 = 十六分）· ↑ ↓ 移音高 · Shift + ← → 改时值 · Ctrl + ← → 选同轨前/后一个音 · Delete 删除'
$预期全文 = "$预期走带 · $预期编辑"
$预期播放 = '播放 / 暂停：从播放头当前位置开始，再按停在原地、再按从那儿接着放（空格键同效）'
# 33 号工单把 `■ 停止` 换成了 `↻ 重头播放` —— 26 号当年钉的是前者那句
# 「停下来（急停是 F6）」，那句话连着那颗按钮一起作废了。这儿按**新的事实**改：
# 元素名、Content、ToolTip 三样都换掉，判据一条不少（还是「逐字相等」）。
$预期重播 = '重头播放：播放头回开头、视野回第一小节，立刻开始放'
$读数栏 = @('ReadoutTrackText', 'ReadoutPitchText', 'ReadoutBarText', 'ReadoutBeatText', 'ReadoutLengthText')
function 读数元素 { @($读数栏 | ForEach-Object { 按编号 $_ }) }
function 提示行 { (按编号 'HintText') }

# 窗口的缩放比例。这一票说的「最小宽度 720」是**逻辑像素**，而 SetWindowPos 和 UIA 的框
# 都是**物理像素** —— 这台机器的缩放是 200%（把宽度要成 400，实得 1440 = 720x2，
# 正是 axaml 里那条 MinWidth 夹出来的）。所以那条断言不能写死 700~740，得按窗口现算。
Add-Type @'
using System; using System.Runtime.InteropServices;
public class P26 {
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  public static double Scale(IntPtr h) { return GetDpiForWindow(h) / 96.0; }
}
'@
$缩放 = [P26]::Scale($h)

# **起手先把窗口从 harness 默认那处挪进工作区。** 默认那处是 2360x1520 @ 405,450，底边落在 1970，
# 而这台机器（3072x1920 的屏）任务栏从 y=1824 起、工作区只有 1824 高 —— 读数栏那一行正好被**任务栏压住**：
# 悬停它时那个点上压的是任务栏，ToolTip 永远弹不出来，UIA `FromPoint` 在那个点上读到的是任务栏那个无名 `Pane`
#（第 4、5 条判据就是这么假红的；先怀疑 ToolTip 不弹，量了三支探针才钉到任务栏头上）。
# 所以这一票的窗口底边必须留在 1824 以上。实测：要 2360x1300，实得 2334x1229（宽高各缩 26/71，这台机器上的固定偏差）。
$r全 = 摆窗 405 300 2360 1300
Write-Host "  重摆窗口：$([int]$r全.X),$([int]$r全.Y) $([int]$r全.Width)x$([int]$r全.Height)（底边 $([int]($r全.Y + $r全.Height))，任务栏从 1824 起）"

# 量「读数栏那一行」：提示行的框、读数栏那一排的 Y 和最右边缘，顺手截一张图。
# 宽窗/窄窗各量一次，两次数值一比才叫「窄了」—— **读数栏在不在场会改变提示行的可用宽度**，
# 所以两次都必须在「已经选中一个音」的状态下量；否则第一次量的是读数栏藏起来时的宽度，
# 那个差里混着两件事，说不清是窗窄了还是读数栏冒出来了。
# `$期望` = 这一量之下提示行**该显的那一层**（36 号之后是参数，不是写死的常量）。
function 量读数行([string]$标, [string]$图路径, [string]$期望) {
  $hs = @(按编号 'HintText')
  if ($hs.Count -ne 1) { throw "「$标」下提示行不唯一（$($hs.Count) 个）—— 那不是「挤」，那是没了或者撞名了" }
  foreach ($id in $读数栏) {
    if (@(按编号 $id).Count -ne 1) { throw "「$标」下 $id 不在场（$(@(按编号 $id).Count) 个）" }
  }
  $rh = $hs[0].Current.BoundingRectangle
  $ys = @($读数栏 | ForEach-Object { (按编号 $_)[0].Current.BoundingRectangle.Y })
  $底s = @($读数栏 | ForEach-Object {
      $b = (按编号 $_)[0].Current.BoundingRectangle; [int]($b.Y + $b.Height)
    })
  $最右 = (@($读数栏 | ForEach-Object {
      $b = (按编号 $_)[0].Current.BoundingRectangle; [int]$b.X + [int]$b.Width
    }) | Measure-Object -Maximum).Maximum
  # 「读数栏那一行有没有挪」只能跟**上一行**（小节导航那排，拿 JumpBox 当锚）比，不能跟窗口底边比：
  # 拉窄之后底下那条按钮条自己会长高（右边那句「放完自动停止…」换行），整摞往上顶 ——
  # 那是正常布局，可它会让「距窗口底边的距离」跟着变，拿它当尺子就成了假红。
  $锚 = 按编号 'JumpBox'
  if ($锚.Count -ne 1) { throw "「$标」下 JumpBox 不在场（$($锚.Count) 个）—— 读数栏那一行的锚没了" }
  $上距 = [int]$ys[0] - [int]($锚[0].Current.BoundingRectangle.Y + $锚[0].Current.BoundingRectangle.Height)
  $行高 = (($底s | Measure-Object -Maximum).Maximum) - (($ys | Measure-Object -Minimum).Minimum)
  $上 = [Math]::Min([int]$rh.Y, [int]$ys[0]) - 8
  $高 = [int]($rh.Y + $rh.Height) - $上 + 8
  截图 $图路径 ([int]$win.X) $上 ([int]$win.Width) $高 1
  Write-Host "  [$标] 提示行 $([int]$rh.X),$([int]$rh.Y) $([int]$rh.Width)x$([int]$rh.Height)；读数栏 Y=$([int]$ys[0]) 行高 $行高 距上一行 $上距 最右 $最右；图 $图路径"
  # 36 号起「全」比的是**此刻该显的那一层**（由调用方传进来）：量这两次的时候都已经选中了一个音，
  # 所以两次都该是编辑那一行。写死成某一层的话，红了分不清是「层换错了」还是「这句抄错了」。
  @{ 提示 = $rh; Y = $ys; 最右 = $最右; 上距 = $上距; 行高 = $行高; 可见 = (-not $hs[0].Current.IsOffscreen); 全 = ($hs[0].Current.Name -ceq $期望) }
}

$跑完了 = $false
try {

# ---------- 1. 提示行：是不是那一句话 ----------
Write-Host "`n=== 1. 提示行（HintText）· 还没载曲子、一个音都没选中 → 走带那一层 ==="
$hints = @(按编号 'HintText')
断言真 '屏幕上有且只有一个 HintText' ($hints.Count -eq 1) "找到 $($hints.Count) 个"
if ($hints.Count -ne 1) { throw '提示行不唯一，下面没法量 —— 先看是不是 x:Name 撞了' }
$提示 = $hints[0]
断言真 '提示行的字逐字就是走带那一层' ($提示.Current.Name -ceq $预期走带) `
  "读到「$($提示.Current.Name)」（$($提示.Current.Name.Length) 字，期望 $($预期走带.Length) 字）"

# 36 号新加的一条：ToolTip 是**两行合起来的全文**（屏幕上那一行只是「此刻该看的那一类」，
# 看全的出口只剩这一个）。它在 UIA 里就是 HelpText —— 不用悬停就能读，§4 那次悬停量的是
# 「那张纸真的弹出来了、纸上真是这句」。
断言真 '提示行的 ToolTip 逐字就是两行合起来的全文' ($提示.Current.HelpText -ceq $预期全文) `
  "读到「$($提示.Current.HelpText)」（$($提示.Current.HelpText.Length) 字，期望 $($预期全文.Length) 字）"
foreach ($字 in @('撤销', '重做', 'Ctrl+Z', 'Ctrl+Y')) {
  断言真 "提示行与它的 ToolTip 里都没有「$字」（36 号撤出去了，只留「操作」菜单那一处）" `
    (($提示.Current.Name -notlike "*$字*") -and ($提示.Current.HelpText -notlike "*$字*")) `
    '用户原话：「不需要单独写，将它们作为快捷键，直接放到『操作』里面作为提示就可以了」'
}

# 静态对照：屏幕上那两句和 Format.cs 里那两份是不是同一个字符串。
# 断言红了要能立刻分清「屏幕错了」还是「脚本里抄错了」—— 两份独立地比一下。
$码 = Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot '..\MidiPerformer.Adapters\Presenters\Format.cs')
function 码里的([string]$名) {
  $m = [regex]::Match($码, "$名\s*=\s*([\s\S]*?);")
  if (-not $m.Success) { throw "Format.cs 里没抓到 $名" }
  -join ([regex]::Matches($m.Groups[1].Value, '"((?:[^"\\]|\\.)*)"') | ForEach-Object { $_.Groups[1].Value })
}
$码走带 = 码里的 'ReadoutHintPerforming'
$码编辑 = 码里的 'ReadoutHintEditing'
断言真 '代码里走带那一层和脚本里这一份逐字相同（红了先怀疑脚本抄错）' ($码走带 -ceq $预期走带) `
  "代码 $($码走带.Length) 字 / 脚本 $($预期走带.Length) 字"
断言真 '代码里编辑那一层和脚本里这一份逐字相同' ($码编辑 -ceq $预期编辑) `
  "代码 $($码编辑.Length) 字 / 脚本 $($预期编辑.Length) 字"
# 全文那一份必须是**拼出来的**，不是第三处手抄的：手抄的话，改了上面两层、它留在原地
$m全文 = [regex]::Match($码, 'ReadoutHintTooltip\s*=\s*([\s\S]*?);')
断言真 'ToolTip 那一份是两层拼起来的（不是手抄的第三份全文）' `
  ($m全文.Success -and (($m全文.Groups[1].Value -replace '\s', '') -eq 'ReadoutHintPerforming+"·"+ReadoutHintEditing')) `
  "Format.cs 里 ToolTip 那一行 = $(if ($m全文.Success) { $m全文.Groups[1].Value } else { '抓不到' })"

# 分层之后「哪一条归哪一层」也成了判据的一部分：此刻一个音都没选中，
# 屏幕上就该是走带那一层 —— 编辑那几条**一个都不该露头**。
foreach ($片段 in @('空格 播放/暂停', 'Shift + 空格 回跳一小节并播放', 'Ctrl + ↑ ↓ 换轨')) {
  断言真 "走带那一层里有「$片段」" ($提示.Current.Name -like "*$片段*") '工单点名的那几条'
}
foreach ($片段 in @('移时间', '移音高', '改时值', '选同轨前/后一个音', 'Delete')) {
  断言真 "没选中音时屏幕上没有「$片段」（它归编辑那一层）" ($提示.Current.Name -notlike "*$片段*") `
    '36 号的两层是「此刻该看的那一类」，不是把一整行切两半随便放'
}
Write-Host '  （每一层里那几条各自的键位在 OnWindowKeyDown 里真绑着没有，由 ShortcutHintTests 那两张表机器测过，不在这儿量）'

$r提示全 = $提示.Current.BoundingRectangle
Write-Host "  全宽时提示行：$([int]$r提示全.X),$([int]$r提示全.Y) $([int]$r提示全.Width)x$([int]$r提示全.Height)"
断言真 '全宽时提示行是可见的' (-not $提示.Current.IsOffscreen) ''

# ---------- 2. 载一首曲子、选中一个音（读数栏才有东西可挤）----------
Write-Host "`n=== 2. 载一首、选中一个音 ==="
$行s = @(曲库行 | Where-Object { @(行里的文字 $_).Count -gt 0 })
if ($行s.Count -eq 0) { throw '曲库是空的 —— 这一票要在有音符的卷帘上量' }
$挑 = @($行s | Where-Object { (行里的文字 $_)[0].Current.Name -like 'Carulli*' })
$行 = if ($挑.Count) { $挑[0] } else { $行s[0] }
$曲名 = (行里的文字 $行)[0].Current.Name
# **不能点名字那一小块。** 25 号把名字格改成了「单击进编辑」，双击它就成了
# 「进编辑 + 第二下打在刚冒出来的框里」—— 曲子根本不开（第一版就是这么红的：
# 播放键灰着、0 个轨头，看着像载入坏了）。元信息格（'没动过'）是行内最干净的落点：
# 没有 ToolTip，单击选中、双击打开。
$格 = (行里的文字 $行)[1]
$r格 = 矩形 $格
Write-Host "  曲库 $($行s.Count) 行，双击「$曲名」的元信息格「$($格.Current.Name)」"
双击 ([int]($r格.X + $r格.Width / 2)) ([int]($r格.Y + $r格.Height / 2)) "双击曲库行「$曲名」"
Start-Sleep -Seconds 3

$播放键 = 按编号 'PlayButton'
断言真 '载进来之后播放键可按（曲子真的进来了）' `
  ($播放键.Count -eq 1 -and $播放键[0].Current.IsEnabled) "启用=$(if ($播放键.Count) { $播放键[0].Current.IsEnabled } else { '找不到键' })"
断言真 '卷帘上有轨头（反证：下面「改名按钮没了」不是空状态下的空过）' (数轨 -ge 1) "数到 $(数轨) 条轨"

$音 = @(找音符)
Write-Host "  扫到 $($音.Count) 条音符带"
if ($音.Count -lt 1) { throw '扫不到音符 —— 卷帘上是不是没音？下面的读数条就出不来' }
$甲 = $音[0]
点 $甲.X $甲.Y "点中音符 $($甲.X),$($甲.Y)"
Start-Sleep -Milliseconds 500
[V25]::Move($停车点[0], $停车点[1]); Start-Sleep -Milliseconds 700

$读数 = 读数元素
断言真 '选中一个音之后读数栏整块出来了（5 个值都在树里）' (@($读数 | Where-Object { $_.Count -eq 1 }).Count -eq 5) `
  "在场 $(@($读数 | Where-Object { $_.Count -eq 1 }).Count)/5"
if (@($读数 | Where-Object { $_.Count -eq 1 }).Count -ne 5) { throw '读数栏没出来 —— 窄窗那条就没得量' }
$音高值 = (按编号 'ReadoutPitchText')[0].Current.Name
断言真 '读数是这个音的值，不是初始的破折号' ($音高值 -ne '—') "音高 = $音高值"

# 36 号：选中集的个数一非零，提示行就该换成编辑那一层 —— 这一下是**在屏幕上**量的，
# 不是读代码（判据在代码里，由 ShortcutHintTests 守着）。
$提示选中 = (按编号 'HintText')[0]
断言真 '选中一个音之后提示行换成了编辑那一层（逐字）' ($提示选中.Current.Name -ceq $预期编辑) `
  "读到「$($提示选中.Current.Name)」"
foreach ($片段 in @('← → 移时间', '↑ ↓ 移音高', 'Shift + ← → 改时值', 'Ctrl + ← → 选同轨前/后一个音', 'Delete 删除')) {
  断言真 "选中之后提示里有「$片段」" ($提示选中.Current.Name -like "*$片段*") ''
}
foreach ($片段 in @('播放/暂停', '回跳一小节并播放', '换轨')) {
  断言真 "选中之后提示里没有「$片段」（走带那一层让位了）" ($提示选中.Current.Name -notlike "*$片段*") ''
}
断言真 '换层**不动 ToolTip**：它还是两行合起来的全文' ($提示选中.Current.HelpText -ceq $预期全文) `
  "读到「$($提示选中.Current.HelpText)」"

$全读 = 量读数行 '全宽·有读数' (Join-Path $PSScriptRoot 'shot26-full.png') $预期编辑
$读数Y_全 = [int]$全读.Y[0]
$读数右_全 = $全读.最右
断言真 '全宽下提示行那一句仍是全文（没有截断）' $全读.全 'UIA 读到的是全文；屏幕上有没有截断看那张图'

# ---------- 3. 拉到最小宽度 ----------
Write-Host "`n=== 3. 拉到最小宽度（MinWidth=720 逻辑像素）==="
$r窄 = 摆窗 405 300 400 1300
$宽窄 = [int]$r窄.Width
$期望窄 = [int](720 * $缩放)
Write-Host "  缩放 $缩放 倍（物理 = 逻辑 x $缩放）；要 400 宽，实得 $宽窄 x $([int]$r窄.Height) @ $([int]$r窄.X),$([int]$r窄.Y)"
断言真 '窗口被 MinWidth 夹住了（这就是「最小宽度」）' ([Math]::Abs($宽窄 - $期望窄) -le 4) `
  "实得 $宽窄 物理，MinWidth 720 逻辑 = $期望窄 物理（axaml 里 MinWidth=720）"

$读数窄 = 读数元素
断言真 '窄窗下读数栏 5 个值还都在树里' (@($读数窄 | Where-Object { $_.Count -eq 1 }).Count -eq 5) `
  "在场 $(@($读数窄 | Where-Object { $_.Count -eq 1 }).Count)/5"
断言真 '窄窗下读数还是那个音的值（没被挤空）' ((按编号 'ReadoutPitchText')[0].Current.Name -eq $音高值) `
  "窄窗 $((按编号 'ReadoutPitchText')[0].Current.Name) / 全宽 $音高值"

$窄 = 量读数行 '最小宽度·有读数' (Join-Path $PSScriptRoot 'shot26-min.png') $预期编辑
$r窄提示 = $窄.提示
$y窄 = $窄.Y
$读数右_窄 = $窄.最右

断言真 '读数栏 5 个值还在同一行（没被挤得换行错位 —— 这就是「不变形」）' `
  ((($y窄 | Measure-Object -Maximum).Maximum - ($y窄 | Measure-Object -Minimum).Minimum) -le 2) `
  "5 个 Y 差 $(($y窄 | Measure-Object -Maximum).Maximum - ($y窄 | Measure-Object -Minimum).Minimum)px"
断言真 '读数栏那一行没挪（拉窄不往下跳）' ([Math]::Abs($窄.上距 - $全读.上距) -le 4) `
  "全宽距上一行 $($全读.上距)px → 窄 $($窄.上距)px（跟上一行比，不跟窗口底边比：底下那条按钮条拉窄时会自己长高）"
断言真 '读数栏没被压扁/撑高（还是那一行高）' ([Math]::Abs($窄.行高 - $全读.行高) -le 4) `
  "全宽 $($全读.行高)px → 窄 $($窄.行高)px"
断言真 '提示行还是单行（不是换行撑成两行）' ([int]$r窄提示.Height -le [int]$全读.提示.Height + 4) `
  "全宽高 $([int]$全读.提示.Height) → 窄 $([int]$r窄提示.Height)"
断言真 '提示行在读数栏右边，没压上去' ([int]$r窄提示.X -ge ($读数右_窄 + 8)) `
  "提示行左 $([int]$r窄提示.X) vs 读数栏最右 $读数右_窄"
断言真 '提示行右边缘不出窗' (([int]$r窄提示.X + [int]$r窄提示.Width) -le ([int]$r窄.X + $宽窄 - 2)) `
  "提示行右 $([int]$r窄提示.X + [int]$r窄提示.Width) vs 窗右 $([int]$r窄.X + $宽窄)"
断言真 '提示行真的被挤窄了（窄了才有省略号可出）' ([int]$r窄提示.Width -lt [int]$全读.提示.Width) `
  "全宽 $([int]$全读.提示.Width) → 窄 $([int]$r窄提示.Width)"
断言真 '窄窗下提示行仍可见' $窄.可见 ''
Write-Host '  那一行到底有没有省略号：UIA 里读不到（TextTrimming 只影响渲染），只能看 shot26-min.png'

# ---------- 4. 悬停在提示行上：看全的完整键位表 ----------
Write-Host "`n=== 4. 悬停看全文 ==="
# 那张纸是**另一个顶层窗**（类名 `Avalonia-<guid>`、没标题），不在主窗的 UIA 子树里 ——
# 在主窗里数「逐字等于全文的 Text」怎么数都是 1 个（提示行自己），弹没弹根本看不出来。
# 纸的**内容**也不能拿 `FindAll` 去搜：`FromHandle(纸的句柄)` 拿到的是个
# `ControlType.Window`、框也落在纸的位置上，但它的 `FindAll(Descendants, …)` **一个都搜不到**
#（连 `ControlType=Text` 都是 0 个；TreeWalker 从它往下走会一路走回主窗的内容）——
# 全宽、窄窗都量过，两种宽度下都是 0。能读的是**按点读**（`FromPoint`）：
# 纸面上那个点读到 `Text`，Name 逐字就是全文（曲库行那个 ToolTip 也是这么读到的，同一把尺子）。
# 所以这一节三步：①多出一个顶层窗 ②纸面上按点读到全文 ③移开之后纸收起来。
要前台 '悬停提示行'
$别前 = @([V25]::Others($脚本PID, $h))
[V25]::Move($停车点[0], $停车点[1]); Start-Sleep -Milliseconds 400
[V25]::Move([int]($r窄提示.X + $r窄提示.Width / 2), [int]($r窄提示.Y + $r窄提示.Height / 2))
Start-Sleep -Milliseconds 1600
$别后 = @([V25]::Others($脚本PID, $h))
Write-Host "  悬停前 app 的别的顶层窗 $($别前.Count) 个，悬停后 $($别后.Count) 个$(if ($别后.Count) { '：' + (($别后 | ForEach-Object { [V25]::Describe($_) }) -join ' ') })"
断言真 '悬停之后弹出那张纸（app 多出一个顶层窗）' ($别后.Count -eq $别前.Count + 1) "从 $($别前.Count) 变成 $($别后.Count)"

$纸面 = $null; $命中点 = $null; $r纸 = $null
foreach ($w in $别后) {
  try { $r纸 = $AE::FromHandle($w).Current.BoundingRectangle } catch { continue }
  Write-Host "  多出来的窗：$([int]$r纸.X),$([int]$r纸.Y) $([int]$r纸.Width)x$([int]$r纸.Height)"
  foreach ($fy in 0.2, 0.5, 0.8) {
    foreach ($fx in 0.2, 0.5, 0.8) {
      $px = [int]($r纸.X + $r纸.Width * $fx); $py = [int]($r纸.Y + $r纸.Height * $fy)
      $at = $AE::FromPoint([System.Windows.Point]::new([double]$px, [double]$py))
      if ($at -and $at.Current.ControlType -eq $CT::Text -and $at.Current.Name -ceq $预期全文) {
        $纸面 = $at; $命中点 = @($px, $py); break
      }
    }
    if ($纸面) { break }
  }
  if ($纸面) { break }
}
断言真 '纸面上能读到「逐字就是完整键位表」的那块字' ($纸面 -ne $null) `
  $(if ($纸面) { "在 $($命中点[0]),$($命中点[1]) 读到「$($纸面.Current.Name)」（$($纸面.Current.Name.Length) 字）" } else { '纸上九个点都没读到那句话' })
if ($纸面) {
  $b纸 = $纸面.Current.BoundingRectangle
  断言真 '那块字是看得见的（没被摆到屏幕外）' (-not $纸面.Current.IsOffscreen) ''
  断言真 '那块字比屏幕上那一行高（纸是把全文折行铺开的，不是也截一行）' ([int]$b纸.Height -gt [int]$r窄提示.Height) `
    "纸上的字 $([int]$b纸.Width)x$([int]$b纸.Height) vs 屏幕上那一行 $([int]$r窄提示.Width)x$([int]$r窄提示.Height)"
  截图 (Join-Path $PSScriptRoot 'shot26-tip.png') ([int]$r纸.X) ([int]$r纸.Y) ([int]$r纸.Width) ([int]$r纸.Height) 1
  Write-Host "  那张纸存成 shot26-tip.png（整张，$([int]$r纸.Width)x$([int]$r纸.Height)）"
} else { 断言真 '弹层里那张纸读得到' $false '多出来的顶层窗上按点读不到那句话' }
[V25]::Move($停车点[0], $停车点[1]); Start-Sleep -Milliseconds 900
$别走 = @([V25]::Others($脚本PID, $h))
断言真 '鼠标移开之后那张纸收起来了（不是赖着不走的浮层）' ($别走.Count -eq $别前.Count) "从 $($别后.Count) 回到 $($别走.Count)"

# ---------- 5. 恢复全宽，量三处 ToolTip ----------
Write-Host "`n=== 5. 播放/暂停、↻ 重头播放 的 ToolTip ==="
$r回 = 摆窗 405 300 2360 1300
Write-Host "  窗口回 $([int]$r回.Width)x$([int]$r回.Height)"
$播 = 按编号 'PlayButton'
$停 = 按编号 'StopButton'          # 33 号拆掉的那颗：下面断言它**找不到**（这就是「真的拆了」）
$重播 = 按编号 'RestartButton'
断言真 '播放键只有一个' ($播.Count -eq 1) "找到 $($播.Count) 个"
断言真 '走带条上没有 ■ 停止 了（33 号换掉了）' ($停.Count -eq 0) "找到 $($停.Count) 个"
断言真 '↻ 重头播放 键只有一个' ($重播.Count -eq 1) "找到 $($重播.Count) 个"
if ($播.Count -eq 1) {
  断言真 '播放/暂停的 ToolTip 逐字是那一句' ($播[0].Current.HelpText -ceq $预期播放) "读到「$($播[0].Current.HelpText)」"
}
if ($重播.Count -eq 1) {
  断言真 '↻ 重头播放 的按钮字逐字是那一句' ($重播[0].Current.Name -ceq '↻ 重头播放') "读到「$($重播[0].Current.Name)」"
  断言真 '↻ 重头播放 的 ToolTip 逐字是那一句' ($重播[0].Current.HelpText -ceq $预期重播) "读到「$($重播[0].Current.HelpText)」"
}

# ---------- 6. 撤销/重做的键位只在一处 ----------
Write-Host "`n=== 6. 撤销/重做：只有菜单项右侧那一处 ==="
$全 = @(找类型 $CT::Text) + @(找类型 $CT::Button) + @(找类型 $CT::Edit) + @(找类型 $CT::MenuItem) + @(找类型 $CT::ComboBox)
# 「没有重复的 ToolTip」= **没有哪个元素的 ToolTip 就是那一对键位本身**。
# 不能写成「HelpText 里出现 Ctrl+Z 就算重复」：轨头那几颗按钮的 ToolTip 里有
# 「只动这条轨，别的轨原地不动 —— Ctrl+Z 可以撤销」这种**正文里提一句**。
# 第一版就是这么假红的（数出 5 个）。
#
# 36 号之后这条 regex 连提示行也命中不了了 —— 提示行的 ToolTip 里已经没有这对键位
#（下一句钉的就是这个新事实，见那里）。
$光键位 = @($全 | Where-Object { $_.Current.HelpText -match '^\s*Ctrl\s*\+?\s*[ZY]\s*$' })
断言真 '全窗没有哪个元素的 ToolTip 只写着 Ctrl+Z / Ctrl+Y（那处键位只该在菜单项右侧）' ($光键位.Count -eq 0) `
  $(if ($光键位.Count) { ($光键位 | ForEach-Object { "$($_.Current.Name)→$($_.Current.HelpText)" }) -join ' / ' } else { '一处都没有' })
$提示行自己 = (按编号 'HintText')[0]
# 26 号当年这一句是**反着**写的：那时提示行的 ToolTip 里写着「Ctrl+Z 撤销 / Ctrl+Y 重做」，
# 所以上面那条 regex 是把提示行**按内容**排除在外的（不是按元素），这一句就是那个「按内容」的证据。
# 36 号把这两条从提示行撤走了（用户：「不需要单独写……直接放到『操作』里面作为提示就可以了」），
# 于是这一句跟着变成**新的事实**：提示行自己也干净了。
断言真 '36 号起提示行与它的 ToolTip 里都不再写 Ctrl+Z / Ctrl+Y（撤出去了）' `
  (($提示行自己.Current.HelpText -notmatch 'Ctrl\+Z') -and ($提示行自己.Current.HelpText -notmatch 'Ctrl\+Y')) `
  "提示行的帮助文本：$($提示行自己.Current.HelpText)"
断言真 '工具栏上没有「撤销」这个按钮了（它进了菜单）' ((按钮 '撤销').Count -eq 0) "找到 $((按钮 '撤销').Count) 个"
断言真 '工具栏上没有「重做」这个按钮了' ((按钮 '重做').Count -eq 0) "找到 $((按钮 '重做').Count) 个"

# 菜单项右侧那处（InputGesture 渲染出来的，UIA 里是 AcceleratorKey）——
# 借 verify-23.ps1 那套：顶级菜单项**没有 ExpandCollapsePattern**，只能真点一下，而且要点完马上轮询
#（Avalonia 一失活就收弹出层）。
$操作 = @(找类型 $CT::MenuItem | Where-Object { $_.Current.Name -eq '操作' }) | Select-Object -First 1
if (-not $操作) { throw '找不到「操作」菜单' }
$项 = @()
for ($次 = 1; $次 -le 6 -and $项.Count -eq 0; $次++) {
  要前台 "展开「操作」第 $次"
  $b = $操作.Current.BoundingRectangle
  [V25]::Move($停车点[0], $停车点[1]); Start-Sleep -Milliseconds 400
  [V25]::Move([int]($b.X + $b.Width / 2), [int]($b.Y + $b.Height / 2)); Start-Sleep -Milliseconds 60
  [V25]::Press()
  foreach ($等 in 1..12) {
    Start-Sleep -Milliseconds 100
    $项 = @($操作.FindAll($TS::Descendants, (& $条件 $CT::MenuItem)))
    if ($项.Count) { break }
  }
  if ($项.Count -eq 0) { Write-Host "    [第 $次没弹出]"; [V25]::Key(0x1B); Start-Sleep -Milliseconds 300 }
}
$表 = @($项 | ForEach-Object { "$($_.Current.Name)→[$($_.Current.AcceleratorKey)]" })
Write-Host "  「操作」菜单读出来：$($表 -join ' / ')"
断言真 '「操作」菜单弹出来了（不然上面那条「ToolTip 为零」是空过）' ($项.Count -ge 2) "$($项.Count) 条"
断言真 '菜单里「撤销」的右侧写着 Ctrl+Z' `
  (@($项 | Where-Object { $_.Current.Name -eq '撤销' })[0].Current.AcceleratorKey -eq 'Ctrl+Z') `
  "'$(@($项 | Where-Object { $_.Current.Name -eq '撤销' })[0].Current.AcceleratorKey)'"
断言真 '菜单里「重做」的右侧写着 Ctrl+Y' `
  (@($项 | Where-Object { $_.Current.Name -eq '重做' })[0].Current.AcceleratorKey -eq 'Ctrl+Y') `
  "'$(@($项 | Where-Object { $_.Current.Name -eq '重做' })[0].Current.AcceleratorKey)'"
断言真 '菜单里「撤销」「重做」自己身上没有 ToolTip（删干净了，键位只在右边那一处）' `
  (@($项 | Where-Object { $_.Current.Name -in @('撤销', '重做') -and $_.Current.HelpText }).Count -eq 0) `
  $(($项 | ForEach-Object { "$($_.Current.Name)帮助='$($_.Current.HelpText)'" }) -join ' / ')
[V25]::Key(0x1B); Start-Sleep -Milliseconds 500

# ---------- 7. 轨头的「改名」连 ToolTip 一起没了 ----------
Write-Host "`n=== 7. 改名按钮 ==="
$改名 = @(按钮 '改名')
断言真 '全窗没有叫「改名」的按钮' ($改名.Count -eq 0) "找到 $($改名.Count) 个"
# 那句话本身**没有消失** —— 25 号把改名的入口从「一颗按钮」换成了「轨头的名字框」，ToolTip 跟着挪过去了
#（TrackLaneView.axaml:271 那行：`NameBox` 常显，名字格不再需要先点一下「改名」）。
# 所以这里不能断言「全窗没有这句话」（第一版就是这么假红的：4 条轨 → 4 个名字框），
# 要断言的是它**挂在哪儿**：一个按钮都不挂它，而且一条轨头正好挂一个、挂的是可编辑的框。
$拿话的 = @($全 | Where-Object { $_.Current.HelpText -like '*改这条轨的名字*' })
$拿话的钮 = @($拿话的 | Where-Object { $_.Current.ControlType -eq $CT::Button })
断言真 '挂着「改这条轨的名字…」的一个按钮都没有（按钮连 ToolTip 一起删了）' ($拿话的钮.Count -eq 0) "$($拿话的钮.Count) 个按钮"
断言真 '那句话现在挂在轨头的名字框上，一条轨一个' ($拿话的.Count -eq (数轨)) `
  "挂着 $($拿话的.Count) 个 / 轨头 $(数轨) 条（$(($拿话的 | ForEach-Object { $_.Current.ControlType.ProgrammaticName -replace 'ControlType\.', '' }) -join ' / ')）"
断言真 '挂着的都是可编辑的框（不是文字、不是菜单项）' `
  (@($拿话的 | Where-Object { $_.Current.ControlType -eq $CT::Edit }).Count -eq $拿话的.Count) '名字框是 TextBox'
断言真 '反证：这一首确实有轨头（不是空状态下的空过）' (数轨 -ge 1) "数到 $(数轨) 条轨"

  $跑完了 = $true
}
catch {
  Write-Host "`n★ 脚本跑到一半抛了：$_"
}
finally {
  try {
    $落 = @([V25]::Others($脚本PID, $h))
    if ($落.Count) { Write-Host "`n收尾：还开着的别的顶层窗 $(($落 | ForEach-Object { [V25]::Describe($_) }) -join ' ;; ')" }
  } catch { }
}

"`n========== 结果 =========="
# 「跑到一半断了」和「全过」必须分得开：断言是 Write-Host 打的，
# 脚本真要在半路抛了，上面的 OK 只覆盖到断点为止。
if (-not $跑完了) {
  '★ 跑到一半断了：断点之后的每一条都没验过，这不是全过'
  $fail = $fail + 1
} elseif ($fail -eq 0) { '全过' } else { "$fail 条红" }
[void][V25]::ShowWindow($h, 9)
[void]$proc.CloseMainWindow()
if (-not $proc.WaitForExit(8000)) { $proc.Kill() }
exit $fail
