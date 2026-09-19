# 27 号工单的实机验证：轨头那行灰字删干净了、演奏器窗口的常驻风险横幅删干净了、
# 三种「没开始」诊断还在。
#
# 用法: pwsh -NoProfile -File verify-27.ps1     （脚本自己起 app、自己收尾）
#
# 复用 23 号那份驱动、25 号补过的浮层闸门（点之前清场那套的来龙去脉见 verify-25.ps1 抬头）。
# 这一票特有的四个量法：
#   · **「删干净了」不能只看字**：元素整个没了和元素还在只是隐着，UIA 里是两种样子
#     （IsVisible=False 的元素**不进** UIA 树）。所以除了搜「只看这条」，还要按 AutomationId
#     直接找 `LaterText` 这个元素本身 —— 它在树里，就说明只是藏起来了。
#   · **反证**：载一首曲子，先断言 4 个轨头（`Head`）和 4 个音数都在场。
#     不然「灰字 0 条」在空状态下也成立，那是**空过**不是通过。
#   · **右边空出来那一条有多宽**：工单里点名的那条风险（`Grid.Column="9"` 那一格空着，
#     会不会看着像少了点什么）。量法是 Head 的右边缘减去音数的右边缘 = 那片空白的宽度，
#     再扫一遍「这片空白里有没有任何元素」——「真的空」和「少了点东西」是两句话。
#   · **三种诊断**：权限那一句能真的触到（本脚本在非提权下跑，app 继承令牌）；
#     输入法那一句**触不到**（预检第一关就是权限，非提权进程永远走不到第二关）；
#     轨不可弹那一句**按构造成触不到**（下拉框只列可弹的轨，空的时候开始按钮是灰的）。
#     后者两条在工单里如实写着「没验」，不假装。

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


$曲名 = 'Carulli_Duetto_No2_Op4'
$MIDI = 'C:\Users\cao17\Desktop\midiplayer\drywetmidi\Resources\MIDI files\Valid\MultiTrack\Middle\Carulli_Duetto_No2_Op4.mid'

# 这一票不碰曲库（不改名、不删），所以没有 25 号那套备份/还原 —— 只读它、点开一首曲子。
function 按编号全([string]$id) {
  $c = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
  @($root.FindAll($TS::Descendants, $c))
}
function 某根里([object]$根, [object]$类型) { @($根.FindAll($TS::Descendants, (& $条件 $类型))) }
function 某根编号([object]$根, [string]$id) {
  $c = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
  @($根.FindAll($TS::Descendants, $c))
}
function 某根文字([object]$根, [string]$含) {
  @(某根里 $根 $CT::Text | Where-Object { $_.Current.Name -like "*$含*" })
}
function 别的顶层窗 { @([V25]::Others($脚本PID, $h)) }
function 类名([IntPtr]$w) {
  $缓 = New-Object System.Text.StringBuilder 256
  [void][V25]::GetClassName($w, $缓, 256)
  $缓.ToString()
}
function 找原生框 {
  foreach ($w in 别的顶层窗) { if ((类名 $w) -like '#32770*') { return $w } }
  return $null
}
function 查别的窗([string]$标) {
  $o = 别的顶层窗
  $说 = if ($o.Count) { ($o | ForEach-Object { $r = [D25]::RectOf($_); "$([V25]::Describe($_)) 框=$($r[0]),$($r[1]) $($r[2])x$($r[3])" }) -join ' ;; ' } else { '没有' }
  Write-Host "  [$标] app 的别的顶层窗 $($o.Count) 个：$说"
}

# 「跑到一半断了」和「全过」要分得开（25 号那口坑，见文件尾）。
$跑完了 = $false

