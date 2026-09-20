# 32 号工单的实机验证：曲库列表里的曲名**不能编辑**（鼠标点它、键盘按 F2，两条都不进编辑），
# 改名只剩顶栏「歌曲名」那一格，而且照样一路通到盘上。
#
# 用法: pwsh -NoProfile -File verify-32.ps1     （脚本自己起 app、自己收尾）
#
# 复用 25 号那份驱动的骨架（UIA 真点 + 前台/点上双重闸门，那两个坑的来龙去脉见 verify-23.ps1 抬头）。
# 这一票特有的三个量法：
#   · **「不能编辑」的判据是「行里没有输入框」**（ListItem 底下的 Edit 数 = 0），
#     不是「点了没反应」—— 后者说不清是「逻辑对」还是「点空了」，
#     而这一票要的恰恰是「那个框不再冒出来」。
#   · **改名走顶栏那一格**：它只认回车（没有 LostFocus 提交那条路）。
#     驱动它**只能用 UIA**：`曲名框.SetFocus()` 把焦点放进那一格，`ValuePattern.SetValue` 写字，
#     再发回车。**不要用合成鼠标点它**：32c/32f 两支探针反复量到「点进去之后字进得去
#     （甚至 Ctrl+A 都不生效、字插在光标处），可回车到不了 OnSongNameKeyDown」；
#     而 21 号工单给 JumpBox 用的那一路（UIA SetFocus + ValuePattern.SetValue + 回车）是通的。
#     为什么会这样没查到根上，但它跟这一票的对错无关 —— 见工单的「给下一个人的坑」。
#   · **改名真的动盘**：曲库就是 bin/Debug/net8.0/songs/ 下的一堆 .mproj，改名 = File.Move。
#     改完数文件名，最后**改名改回去**并断言内容哈希一字未动。

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
  /// 把窗口抬到**非 topmost 那一层的最上面**（前台和「谁在最上面」是两码事，见 verify-23 抬头）。
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
    Raise(h);
    SetFocus(h); System.Threading.Thread.Sleep(300);
    return GetForegroundWindow() == h;
  }
  /// 挪光标 / 按一下，拆成两步是为了能在中间「先停到别处去」—— 见 PowerShell 那边的 `点`。
  ///
  /// 为什么要先停到别处：**Avalonia 的悬浮提示是 app 自己的另一个顶层窗**，
  /// 它**不吃 hit test** —— `WindowFromPoint` 照报主窗、PID 闸门也照过，可只要它开着，
  /// 第一下点击就什么都不做。所以每次点之前先把光标挪到一块没有提示的空地上停够时间。
  public static void Move(int x, int y) { SetCursorPos(x, y); }
  public static void Press() {
    mouse_event(0x0002, 0,0,0, IntPtr.Zero); mouse_event(0x0004, 0,0,0, IntPtr.Zero);
    System.Threading.Thread.Sleep(330);
  }
  // 按一下再松开。**不用 SendKeys 发快捷键**：它把键投给「当前有焦点的控件」，
  // 而窗口级快捷键恰恰是「不管焦点在谁身上、窗口层先吃掉」（TextBox 除外）。
  public static void Key(byte vk) {
    keybd_event(vk, 0, 0, IntPtr.Zero);
    System.Threading.Thread.Sleep(50);
    keybd_event(vk, 0, 2, IntPtr.Zero);
    System.Threading.Thread.Sleep(200);
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

# 双击要自己来：V25::Press 中间睡 330 毫秒，两次拼起来超过系统双击间隔，那是两下单击不是双击。
Add-Type @'
using System; using System.Runtime.InteropServices;
public class D25 {
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
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
# **摆在工作区里**（26 号工单量出来的坑）：这台机器屏 3072x1920、工作区只有 1824 高
# （任务栏从 y=1824 起），窗口底边落到那底下的话，被压住的那一行悬停不出 ToolTip。
# 这一票要悬停曲库行的名字，所以底边必须留在工作区里。
$摆 = { param($摆X, $摆Y, $摆宽, $摆高)
  [void][V25]::SetWindowPos($h, [IntPtr]::Zero, $摆X, $摆Y, $摆宽, $摆高, 0x0004 -bor 0x0010)
  Start-Sleep -Milliseconds 700
  ($AE::FromHandle($h)).Current.BoundingRectangle
}
$win = & $摆 405 300 2360 1300
"窗口 $([int]$win.Width)x$([int]$win.Height) @ $([int]$win.X),$([int]$win.Y)（底边 $([int]($win.Y + $win.Height))，任务栏从 1824 起）"
foreach ($等 in 1..20) {
  $root = $AE::FromHandle($h)
  if (@($root.FindAll($TS::Descendants, (& $条件 $CT::Edit)) | Where-Object { $_.Current.AutomationId -eq 'SongNameBox' }).Count) { break }
  Start-Sleep -Milliseconds 500
}
Start-Sleep -Seconds 2
$root = $AE::FromHandle($h)

# 「停车点」= 窗口里那块没有悬浮提示的空地（卷帘区中部）：每次点之前先停这儿，
# 把上一下遗留下来的悬浮提示关掉。
$停车点 = @([int]($win.X + $win.Width * 0.6), [int]($win.Y + $win.Height * 0.45))

# ---------- 闸门：前台 + 点上（两个坑的来龙去脉见 verify-23.ps1 抬头）----------
function 净了([int]$横, [int]$纵) {
  if ($横 -lt 0) { return $true }
  if ([V25]::PidAt($横, $纵) -ne $脚本PID) { return $false }
  return ([V25]::At($横, $纵) -eq $h)
}
# app 自己开着的、**该收掉**的浮层：菜单弹出层（里面有 MenuItem）和原生对话框（类名 #32770）。
function 浮层([object[]]$别窗) {
  $要收 = @()
  foreach ($w in $别窗) {
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
function 点([int]$横, [int]$纵, [string]$谁) {
  清场 $横 $纵 $谁
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
function 按编号([string]$id) {
  @(@(找类型 $CT::Text) + @(找类型 $CT::Button) + @(找类型 $CT::Edit) + @(找类型 $CT::ComboBox)) |
    Where-Object { $_.Current.AutomationId -eq $id }
}
# 轨数用「折叠」按钮数：每条轨的头上都有一颗（和 verify-23 同一把尺子）
function 数轨 { @(找类型 $CT::Button | Where-Object { $_.Current.Name -eq '折叠' }).Count }
function 曲库行 { @(找类型 $CT::ListItem) }
function 行里的文字($行) { @($行.FindAll($TS::Descendants, (& $条件 $CT::Text))) }
# **这一票的主尺子**：行里有没有输入框。曲名「能不能编辑」在 UIA 里就是这一件事 ——
# 编辑框在的时候它是个 Edit，不在的时候那一格是个 Text。
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
function 选中了($行) {
  try { $行.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected }
  catch { $false }
}
# 行里「不是名字文字」的那一格：右边那格小字（'没动过' / '改过' / '读不出来'）。
# 它没有 ToolTip、也不是按钮，是行里最干净的落点（点它只会选中这一行）。
function 小字($行) { (行里的文字 $行)[1] }
function 曲名格($行) { (行里的文字 $行)[0] }

# 顶栏那格「歌曲名」改名 —— 32 号之后**唯一**的改名入口。
#
# 这一格只认回车（没有 LostFocus 提交那条路），所以驱动要拆成三步：
#   ① UIA `SetFocus()` —— 把键盘焦点真的放进这一格（`ValuePattern` 只改文本、不给焦点）
#   ② `ValuePattern.SetValue` —— 写字。**不用 SendKeys 打字**：那一串字符里但凡有 `+ ^ % ( )`，
#      SendKeys 就把它们当控制符；而且这里要的是「框里是这串字」，不是「逐字敲进去」
#   ③ 回车**不走 `按`**：`按` 开头的清场会 Take(h) + SetFocus(h)，把焦点从这一格抢回窗口，
#      那样回车就没人接了（25 号 §6 的 F2 栽过同一个坑）。
#
# 为什么不真点进去：32c/32f 量过，鼠标点进去之后这一格的字进得去、回车却到不了
# `OnSongNameKeyDown`（连 Ctrl+A 都不生效）。同理**不要**用没有闸门的脚本去点它 ——
# 别的窗口盖在 app 上面时，那一下会点到别人身上，后面的 SendKeys 就进了别人的编辑器。
function 顶栏改名为([string]$到) {
  $框 = 曲名框
  if (-not $框) { throw '找不到曲名框' }
  if (-not $框.Current.IsEnabled) { throw '曲名框是灰的（没有开着的曲子），改不了名' }
  要前台 '顶栏改名（把焦点放进那一格）'
  [void]$框.SetFocus()
  Start-Sleep -Milliseconds 300
  设值 $框 $到
  Start-Sleep -Milliseconds 300
  [V25]::Key(0x0D)
  Start-Sleep -Milliseconds 1500
}

$曲库目录 = (Resolve-Path (Join-Path $PSScriptRoot '..\MidiPerformer.App\bin\Debug\net8.0\songs')).Path
$原名 = 'Carulli_Duetto_No2_Op4'
$临时 = 'Carulli_Verify_Tmp'

# 盘上那几份先整份备份。改名本来是可逆的，但**万一脚本中途炸了**，
# 用户的曲库不该留下乱七八糟的名字 —— 这不是脚本自己的临时目录，是人家在用的东西。
$备份目录 = Join-Path $env:TEMP ('verify32-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $备份目录 | Out-Null
$开工前 = 盘上
foreach ($n in $开工前) { Copy-Item (Join-Path $曲库目录 $n) (Join-Path $备份目录 $n) -Force }
$开工前哈希 = @{}
foreach ($n in $开工前) { $开工前哈希[$n] = 哈希 $n }
"曲库目录 $曲库目录"
"开工前盘上：$($开工前 -join ' / ')"

$跑完了 = $false

try {

# =====================================================================
"`n=== 1. 从曲库点开一首（4 条轨）==="
# =====================================================================
$行 = 找行 $原名
if (-not $行) { throw "曲库里没有「$原名」" }
# 下面那一下 SendKeys('{ENTER}') 投给的是**当前有焦点的窗口**，所以先确认前台是 app ——
# 别的窗口盖在上面时，这一下会往别人的编辑器里敲一个回车（32 号实测栽过）。
要前台 "点开「$原名」"
[void]$行.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 400
[void]$行.SetFocus()
Start-Sleep -Milliseconds 400
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
Start-Sleep -Seconds 4
断言真 '曲库点开之后真的载进来了（4 条轨）' ((数轨) -eq 4) "$(数轨) 条轨"

# =====================================================================
"`n=== 2. 行里根本没有输入框（常驻的那个没了）==="
# =====================================================================
$行内框 = @(曲库行 | ForEach-Object { 行里的框 $_ })
断言真 '曲库行里一个常驻输入框都没有' ($行内框.Count -eq 0) "数到 $($行内框.Count) 个"
断言真 '整个列表里也没有任何 Edit（改名框连影都没有）' ((找类型 $CT::Edit | Where-Object { $_.Current.AutomationId -notin @('SongNameBox','BpmBox','JumpBox','NameBox') }).Count -eq 0) "除顶栏那几格之外还有 $((找类型 $CT::Edit | Where-Object { $_.Current.AutomationId -notin @('SongNameBox','BpmBox','JumpBox','NameBox') }).Count) 个 Edit"
断言真 '曲名那一格还是**文字**（不是输入框假扮的）' ((曲名格 (找行 $原名)).Current.ControlType -eq $CT::Text) "读到 $((曲名格 (找行 $原名)).Current.ControlType.ProgrammaticName -replace 'ControlType\.','')"

# =====================================================================
"`n=== 3. 点曲名不进编辑（这一票的正面判据）==="
# =====================================================================
$行 = 找行 $原名
$c = 中心 (曲名格 $行)
点 $c[0] $c[1] "点曲名「$原名」（该只是选中这一行）"
Start-Sleep -Milliseconds 700
$行 = 找行 $原名
if (-not $行) { throw '点完那一行不见了 —— 名字那格的点击把行拆了？' }
断言真 '点曲名之后行里**没有**冒出输入框' ((行里的框 $行).Count -eq 0) "行里的框 $((行里的框 $行).Count) 个"
断言真 '点曲名把这一行选中了（点击没被吃掉）' (选中了 $行) "选中=$(选中了 $行)"
断言真 '点曲名之后名字那一格仍然是文字' ((曲名格 $行).Current.ControlType -eq $CT::Text) "读到 $((曲名格 $行).Current.ControlType.ProgrammaticName -replace 'ControlType\.','')"
断言真 '点曲名没有把歌打开（曲名框还是原来那首）' ((取值 (曲名框)) -eq $原名) "曲名框='$(取值 (曲名框))'"

# =====================================================================
"`n=== 4. F2 也不进编辑（键盘那条一起拆了）==="
# =====================================================================
# F2 从前挂在 ListBox 上，所以先得像用户那样把列表点活：点右边那格小字选中这一行。
# 落点挑小字而不是行内边距是有实测依据的（见 25 号 §6）：ListBoxItem 的 UIA 矩形比它能接
# 点击的范围大一圈，贴边点是**死区**，而那一格小字是实打实的元素。
$行 = 找行 $原名
$c = 中心 (小字 $行)
点 $c[0] $c[1] "点「$原名」那一行的小字（先把列表点活）"
Start-Sleep -Milliseconds 500
$行 = 找行 $原名
断言真 '点小字把这一行选中了（F2 才有落点）' (选中了 $行) "选中=$(选中了 $行)"
# F2 这**一下**不走 `按`：清场里的 SetFocus(h) 会把键盘焦点从刚点上的 ListBoxItem
# 挪回窗口，而 F2 恰恰挂在 ListBox 上 —— 抢完再发就没人接了（25 号实测栽过一回）。
if ([V25]::GetForegroundWindow() -ne $h) { 清场 -1 -1 'F2' }
[V25]::Key(0x71)
Start-Sleep -Milliseconds 900
$行 = 找行 $原名
断言真 'F2 之后行里**也没有**输入框（改名那条键盘路拆干净了）' ((行里的框 $行).Count -eq 0) "行里的框 $((行里的框 $行).Count) 个"
断言真 'F2 之后这一行还是原来那个名字' ((曲名格 $行).Current.Name -eq $原名) "'$(行名 $行)'"

# =====================================================================
"`n=== 5. 悬浮曲名仍然看得见完整名字（ToolTip 没跟着拆）==="
# =====================================================================
foreach ($名 in @('cargo', $原名)) {
  $此 = 找行 $名
  if (-not $此) { throw "找不到「$名」那一行" }
  $格 = 曲名格 $此
  $c = 中心 $格
  清场 $c[0] $c[1] "悬停「$名」那一行的曲名"
  [V25]::Move($停车点[0], $停车点[1]); Start-Sleep -Milliseconds 500
  [V25]::Move($c[0], $c[1]); Start-Sleep -Milliseconds 1200
  断言真 "曲库行「$名」悬浮显示完整曲名" ($格.Current.HelpText -eq $名) "帮助='$($格.Current.HelpText)'"
  [V25]::Move($停车点[0], $停车点[1]); Start-Sleep -Milliseconds 300
}
断言真 '那个长名字确实是长的（上一条断言才有意义）' ($原名.Length -ge 16) "$($原名.Length) 个字符"

# =====================================================================
"`n=== 6. 双击曲名能打开这一首（从前第一下就进改名、被拦着）==="
# =====================================================================
$行 = 找行 'cargo'
if (-not $行) { throw '曲库里没有 cargo 这一首' }
$c = 中心 (曲名格 $行)
双击 $c[0] $c[1] '双击「cargo」那一行的曲名（该打开这一首）'
Start-Sleep -Seconds 5
if ((取值 (曲名框)) -ne 'cargo') {
  Write-Host "    ★ 双击曲名没打开。点上读到的是 $([V25]::Describe([V25]::At($c[0], $c[1])))；曲名框还是 '$(取值 (曲名框))'"
  $行 = 找行 'cargo'
  $c = 中心 (曲名格 $行)
  双击 $c[0] $c[1] '再双击一次「cargo」那一行的曲名'
  Start-Sleep -Seconds 5
  Write-Host "    → 补双击一次：曲名框='$(取值 (曲名框))' 轨数=$(数轨)"
}
断言真 '双击曲名真的把那一首打开了（曲名框变成 cargo）' ((取值 (曲名框)) -eq 'cargo') "曲名框='$(取值 (曲名框))'；轨数 $(数轨)"
断言真 '右上角跳到 cargo 之后，行里照样没有输入框' ((行里的框 (找行 'cargo')).Count -eq 0) "行里的框 $((行里的框 (找行 'cargo')).Count) 个"

# 把原来那首开回来：改名那一节要改的是**当前开着的那首**（顶栏那一格只认它）
$行 = 找行 $原名
要前台 '把 Carulli 开回来'
[void]$行.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 400
[void]$行.SetFocus()
Start-Sleep -Milliseconds 400
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
Start-Sleep -Seconds 5
断言真 '把 Carulli 开回来，曲名框回到原名' ((取值 (曲名框)) -eq $原名) "曲名框='$(取值 (曲名框))'；轨数 $(数轨)"

# =====================================================================
"`n=== 7. 顶栏那一格改名照样一路通到盘上（改出去、再改回来）==="
# =====================================================================
$原哈希 = 哈希 "$原名.mproj"
顶栏改名为 $临时
断言真 "盘上真的出现了「$临时.mproj」" (Test-Path (Join-Path $曲库目录 "$临时.mproj")) "盘上现在：$((盘上) -join ' / ')"
断言真 "盘上原来那份「$原名.mproj」没了" (-not (Test-Path (Join-Path $曲库目录 "$原名.mproj"))) "盘上现在：$((盘上) -join ' / ')"
断言真 '窗口喊了一句「改成了」' ((文本 '改成了').Count -ge 1) "数到 $((文本 '改成了').Count) 条"
断言真 '曲名框跟着换成新名字' ((取值 (曲名框)) -eq $临时) "曲名框='$(取值 (曲名框))'"
$行新 = 找行 $临时
断言真 '列表里刷出了新名字那一行（改名之后列表跟着刷新）' ($行新 -ne $null) "现在有：$((曲库行 | ForEach-Object { 行名 $_ }) -join ' / ')"
断言真 '新名字那一行里也没有输入框' ($行新 -ne $null -and (行里的框 $行新).Count -eq 0) "行里的框 $(if($行新){(行里的框 $行新).Count}else{'行没了'}) 个"
断言真 '改名动的是文件名，不是又抄了一份（文件个数没变）' ((盘上).Count -eq $开工前.Count) "$((盘上).Count) 个（开工前 $($开工前.Count) 个）"

顶栏改名为 $原名
断言真 '再改回来，盘上文件名回到原样' ((盘上) -contains "$原名.mproj") "盘上现在：$((盘上) -join ' / ')"
断言真 '改回来之后**内容一个字节都没动**（改名只动文件名）' ((哈希 "$原名.mproj") -eq $原哈希) "$(哈希 "$原名.mproj") 对 $原哈希"
断言真 '曲名框回到原名' ((取值 (曲名框)) -eq $原名) "曲名框='$(取值 (曲名框))'"

# =====================================================================
"`n=== 8. 顶栏那一格的两条老判据没退化：空名字挡住、非法字符消毒 ==="
# =====================================================================
$这次前 = 盘上
顶栏改名为 ''
$错 = 文本 '不能当曲名'
断言真 '空名字被挡下来了：窗口冒出那句现成的中文' ($错.Count -ge 1) "数到 $($错.Count) 条；窗口里的错字是「$(if($错.Count){$错[0].Current.Name})」"
断言真 '空名字下盘上一个文件都没动' (((盘上) -join '|') -eq ($这次前 -join '|')) "现在：$((盘上) -join ' / ')"
断言真 '空名字之后曲名框退回原名（不是留着一个空框）' ((取值 (曲名框)) -eq $原名) "曲名框='$(取值 (曲名框))'"

# `Carulli/Tmp:Bad` 里的 / 和 : 是 Windows 文件名里不能用的（SongLibrary.InvalidNameChars）。
# 工单要的是「不产生坏文件名」—— 判据是盘上没有带 / 或 : 的名字，而不是「它一定被拦下来」。
顶栏改名为 'Carulli/Tmp:Bad'
$带坏字符 = @(盘上 | Where-Object { $_ -match '[/:\\:*?"<>|]' })
断言真 '名字里的 / 和 : 一个都没落到文件名上' ($带坏字符.Count -eq 0) "带坏字符的有：$($带坏字符 -join ' / ')"
断言真 '消毒之后的名字落在盘上（CarulliTmpBad.mproj）' ((盘上) -contains 'CarulliTmpBad.mproj') "盘上现在：$((盘上) -join ' / ')"
顶栏改名为 $原名
断言真 '消毒那一轮过完，名字回到原名' ((盘上) -contains "$原名.mproj") "盘上现在：$((盘上) -join ' / ')"
断言真 '这一轮下来内容依然一个字节都没动' ((哈希 "$原名.mproj") -eq $原哈希) "$(哈希 "$原名.mproj") 对 $原哈希"

  $跑完了 = $true
}
catch {
  Write-Host "`n★ 脚本跑到一半抛了：$_"
}
finally {
  # 无条件还原曲库：先把盘上所有 .mproj 清掉，再把开工前备份的整份拷回来，逐字节对一遍。
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
if (-not $跑完了) {
  '★ 跑到一半断了：断点之后的每一条都没验过，这不是全过'
  $fail = $fail + 1
} elseif ($fail -eq 0) { '全过' } else { "$fail 条红" }
[void][V25]::ShowWindow($h, 9)
[void]$proc.CloseMainWindow()
if (-not $proc.WaitForExit(8000)) { $proc.Kill() }
exit $fail
