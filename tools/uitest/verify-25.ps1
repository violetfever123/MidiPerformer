# 25 号工单的实机验证：轨头的名字就地可编辑、悬浮看全名、脚注没了、空态文案改短、
# 非法名字不产生坏文件名、改名真的落到文件系统。
#
# **32 号工单改动了这一票的两处**（改完这里，脚本才配得上「现在的真相」）：
#   · 曲库行的名字**不再就地可编辑** —— 点名字只选中这一行、双击才是打开。
#     于是 §5/§6 从「点一下就进编辑」改成断言「**不**进编辑」，判据仍是行里有没有输入框。
#   · 改名只剩顶栏那格「歌曲名」（改的是当前开着的那首），§7/§8/§9 全部改走 `改名走顶栏`。
# §1-§4、§10 没动。轨头那一半（§4 的轨头名字）仍然是 25 号当时的样子。
#
# 用法: pwsh -NoProfile -File verify-25.ps1     （脚本自己起 app、自己收尾）
#
# 复用 23 号那份驱动的骨架（UIA 真点 + 前台/点上双重闸门，那两个坑的来龙去脉见 verify-23.ps1 抬头）。
# 这一票特有的三个量法：
#   · **悬浮看全名**：Avalonia 把 ToolTip 暴露成 UIA 的 HelpText —— 实测曲库行的名字 Text
#     读得到 帮助='cargo'，所以「被省略号吃掉的名字能不能看全」是**可以直接断言**的。
#   · **非法名字**：窗口弹的不是对话框，是一个行内错误条（ErrorBox/ErrorText 置可见，见 MainWindow.ShowError），
#     所以判据是「ErrorText 有没有那句话」，不是「有没有多出一个窗口」。
#   · **改名真的动盘**：曲库就是 bin/Debug/net8.0/songs/ 下的一堆 .mproj，改名 = File.Move。
#     所以改完去数文件名，然后**改名改回去**并断言内容哈希一字未动。

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
$exe = Join-Path $PSScriptRoot '..\..\MidiPerformer.App\bin\Debug\net8.0\MidiPerformer.exe'
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
function 盘上 { @(Get-ChildItem -File $曲库目录 | Where-Object { $_.Extension -eq '.mproj' } | ForEach-Object { $_.Name } | Sort-Object) }
function 哈希([string]$名) { (Get-FileHash (Join-Path $曲库目录 $名)).Hash }
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

# 曲名那一格**原来**点一下就进改名（25 号做的），32 号把它拆了：点名字只是选中这一行，
# 双击才打开。所以这里不再有 `进编辑` 这个动作 —— 改名一律走顶栏那格「歌曲名」。
# 判「有没有进编辑」还是同一把尺子：**行里有没有冒出输入框**（行里的框 = Edit 数）。
#
# 改名走顶栏那一格（32 号之后**唯一**的入口，而且只改**当前开着的那首**）。
# 驱动它只能用 UIA：`SetFocus()` 把键盘焦点放进那一格（ValuePattern 只改文本、不给焦点），
# `ValuePattern.SetValue` 写字，再发回车 —— 这一格只认回车，没有 LostFocus 提交那条路。
# **不要用合成鼠标点它**：32c/32f 两支探针量到「点进去之后字进得去，可回车到不了
# OnSongNameKeyDown」（连 Ctrl+A 都不生效）；21 号工单给 JumpBox 用的那一路才是通的。
function 改名走顶栏([string]$到) {
  $框 = 曲名框
  if (-not $框) { throw '找不到顶栏那格「歌曲名」' }
  if (-not $框.Current.IsEnabled) { throw '曲名框是灰的（没有开着的曲子），改不了名' }
  要前台 '顶栏改名（把焦点放进那一格）'
  [void]$框.SetFocus()
  Start-Sleep -Milliseconds 300
  设值 $框 $到
  Start-Sleep -Milliseconds 300
  # 回车**不走 `按`**：`按` 开头的清场会 Take(h) + SetFocus(h)，把焦点从这一格抢回窗口，
  # 那样回车就没人接了（第 6 节的 F2 栽过同一个坑）。
  [V25]::Key(0x0D)
  Start-Sleep -Milliseconds 1500
}