try {

# =====================================================================
"`n=== 0. 安全闸：本脚本必须在非提权下跑 ==="
# =====================================================================
# app 是本进程的子进程，令牌一样。**一旦提权**，第 5 节按下「开始演奏」就会真的过预检、
# 真的开始往系统里发按键 —— 发到那时前台的那个窗口上。那不是这一票想干的事，
# 所以宁可不跑：这一条挡不住风险的时候，别的都白说。
$我是管理员 = (New-Object Security.Principal.WindowsPrincipal(
  [Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($我是管理员) { throw '本脚本必须在**非提权**下跑：提权之后按下「开始演奏」会真的往系统里发按键' }
Write-Host '  当前非提权 —— app 继承同样的令牌，第 5 节的「开始」必然停在权限那一关'

# =====================================================================
"`n=== 1. 载一首曲子（轨头得先在场，否则下面那些「0 条」都是空过） ==="
# =====================================================================
$行 = 找行 $曲名
if (-not $行) { throw "曲库里没有「$曲名」这一行（现在有：$((曲库行 | ForEach-Object { 行名 $_ }) -join ' / ')）" }
[void]$行.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 400
[void]$行.SetFocus()
Start-Sleep -Milliseconds 400
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
Start-Sleep -Seconds 5
$轨数 = 数轨
断言真 '曲子真的载进来了（4 条轨的轨头都在场）' ($轨数 -eq 4) "$轨数 条轨"

# =====================================================================
"`n=== 2. 轨头那行灰字：删掉了，还是只是藏起来了 ==="
# =====================================================================
# 两种判据都要：搜文字（用户看得见的那一句）+ 按 AutomationId 找元素本身。
# 只搜文字的话，「元素还在、只是 IsVisible=False」也会是 0 条（隐藏的元素不进 UIA 树）——
# 那是两种不同的交付，工单要的是**删掉**。
$灰 = 文本 '只看这条'
断言真 '「只看这条」这几个字一条都不剩' ($灰.Count -eq 0) "数到 $($灰.Count) 条"
$还没 = 文本 '还没做'
断言真 '「还没做」也一条都不剩（别的控件别处也没有这句）' ($还没.Count -eq 0) "数到 $($还没.Count) 条"
$元素 = 按编号全 'LaterText'
断言真 'LaterText 这个元素本身不在 UIA 树里了（不是仅隐藏）' ($元素.Count -eq 0) "数到 $($元素.Count) 个"

# 反证：上面三条在「一条轨都没有」的空窗口上照样成立。轨头真在场，它们才说明问题。
#
# 轨头这一格**不能按 AutomationId 找**，两条路都堵死（都是实测）：
#   · `Head` 是个 Border —— 它根本不进 UIA 树（按 id 数到 0 个）；
#   · `CountText` **撞车** —— 曲库面板底部那个「2 首」也叫 CountText，全窗口数到 5 个。
# 所以改成**从每条轨都有的「折叠」按钮往上爬**：轨头那一行横跨整条轨，
# 往上第一个「宽到几百像素、里面又只读得到一个 CountText」的祖先就是它。
$走 = [System.Windows.Automation.TreeWalker]::RawViewWalker
$头们 = @()
$数们 = @()
$折们 = @(找类型 $CT::Button | Where-Object { $_.Current.Name -eq '折叠' })
foreach ($折 in $折们) {
  $p = $折
  $层 = @()
  for ($i = 0; $i -lt 8; $i++) {
    $p = $走.GetParent($p)
    if (-not $p) { break }
    $层 += $p
  }
  $好 = $null; $好数 = $null
  foreach ($q in $层) {
    $n = @(某根编号 $q 'CountText')
    if ($n.Count -eq 1 -and (矩形 $q).Width -gt 600) { $好 = $q; $好数 = $n[0]; break }
  }
  if ($好) { $头们 += $好; $数们 += $好数 } else { Write-Host "    ★ 有一颗「折叠」往上找不到轨头行（爬了 $($层.Count) 层）" }
}
断言真 '反证：4 条轨的轨头行都找得到（每颗「折叠」往上都有一行横跨整轨的容器）' ($折们.Count -eq 4 -and $头们.Count -eq 4) "折叠按钮 $($折们.Count) 个，轨头行 $($头们.Count) 个"
断言真 '反证：每个轨头里都读得到一个音数（CountText）' ($数们.Count -eq 4) "数到 $($数们.Count) 个"
if ($数们.Count) { Write-Host "    音数那一格现在写的是「$($数们[0].Current.Name)」" }

# =====================================================================
"`n=== 3. 右边空出来那一条有多宽（工单点名要看的那一眼） ==="
# =====================================================================
# 工单里的担心：`Grid.Column="9"` 那一格空着（列定义没删，是为了不动九处 Grid.Column），
# 会不会在轨头右边留出一条「看着像少了点什么」的空白。
# 量法：Head 的右边缘 − 音数的右边缘 = 从最后一个真元素到行尾的距离；
# 再扫一遍这片矩形里有没有任何元素 —— 「真的空」和「少了点东西」是两句话。
$量过 = @()
foreach ($头 in $头们) {
  $r头 = 矩形 $头
  $格 = @(某根编号 $头 'CountText')
  if ($格.Count -ne 1) { Write-Host "    ★ 这个轨头里有 $($格.Count) 个音数格，跳过"; continue }
  $r音 = 矩形 $格[0]
  $空白左 = [int]($r音.X + $r音.Width)
  $空白右 = [int]($r头.X + $r头.Width)
  $空白宽 = $空白右 - $空白左
  # 这片空白里还有没有东西：
  $里头 = @(某根里 $头 $CT::Text) + @(某根里 $头 $CT::Button) + @(某根里 $头 $CT::Edit) + @(某根里 $头 $CT::ComboBox)
  $闯进 = @($里头 | Where-Object {
    $r = 矩形 $_
    $右 = [int]($r.X + $r.Width)
    $_.Current.AutomationId -ne 'CountText' -and $右 -gt $空白左 + 1 -and [int]$r.X -lt $空白右 - 1
  })
  $量过 += [pscustomobject]@{
    头 = "$([int]$r头.X),$([int]$r头.Y)"
    头宽 = [int]$r头.Width
    音数右 = $空白左
    空白宽 = $空白宽
    空白里的元素 = $闯进.Count
  }
  Write-Host "    轨头 @$([int]$r头.X),$([int]$r头.Y) 宽 $([int]$r头.Width)：音数右边缘 $空白左，行右边缘 $空白右"
  Write-Host "      → 右边空出 $空白宽 px；这片空白里的元素 $($闯进.Count) 个$(if($闯进.Count){'：' + (($闯进 | ForEach-Object { "id='$($_.Current.AutomationId)' 名='$($_.Current.Name)'" }) -join ' / ')})"
}
断言真 '每个轨头右边那片空白里都是真的空（一个元素都没有）' ($量过.Count -eq 4 -and @($量过 | Where-Object { $_.空白里的元素 -ne 0 }).Count -eq 0) "量到 $($量过.Count) 个轨头：$(($量过 | ForEach-Object { "$($_.空白宽)px" }) -join ' / ')"
Write-Host '    （「看着像不像少了点东西」这一条，量到的是「那片像素上什么都没有」；好不好看归人工）'

# =====================================================================
"`n=== 4. 演奏器窗口：常驻风险横幅没了、状态行还在 ==="
# =====================================================================
$开钮 = 按钮 '演奏器…'
断言真 '工具栏上那颗「演奏器…」还在（入口没被顺手删掉）' ($开钮.Count -eq 1) "数到 $($开钮.Count) 颗"
[void]$开钮[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
$期限 = (Get-Date).AddSeconds(20)
$演奏器 = [IntPtr]::Zero
while ((Get-Date) -lt $期限 -and $演奏器 -eq [IntPtr]::Zero) {
  foreach ($w in 别的顶层窗) {
    try { if ((某根编号 ($AE::FromHandle($w)) 'StartButton').Count -gt 0) { $演奏器 = $w; break } } catch { }
  }
  if ($演奏器 -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 400 }
}
if ($演奏器 -eq [IntPtr]::Zero) { throw '等不到演奏器窗口（按了「演奏器…」之后没多出 StartButton 那个顶层窗）' }
Start-Sleep -Seconds 2
$根二 = $AE::FromHandle($演奏器)
$r二 = [D25]::RectOf($演奏器)
断言真 '演奏器窗口真的开出来了' ($r二[2] -gt 300) "$([V25]::Describe($演奏器)) 框=$($r二[0]),$($r二[1]) $($r二[2])x$($r二[3])"

# 横幅那一整块的两句话，一个字都不该剩。窗口子树里搜 + 主窗口子树里也搜一遍
# （万一有人把它挪到主窗口去，只搜演奏器窗口就会漏掉）。
$横幅一 = @(某根文字 $根二 '虚拟输入违反游戏规则') + @(某根文字 $root '虚拟输入违反游戏规则')
$横幅二 = @(某根文字 $根二 '本程序只做一件事') + @(某根文字 $root '本程序只做一件事')
$横幅三 = @(某根文字 $根二 '风险自负') + @(某根文字 $root '风险自负')
断言真 '「虚拟输入违反游戏规则，可能导致封号」那一句没了' ($横幅一.Count -eq 0) "数到 $($横幅一.Count) 条"
断言真 '「本程序只做一件事：往系统里发按键…」那一句也没了' ($横幅二.Count -eq 0) "数到 $($横幅二.Count) 条"
断言真 '整个程序里再也搜不到「风险自负」' ($横幅三.Count -eq 0) "数到 $($横幅三.Count) 条"

# 删横幅不该把「为什么没开始」的通道一起带走。
$状 = 某根编号 $根二 'StatusText'
断言真 '状态行还在（横幅删了没把它一起带走）' ($状.Count -eq 1) "数到 $($状.Count) 个"
if ($状.Count -eq 1) {
  $r状 = 矩形 $状[0]
  $窗口底 = $r二[1] + $r二[3]
  $状态底 = [int]($r状.Y + $r状.Height)
  Write-Host "    窗口 430x$($r二[3])，底边 y=$窗口底；状态行 y=$([int]$r状.Y) 高 $([int]$r状.Height)，底边 y=$状态底"
  Write-Host "      → 状态行下面还剩 $($窗口底 - $状态底) px（StackPanel 的 Margin=14 就是它）"
  断言真 '状态行可见（不是藏在窗口外面）' (-not $状[0].Current.IsOffscreen) "IsOffscreen=$($状[0].Current.IsOffscreen)"
  断言真 '状态行整块落在窗口矩形里' ($状态底 -le $窗口底 -and [int]$r状.Y -ge $r二[1]) "状态行底 $状态底 ≤ 窗口底 $窗口底"
  # 「窗口底下面就是状态行」= 没有元素被挤到可视区外面去。窗口是 SizeToContent="Height"，
  # 高度就是内容撑出来的，所以这一条量的是「撑出来的高度装得下所有东西」。
  $越界 = @()
  foreach ($t in @(某根里 $根二 $CT::Text) + @(某根里 $根二 $CT::Button) + @(某根里 $根二 $CT::ComboBox) + @(某根里 $根二 $CT::Edit)) {
    $r = 矩形 $t
    if (-not $t.Current.IsOffscreen -and [int]($r.Y + $r.Height) -gt $窗口底 + 1) { $越界 += $t }
  }
  断言真 '没有控件被顶到窗口底边外面去（SizeToContent 撑得下）' ($越界.Count -eq 0) "越界的 $($越界.Count) 个"
}

# =====================================================================
"`n=== 5. 「没开始」三种诊断：能触到的那一种，逐字量 ==="
# =====================================================================
# 预检的顺序是**权限 → 输入法 → 有没有能弹的轨**（见 PerformancePreflight 的类注释）。
# 本脚本非提权，所以按下去必然停在第一关 —— 这也顺带把「顺序」这件事实测了一遍：
# 后面两关的文案这次一个字都读不到。
$预期 = '没开始：要以管理员身份运行。不然发的按键会被系统挡在游戏窗口外面 —— 一个音都收不到，还不报错。'

# ---------- 把「在那个原生框里开一首曲子」封成一个函数 ----------
# **这个框里什么都暴露成 Pane**：没有 Edit、也没有 Button。文件名那一格是 `Pane id='1148'`
# （里面还套着两层同名 Pane，真正的编辑框不进 UIA 树 —— 对它 SetFocus 直接抛
# 「目标元素无法接收焦点」，量过），「打开」那颗是 `Pane id='1' 名='打开(O)'`。
# 所以能走的路只有一条：**从列表里选中那一首，再按「打开」**。
#
# 三个坑，全是实测撞出来的：
#   1. **id='1' 会撞车**：列表里第一行的 ListItem 也是 `id='1'`，而且它自己支持 InvokePattern
#      （在文件框里双击一行 = 打开它）。只按 id 取第一个元素，那一击打开的就是列表里第一首
#      （实测开成了 65tmmntw.mid）。**认名字，别认序号。**
#   2. **列表是异步填的**：框刚开时 ListItem 可能还是空的，要轮询等那一行出现。
#   3. **「打开」那颗三种模式都不认**：InvokePattern 抛「不支持的模式」，
#      LegacyIAccessiblePattern 在本机直接「找不到类型」（pwsh 7 不带它），
#      最后只能**真点它一下** —— 而这一下**不能走 `点`**：那条路带的闸门要求落点属于主窗口，
#      这一颗在原生框上，闸门会当场拒绝。框是 app 自己弹的模态框、就在最上面，落点没有歧义。
function 开一首([string]$文件名) {
  $开钮2 = 某根编号 ($AE::FromHandle($演奏器)) 'OpenButton'
  if ($开钮2.Count -ne 1) { throw "演奏器窗口里找不到「打开 MIDI…」（数到 $($开钮2.Count) 颗）" }
  [void]$开钮2[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  $期限 = (Get-Date).AddSeconds(25)
  $原生框 = $null
  while ((Get-Date) -lt $期限 -and -not $原生框) { $原生框 = 找原生框; if (-not $原生框) { Start-Sleep -Milliseconds 400 } }
  if (-not $原生框) { throw '等不到「打开 MIDI 文件」的原生框' }
  Start-Sleep -Milliseconds 900
  $r框 = [D25]::RectOf($原生框)
  $框根 = $AE::FromHandle($原生框)
  $项 = @()
  $期限 = (Get-Date).AddSeconds(15)
  while ((Get-Date) -lt $期限 -and $项.Count -eq 0) {
    $项 = @(某根里 $框根 $CT::ListItem | Where-Object { $_.Current.Name -eq $文件名 })
    if ($项.Count -eq 0) { Start-Sleep -Milliseconds 500 }
  }
  if ($项.Count -eq 0) {
    $在 = (某根里 $框根 $CT::ListItem | ForEach-Object { $_.Current.Name }) -join ' / '
    throw "「$文件名」不在这个框的列表里（它现在开在：$在）—— 这个框记得的是上次用过的目录，换了目录这条就找不到"
  }
  [void]$项[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  Start-Sleep -Milliseconds 600
  $开 = @(某根编号 $框根 '1' | Where-Object { $_.Current.Name -like '打开*' })
  if ($开.Count -eq 0) { throw '原生框里找不到名字叫「打开…」的那一颗（id=1 的元素里没有一个对得上）' }
  Write-Host "  框 $($r框[2])x$($r框[3]) 里选中「$文件名」，点「$($开[0].Current.Name)」"
  $c = 中心 $开[0]
  [V25]::Move($停车点[0], $停车点[1]); Start-Sleep -Milliseconds 300
  [V25]::Move($c[0], $c[1]); Start-Sleep -Milliseconds 80
  [V25]::Press()
  $期限 = (Get-Date).AddSeconds(20)
  while ((Get-Date) -lt $期限 -and (找原生框)) { Start-Sleep -Milliseconds 500 }
  Start-Sleep -Seconds 4
}

# 哪一首能弹**不能假设**：实测 Carulli_Duetto_No2_Op4.mid 载进去之后「开始演奏」是灰的、
# 提示行整条不显示 —— 那是空状态块顶上来了（「这首歌没有可演奏的单声部轨」）。
# 所以按候选表一首一首试，试到有一首「开始」能按为止。这一步只是为了拿到一个能按的开始键，
# 与这一票要验的东西无关；试不动的那几首把窗口里的话原样打出来，省得回头猜。
$候补 = @("$曲名.mid", '65tmmntw.mid', 'Hymn-Nr-05.mid', 'pilgrim.mid')
$载入的 = $null
$成功文件 = $null
$提示文 = '（还没载入）'
foreach ($f in $候补) {
  开一首 $f
  $根二 = $AE::FromHandle($演奏器)
  $曲值 = 某根编号 $根二 'SongValue'
  $曲名读到 = if ($曲值.Count) { $曲值[0].Current.Name } else { '（没有）' }
  $轨提示 = 某根编号 $根二 'TrackHint'
  $提示文 = if ($轨提示.Count) { $轨提示[0].Current.Name } else { '（没有提示行）' }
  $开2 = 某根编号 $根二 'StartButton'
  $能按 = if ($开2.Count) { $开2[0].Current.IsEnabled } else { $false }
  Write-Host "  载入「$曲名读到」：提示行「$提示文」，开始可按=$能按"
  if ($能按) { $载入的 = $曲名读到; $成功文件 = $f; break }
  $看见 = (某根里 $根二 $CT::Text | ForEach-Object { $_.Current.Name }) -join ' | '
  Write-Host "    → 这一首按不动，窗口里现在写着：$看见"
}
断言真 '至少载进来一首能弹的（不然下面那条「按开始」根本没得按）' ($null -ne $载入的) "最后载入的是「$载入的」"
if ($成功文件) {
  $应读 = $成功文件.Substring(0, $成功文件.Length - 4)
  断言真 '载进来的是**点的那一首**（不是列表里别的那一行）' ($载入的 -eq $应读) "点了「$成功文件」，曲目那一行写的是「$载入的」"
}
断言真 '载入之后提示行报出了轨数与可弹数' ($提示文 -like '只列出单声部轨 · *') "「$提示文」"

$根二 = $AE::FromHandle($演奏器)
$开始 = 某根编号 $根二 'StartButton'
if ($开始.Count -ne 1 -or -not $开始[0].Current.IsEnabled) {
  throw '「开始演奏」还是灰的 —— 上面那条断言已经红了，别让下面那句「没按动」看起来像「按了没反应」'
}
$前 = (别的顶层窗).Count
[void]$开始[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 3
$根二 = $AE::FromHandle($演奏器)
$状 = 某根编号 $根二 'StatusText'
$读到 = $状[0].Current.Name
Write-Host "  状态行现在写的是：`n    「$读到」"
断言真 '按下去停在权限那一关，状态行逐字就是那句中文' ($读到 -eq $预期) "读到「$读到」"
$后 = (别的顶层窗).Count
断言真 '预检不放行时没有多拉起悬浮层（不该多出置顶窗口）' ($后 -eq $前) "按之前 $前 个别的顶层窗，按之后 $后 个"
if ($后 -ne $前) { 查别的窗 '按之后' }

  $跑完了 = $true
}
catch {
  Write-Host "`n★ 脚本跑到一半抛了：$_"
  查别的窗 '抛的时候'
}
finally {
  # 这个脚本不写盘（曲库只读、MIDI 只读），所以没有 25 号那套还原。
  # 要收的只有 app 自己：连它一起开着的那个原生框、那个置顶的演奏器窗口。
  try {
    $落 = 别的顶层窗
    if ($落.Count) { Write-Host "`n收尾：还开着的别的顶层窗 $(($落 | ForEach-Object { [V25]::Describe($_) }) -join ' ;; ')" }
  } catch { }
}

"`n========== 结果 =========="
# 「跑到一半断了」和「全过」必须分得开：断言是 Write-Host 打的、收尾这几句是输出流，
# 脚本真要是在半路抛了，上面的 OK 只覆盖到断点为止 —— 25 号实测栽过一回。
if (-not $跑完了) {
  '★ 跑到一半断了：断点之后的每一条都没验过，这不是全过'
  $fail = $fail + 1
} elseif ($fail -eq 0) { '全过' } else { "$fail 条红" }
[void][V25]::ShowWindow($h, 9)
[void]$proc.CloseMainWindow()
if (-not $proc.WaitForExit(8000)) { $proc.Kill() }
exit $fail