$曲库目录 = (Resolve-Path (Join-Path $PSScriptRoot '..\..\MidiPerformer.App\bin\Debug\net8.0\songs')).Path
$原名 = 'Carulli_Duetto_No2_Op4'
$临时 = 'Carulli_Verify_Tmp'

# 盘上那两份先整份备份。改名本来是可逆的，但**万一脚本中途炸了**，
# 用户的曲库不该留下乱七八糟的名字 —— 这不是脚本自己的临时目录，是人家在用的东西。
$备份目录 = Join-Path $env:TEMP ('verify25-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $备份目录 | Out-Null
$开工前 = 盘上
foreach ($n in $开工前) { Copy-Item (Join-Path $曲库目录 $n) (Join-Path $备份目录 $n) -Force }
$开工前哈希 = @{}
foreach ($n in $开工前) { $开工前哈希[$n] = 哈希 $n }
"曲库目录 $曲库目录"
"开工前盘上：$($开工前 -join ' / ')"

# 「跑到底了没有」这一格是给收尾那句「全过」把关的：脚本要是中途抛了，
# 断言只覆盖到断点，而 $fail 照样是 0 —— 那种「全过」是假的。
$跑完了 = $false

try {

# =====================================================================
"`n=== 1. 空状态：文案改短了、不再指路、曲库脚注整条没了 ==="
# =====================================================================
$新句 = '还没有曲子 —— 把 .mid 拖进这个窗口，或从「文件」菜单导入。'
断言真 '卷帘区的空态文案就是改短后那一句（逐字）' ((文本 $新句).Count -eq 1) "数到 $((文本 $新句).Count) 条"
$旧一 = 文本 '还没有曲子 —— 点左上角'
$旧二 = 文本 '还没有曲子 —— 点上面'
断言真 '旧文案（指着工具栏那颗按钮的两句）一条都不剩' ($旧一.Count -eq 0 -and $旧二.Count -eq 0) "左上角 $($旧一.Count) 条 / 上面 $($旧二.Count) 条"
$指路 = 文本 '导入 MIDI'
断言真 '全窗口没有文字还写着「导入 MIDI…」（不再靠位置指路）' ($指路.Count -eq 0) "数到 $($指路.Count) 条"
断言真 '曲库脚注「导入的曲子存进…」整条没了' ((文本 '导入的曲子存进').Count -eq 0) "数到 $((文本 '导入的曲子存进').Count) 条"
断言真 '脚注后半句「改名就是改文件名」也没了' ((文本 '改名就是改文件名').Count -eq 0) "数到 $((文本 '改名就是改文件名').Count) 条"
断言真 '空状态下也没有名字叫「改名」的按钮' ((按钮 '改名').Count -eq 0) "数到 $((按钮 '改名').Count) 个"

# =====================================================================
"`n=== 2. 从曲库点开一首（4 条轨）==="
# =====================================================================
$行 = 找行 $原名
if (-not $行) { throw "曲库里没有「$原名」" }
[void]$行.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 400
[void]$行.SetFocus()
Start-Sleep -Milliseconds 400
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
Start-Sleep -Seconds 4
断言真 '曲库点开之后真的载进来了（4 条轨）' ((数轨) -eq 4) "$(数轨) 条轨"

# =====================================================================
"`n=== 3. 「改名」按钮全没了、名字格常驻 ==="
# =====================================================================
断言真 '载了曲之后也没有名字叫「改名」的按钮（轨头 + 曲库行一起数）' ((按钮 '改名').Count -eq 0) "数到 $((按钮 '改名').Count) 个"
$名框 = 按编号 'NameBox'
断言真 '轨头 4 个名字格都在树里' ($名框.Count -eq 4) "数到 $($名框.Count) 个"
$名框看不见 = @($名框 | Where-Object { $_.Current.IsOffscreen }).Count
断言真 '轨头的名字格**常驻可见**（不是藏在某颗按钮后面）' ($名框看不见 -eq 0) "看不见的有 $名框看不见 个"
$名框提示对 = @($名框 | Where-Object { $_.Current.HelpText -eq '改这条轨的名字（回车确定，Esc 取消）' }).Count
断言真 '名字格的悬浮提示写着怎么提交、怎么取消' ($名框提示对 -eq 4) "命中 $名框提示对 个"
$行内框 = @(曲库行 | ForEach-Object { 行里的框 $_ })
断言真 '曲库行里没有常驻输入框（平时还是文字，点了才顶上来的那个不算）' ($行内框.Count -eq 0) "数到 $($行内框.Count) 个"

# =====================================================================
"`n=== 4. 悬浮看全名（Avalonia 把 ToolTip 露成 UIA 的 HelpText）==="
# =====================================================================
foreach ($名 in @('cargo', $原名)) {
  $此 = 找行 $名
  if (-not $此) { throw "找不到「$名」那一行" }
  $格 = (行里的文字 $此)[0]
  断言真 "曲库行「$名」悬浮显示完整曲名" ($格.Current.HelpText -eq $名) "帮助='$($格.Current.HelpText)'"
}
断言真 '那个长名字确实是长的（上一条断言才有意义）' ($原名.Length -ge 16) "$($原名.Length) 个字符"

# =====================================================================
"`n=== 5. 点曲名**不进**编辑（32 号把这条路拆了：点名字只是选中这一行）==="
# =====================================================================
$行 = 找行 $原名
$c = 中心 (曲名格 $行)
点 $c[0] $c[1] "点曲名「$原名」（该只是选中）"
Start-Sleep -Milliseconds 700
$行 = 找行 $原名
if (-not $行) { throw '点完那一行不见了 —— 名字那格的点击把行拆了？' }
断言真 '点曲名之后行里**没有**冒出输入框' ((行里的框 $行).Count -eq 0) "行里的框 $((行里的框 $行).Count) 个"
断言真 '点曲名把这一行选中了（点击没被吃掉）' (选中了 $行) "选中=$(选中了 $行)"
断言真 '点曲名不会把歌打开（曲名框还是原来那首）' ((取值 (曲名框)) -eq $原名) "曲名框='$(取值 (曲名框))'"

# =====================================================================
"`n=== 6. F2 那条键盘路也拆了（鼠标和键盘是一起拆的）==="
# =====================================================================
# F2 从前是**列表上的**快捷键（OnListKeyDown 挂在 ListBox 上），所以先得像用户那样把列表
# 点活：点这一行右边那格小字（选中它、焦点落到列表上），再按 F2。
# 落点挑小字而不是「行内边距」是有实测依据的：ListBoxItem 的 UIA 矩形比它能接点击的范围
# 大一圈，贴着行边缘点（离上边 2px）是**死区**，什么都不发生 —— 而那一格小字是实打实的元素。
#
# 这一条的力气只有一半，得说清楚：判据是「F2 之后行里没有输入框」，而「F2 根本没送到列表」
# 也满足它 —— 分不开「送到了、被无视了」和「压根没送」。前半句有 §6 上面那次点击做的
# 「这一行确实选中了」兜着（列表是活的），但这仍然不是一条能证明「F2 被处理过」的断言。
$行 = 找行 $原名
$c = 中心 (小字 $行)
点 $c[0] $c[1] "点「$原名」那一行的小字（先把列表点活）"
Start-Sleep -Milliseconds 500
$行 = 找行 $原名
断言真 '点小字把这一行选中了（F2 才有落点）' (选中了 $行) "选中=$(选中了 $行)"
# F2 这**一下**特意不走 `按`（它开头要抢一次前台）：Take 里那句 `SetFocus(h)` 会把键盘焦点
# 从刚点上的那个 ListBoxItem 挪回窗口上，而 F2 恰恰是挂在 ListBox 上的 —— 抢完再发，
# F2 就没人接了。实测栽过一回。
if ([V25]::GetForegroundWindow() -ne $h) { 清场 -1 -1 'F2' }
[V25]::Key(0x71)
Start-Sleep -Milliseconds 800
$行 = 找行 $原名
断言真 'F2 之后行里**也没有**输入框（改名那条键盘路拆干净了）' ((行里的框 $行).Count -eq 0) "行里的框 $((行里的框 $行).Count) 个"
断言真 'F2 之后这一行还是原来那个名字' ((曲名格 $行).Current.Name -eq $原名) "'$(行名 $行)'"

# =====================================================================
"`n=== 7. 改名真的落到文件系统（走顶栏那一格，改出去、再改回来）==="
# =====================================================================
$原哈希 = 哈希 "$原名.mproj"
改名走顶栏 $临时
断言真 "盘上真的出现了「$临时.mproj」" (Test-Path (Join-Path $曲库目录 "$临时.mproj")) "盘上现在：$((盘上) -join ' / ')"
断言真 "盘上原来那份「$原名.mproj」没了" (-not (Test-Path (Join-Path $曲库目录 "$原名.mproj"))) "盘上现在：$((盘上) -join ' / ')"
断言真 '窗口喊了一句「改成了」' ((文本 '改成了').Count -ge 1) "数到 $((文本 '改成了').Count) 条"
断言真 '改名动的是文件名，不是又抄了一份（文件个数没变）' ((盘上).Count -eq $开工前.Count) "$((盘上).Count) 个（开工前 $($开工前.Count) 个）"

改名走顶栏 $原名
断言真 '再改回来，盘上文件名回到原样' ((盘上) -contains "$原名.mproj") "盘上现在：$((盘上) -join ' / ')"
断言真 '改回来之后**内容一个字节都没动**（改名只动文件名）' ((哈希 "$原名.mproj") -eq $原哈希) "$(哈希 "$原名.mproj") 对 $原哈希"

# =====================================================================
"`n=== 8. 空名字被挡住，而且盘上什么都不动 ==="
# =====================================================================
# 这句错话 32 号之后换了措辞（从「曲名是空的」变成「不能当曲名：曲名就是文件名…」）：
# 判据跟着改成新的那句，不然这一条会在「功能其实是对的」的时候红。
$这次前 = 盘上
改名走顶栏 ''
$错 = 文本 '不能当曲名'
断言真 '空名字被挡下来了：窗口冒出那句现成的中文' ($错.Count -ge 1) "数到 $($错.Count) 条；窗口里的错字是「$(if($错.Count){$错[0].Current.Name})」"
断言真 '空名字下盘上一个文件都没动' (((盘上) -join '|') -eq ($这次前 -join '|')) "现在：$((盘上) -join ' / ')"
断言真 '空名字之后曲库行还好好地叫原名' ((找行 $原名) -ne $null) "现在有：$((曲库行 | ForEach-Object { 行名 $_ }) -join ' / ')"
断言真 '空名字之后曲名框退回原名（不是留着一个空框）' ((取值 (曲名框)) -eq $原名) "曲名框='$(取值 (曲名框))'"

# =====================================================================
"`n=== 9. 名字里的非法字符被消毒，不产生坏文件名 ==="
# =====================================================================
# `Carulli/Tmp:Bad` 里的 / 和 : 是 Windows 文件名里不能用的（SongLibrary.InvalidNameChars）。
# 工单要的是「不产生坏文件名」—— 判据是盘上没有带 / 或 : 的名字，
# 而不是「它一定被拦下来」（实测是被消毒成 CarulliTmpBad 放行）。
改名走顶栏 'Carulli/Tmp:Bad'
$带坏字符 = @(盘上 | Where-Object { $_ -match '[/:\\:*?"<>|]' })
断言真 '名字里的 / 和 : 一个都没落到文件名上' ($带坏字符.Count -eq 0) "带坏字符的有：$($带坏字符 -join ' / ')"
断言真 '消毒之后的名字落在盘上（CarulliTmpBad.mproj）' ((盘上) -contains 'CarulliTmpBad.mproj') "盘上现在：$((盘上) -join ' / ')"
改名走顶栏 $原名
断言真 '消毒那一轮过完，名字回到原名' ((盘上) -contains "$原名.mproj") "盘上现在：$((盘上) -join ' / ')"
断言真 '这一轮下来内容依然一个字节都没动' ((哈希 "$原名.mproj") -eq $原哈希) "$(哈希 "$原名.mproj") 对 $原哈希"

# =====================================================================
"`n=== 10. 单击选中 / 双击打开，两条路没互相抢 ==="
# =====================================================================
# 落点挑**右边那格小字**（'没动过'）：它是 TextBlock，
#   · 单击 = 点在这一行上 → 只选中（32 号之后名字那一格上连 Tapped 都没有了）；
#   · 双击 = 落到 ListBoxItem 上 → OnRowDoubleTapped 那道「祖先里有 Button 就拦下」
#     的闸门不拦它（它不是按钮）→ 该打开；
#   · 而且它**没有 ToolTip**（ToolTip 只挂在名字和删除上），删除那一颗点了会弹确认框。
# 先单击一次「点在这一行上会不会误进改名」，再双击验「会不会打开」，两件事分开。
$行 = 找行 'cargo'
$元 = 小字 $行
断言真 '右边那格小字认出来了（就是「改过没改过」那一格）' ($元.Current.Name -eq '没动过') "'$($元.Current.Name)'"
$c = 中心 $元
$r行 = 矩形 $行; $r元 = 矩形 $元
Write-Host "    落点：行 $([int]$r行.X),$([int]$r行.Y) $([int]$r行.Width)x$([int]$r行.Height)；小字那一格 $([int]$r元.X),$([int]$r元.Y) $([int]$r元.Width)x$([int]$r元.Height)；点 $($c[0]),$($c[1])"
点 $c[0] $c[1] '单击曲库行的小字（该只是选中，不进编辑）'
# 分两次读：150ms 一次、再 550ms 一次。中间被谁改回去了的话，两次读数会不一样 ——
# 那样的话「点了没选中」和「选中了又被清掉」是两句完全不同的话，得分开。
Start-Sleep -Milliseconds 150
Write-Host "    按下后 150ms：cargo 选中=$(选中了 (找行 'cargo'))  Carulli 选中=$(选中了 (找行 $原名))  外面的窗：$((@([V25]::Others($脚本PID, $h)) | ForEach-Object { [V25]::Describe($_) }) -join ' ;; ')"
Start-Sleep -Milliseconds 550
$行 = 找行 'cargo'
$卡 = 找行 $原名
Write-Host "    按下后 700ms：cargo 选中=$(选中了 $行)  Carulli 选中=$(选中了 $卡)"
if (-not (选中了 $行)) {
  # 第一次没中就补一次 —— 和第 6/9 节那条改名路一样的手法，好把「第一下不生效」
  # 和「这条路根本不通」分开。补上了会在这一行下面直接说。
  Write-Host "    ★ 第一下没选中。点上读到的是 $([V25]::Describe([V25]::At($c[0], $c[1])))"
  $行2 = 找行 'cargo'
  $c = 中心 (小字 $行2)
  点 $c[0] $c[1] '再点一次曲库行的小字'
  Start-Sleep -Milliseconds 700
  $行 = 找行 'cargo'
  $卡 = 找行 $原名
  Write-Host "    → 补点一次：cargo 选中=$(选中了 $行)  Carulli 选中=$(选中了 $卡)"
}
断言真 '单击非名字区**选中了这一行**' (选中了 $行) "选中=$(选中了 $行)"
断言真 '单击非名字区把原来选中的那行让了出去' ($卡 -eq $null -or -not (选中了 $卡)) "Carulli 选中=$(if($卡){选中了 $卡}else{'行没了'})"
断言真 '单击非名字区**没有**进编辑（两条路没打架）' ((行里的框 $行).Count -eq 0) "行里的框 $((行里的框 $行).Count) 个"
断言真 '单击没有把歌打开（曲名框还是 Carulli）' ((取值 (曲名框)) -eq $原名) "曲名框='$(取值 (曲名框))'"

$元 = 小字 (找行 'cargo')
$c = 中心 $元
双击 $c[0] $c[1] '双击曲库行的小字（该打开这一首）'
Start-Sleep -Seconds 5
if ((取值 (曲名框)) -ne 'cargo') {
  Write-Host "    ★ 双击没打开。点上读到的是 $([V25]::Describe([V25]::At($c[0], $c[1])))；曲名框还是 '$(取值 (曲名框))'"
  $元 = 小字 (找行 'cargo')
  $c = 中心 $元
  双击 $c[0] $c[1] '再双击一次曲库行的小字'
  Start-Sleep -Seconds 5
  Write-Host "    → 补双击一次：曲名框='$(取值 (曲名框))' 轨数=$(数轨)"
}
断言真 '双击真的把那一首打开了（曲名框变成 cargo）' ((取值 (曲名框)) -eq 'cargo') "曲名框='$(取值 (曲名框))'；轨数 $(数轨)"
# 打开另一首之后把原来那首开回来，别把 app 留在别的曲子上
$行 = 找行 $原名
# 下面那一下 SendKeys('{ENTER}') 投给的是**当前有焦点的窗口**，先确认前台是 app ——
# 别的窗口盖在上面时，这一下会往别人的编辑器里敲一个回车（32 号实测栽过）。
要前台 "把「$原名」开回来"
[void]$行.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 400
[void]$行.SetFocus()
Start-Sleep -Milliseconds 400
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
Start-Sleep -Seconds 5
断言真 '把 Carulli 开回来，曲名框回到原名' ((取值 (曲名框)) -eq $原名) "曲名框='$(取值 (曲名框))'；轨数 $(数轨)"

  $跑完了 = $true
}
catch {
  # 中途抛了就当场喊一声，别让收尾那句「全过」把人骗过去
  Write-Host "`n★ 脚本跑到一半抛了：$_"
}
finally {
  # 无条件还原曲库：先把盘上所有 .mproj 清掉，再把开工前备份的整份拷回来，逐字节对一遍。
  # 这个脚本动的是**用户的曲库**，不能停在「应该没问题」上。
  try {
    Get-ChildItem -File $曲库目录 -Filter *.mproj -EA SilentlyContinue | Remove-Item -Force -EA SilentlyContinue
    foreach ($n in $开工前) { Copy-Item (Join-Path $备份目录 $n) (Join-Path $曲库目录 $n) -Force }
    $还原后 = 盘上
    $对得上 = (($还原后 -join '|') -eq ($开工前 -join '|'))
    foreach ($n in $开工前) { if ((哈希 $n) -ne $开工前哈希[$n]) { $对得上 = $false } }
    if ($对得上) { "`n曲库已还原：$($还原后 -join ' / ')（逐字节和开工前一致）" }
    else { "`n★ 曲库还原没对上！现在：$($还原后 -join ' / ')，开工前：$($开工前 -join ' / ')" }
    Remove-Item $备份目录 -Recurse -Force -EA SilentlyContinue
  } catch { "`n★ 还原时出错：$_" }
}

"`n========== 结果 =========="
# 「跑到一半断了」和「全过」必须分得开：断言是 Write-Host 打的、收尾这几句是输出流，
# 脚本真要是在半路抛了，上面的 OK 只覆盖到断点为止 —— 实测栽过一回：
# 断在第 5 节，这里照样印出「全过」（$fail 是 0，因为红的那几条压根没跑到）。
if (-not $跑完了) {
  '★ 跑到一半断了：断点之后的每一条都没验过，这不是全过'
  $fail = $fail + 1
} elseif ($fail -eq 0) { '全过' } else { "$fail 条红" }
[void][V25]::ShowWindow($h, 9)
[void]$proc.CloseMainWindow()
if (-not $proc.WaitForExit(8000)) { $proc.Kill() }
exit $fail
