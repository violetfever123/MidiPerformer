# 33 号工单的实机验证：走带条右边那颗 `■ 停止` 换成了 **`↻ 重头播放`**
# —— 按一下 = 播放头回开头 + 视野回第一小节 + **立刻开始放**。
#
# 用法: pwsh -NoProfile -File verify-33.ps1     （脚本自己起 app、自己收尾）
#
# 复用 32 号那份骨架（UIA 真找元素、前台 + 落点双重闸门，两个坑的来龙去脉见 verify-23.ps1 抬头）。
# 这一票特有的四处量法：
#   · **按钮字和 ToolTip 是两条独立的判据**：`Content` 走 UIA 的 `Name`（按钮上写什么），
#     `ToolTip.Tip` 走 `HelpText`。只测一条的话，「提示改了、按钮上的字没改」会溜过去。
#   · **按 ↻ 之后最要紧的一条是「它真的在放」**：位置回到 1 之后**再等几秒看它有没有往前爬**。
#     只测「位置=1」的话，一个「回开头然后停在那」的实现（或者干脆什么都没做的实现）
#     照样能过 —— 而那是这一票最可能做错的地方（忘了 `Play()`）。
#   · **↻ 的三种起点都要试**：正在播 / 暂停中 / 已放完。三条走的是同一个 `RestartPlayback`，
#     但进来的状态不同（`Play()` 里那句 <c>Seek(MusicNow)</c> 三种情况共用）——
#     只试一种的话，「暂停中按 ↻ 卡在『▶ 继续』上」这种错看不出来。
#   · **速度要改成 120**（借 verify-20 的第 0 节）：曲子原速 50 太慢 ——
#     实测这个速度下「等 3 秒看它有没有往前爬」几乎读不到变化，那会把假绿喂给上面第二条。
#     240 也不行：那时一拍只有 0.25 秒，按完 ↻ 到脚本读出位置的这点时间窗口里
#     播放头已经爬过一格了（第一次跑就是这么红的一条：读到 2、期望 1）——
#     那不是 app 错了，是**尺子太快**。120 下大约 1 格/秒，量「回到第 1 小节」才有分辨力。
#     速度不是这一票管得着的东西。
#   · **这支脚本一个空格都不往里打**（起播和暂停都走播放键的 Invoke）。空格是**窗口级**的键，
#     只有焦点不在输入框里才到得了窗口；而这支脚本每次 `跳小节` 之后焦点都落在某个 Edit 上
#     （`填框` 收尾那个 TAB 只是把焦点挪出原来那个框，完全可能挪进**另一个**框），
#     空格就被打进去了。第一次跑第 4 节就是这么红的：按钮还停在「⏸ 暂停」、
#     播放头从 4 爬到别处 —— 看着像「暂停坏了」，其实是那一下键根本没到。
#     播放键的 Invoke 和空格走的是**同一条 `TogglePlayback`**（verify-20 第 1/2/3 节
#     把空格和按钮量成一回事），所以换成 Invoke 之后覆盖一点没少，只是不再赌焦点在哪。
#
# 演奏器窗口那颗 `■ 急停`（另一个文件里的同名 `StopButton`）**不能在这里量**：
# 那要打开演奏器窗口（装全局钩子、走倒计时），代价和风险都不属于这一票。
# 它由两条**文件级**判据兜着（见第 6 节）：git 眼里那两个文件没被改过 + 文件文本里
# `Content="■ 急停"` 和两处 `StopButton.IsEnabled` 都还在。

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
Add-Type @"
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public class V33 {
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
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X,Y; }
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  // 同一个 API 要两种用法（丢弃 pid / 要 pid），而 `out` 是签名的一部分 —— 按 EntryPoint 再来一个。
  [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
  public static extern uint GetThreadPid(IntPtr h, out uint pid);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public static string Describe(IntPtr h) {
    if (h == IntPtr.Zero) return "（空）";
    var c = new StringBuilder(256); GetClassName(h, c, 256);
    var t = new StringBuilder(256); GetWindowText(h, t, 256);
    uint pid; GetThreadPid(h, out pid);
    RECT r; GetWindowRect(h, out r);
    return "句柄 " + h + " 类名'" + c + "' 标题'" + t + "' PID " + pid + " " + (r.R-r.L) + "x" + (r.B-r.T) + " @" + r.L + "," + r.T;
  }
  public static void Raise(IntPtr h) {
    SetWindowPos(h, new IntPtr(-1), 0,0,0,0, 0x0001|0x0002|0x0010);
    SetWindowPos(h, new IntPtr(-2), 0,0,0,0, 0x0001|0x0002|0x0010);
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
  public static IntPtr At(int x, int y) { POINT p; p.X=x; p.Y=y; return WindowFromPoint(p); }
  public static uint PidAt(int x, int y) { IntPtr w = At(x,y); if (w==IntPtr.Zero) return 0; uint pid; GetThreadPid(w, out pid); return pid; }
  public static void Move(int x, int y) { SetCursorPos(x, y); }
  public static void Press() {
    mouse_event(0x0002, 0,0,0, IntPtr.Zero); mouse_event(0x0004, 0,0,0, IntPtr.Zero);
    System.Threading.Thread.Sleep(330);
  }
  public static void Key(byte vk) {
    keybd_event(vk, 0, 0, IntPtr.Zero);
    System.Threading.Thread.Sleep(50);
    keybd_event(vk, 0, 2, IntPtr.Zero);
    System.Threading.Thread.Sleep(200);
  }
  /// app 进程还开着的、够大的可见顶层窗（除了主窗）。**带尺寸下限**，理由见 verify-32。
  public static List<IntPtr> Others(uint want, IntPtr skip) {
    var list = new List<IntPtr>();
    EnumWindows((h,l) => {
      uint pid; GetThreadPid(h, out pid);
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
function 断言([string]$名字, $实际, $期望) {
  if ("$实际" -eq "$期望") { "  OK   $名字 = $实际" }
  else { "  FAIL $名字 = $实际（期望 $期望）"; $script:fail++ }
}
function 断言真([string]$名字, [bool]$条件, [string]$原文) {
  if ($条件) { "  OK   $名字（$原文）" } else { "  FAIL $名字（$原文）"; $script:fail++ }
}

# ---------- 起一个干净实例 ----------
$根 = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $根 'MidiPerformer.App\bin\Debug\net8.0\MidiPerformer.exe'
if (-not (Test-Path $exe)) { throw "没找到 $exe —— 先编译" }
Get-Process -Name MidiPerformer -EA SilentlyContinue | ForEach-Object {
  [void][V33]::ShowWindow($_.MainWindowHandle, 9)
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

if (-not [V33]::Take($h)) { throw '拽不到前台' }
# 摆在**工作区里**（26 号量出来的坑：这台机器任务栏从 y=1824 起）。
[void][V33]::SetWindowPos($h, [IntPtr]::Zero, 405, 300, 2360, 1300, 0x0004 -bor 0x0010)
Start-Sleep -Milliseconds 900
$win = ($AE::FromHandle($h)).Current.BoundingRectangle
"窗口 $([int]$win.Width)x$([int]$win.Height) @ $([int]$win.X),$([int]$win.Y)"
$停车点 = @([int]($win.X + $win.Width * 0.6), [int]($win.Y + $win.Height * 0.45))

# ---------- 闸门（照抄 32 号）----------
function 净了([int]$横, [int]$纵) {
  if ($横 -lt 0) { return $true }
  if ([V33]::PidAt($横, $纵) -ne $脚本PID) { return $false }
  return ([V33]::At($横, $纵) -eq $h)
}
function 浮层([object[]]$别窗) {
  $要收 = @()
  foreach ($w in $别窗) {
    $缓冲 = New-Object System.Text.StringBuilder 256
    [void][V33]::GetClassName($w, $缓冲, 256)
    $类 = $缓冲.ToString()
    $是菜单 = $false
    try { $el = $AE::FromHandle($w); $是菜单 = @($el.FindAll($TS::Descendants, (& $条件 $CT::MenuItem))).Count -gt 0 } catch { }
    if ($是菜单 -or $类 -like '#32770*') { $要收 += $w }
  }
  , $要收
}
function 要前台([string]$谁) {
  for ($i = 1; $i -le 6; $i++) {
    [void][V33]::Take($h)
    if ([V33]::GetForegroundWindow() -eq $h) {
      Start-Sleep -Milliseconds 250
      $中横 = [int]($win.X + $win.Width / 2); $中纵 = [int]($win.Y + $win.Height / 2)
      if ([V33]::GetForegroundWindow() -eq $h -and [V33]::PidAt($中横, $中纵) -eq $脚本PID) { return }
      Write-Host "    [「$谁」前台是它了，但窗口正中压着 $([V33]::Describe([V33]::At($中横, $中纵))) —— 再抬一次]"
    }
    Start-Sleep -Milliseconds 500
  }
  throw "「$谁」之前没能让 app 既在前台、又没被压住（前台是 $([V33]::Describe([V33]::GetForegroundWindow()))）"
}
function 清场([int]$横, [int]$纵, [string]$谁) {
  for ($i = 1; $i -le 6; $i++) {
    要前台 $谁
    if (净了 $横 $纵) {
      $要收 = 浮层 @([V33]::Others($脚本PID, $h))
      if ($要收.Count -eq 0) { return }
      Write-Host "    ↺「$谁」之前先收一下：app 还开着 $($要收.Count) 个浮层（第 $i 次，Esc）"
      [V33]::Key(0x1B)
    } else {
      Write-Host "    ↺「$谁」之前先收一下：$横,$纵 上压着 $([V33]::Describe([V33]::At($横,$纵)))（第 $i 次）"
      [V33]::Move($停车点[0], $停车点[1]); Start-Sleep -Milliseconds 700
    }
    Start-Sleep -Milliseconds 400
  }
  throw "「$谁」之前清不干净：$横,$纵 上还压着 $([V33]::Describe([V33]::At($横, $纵)))"
}

# ---------- 取数 ----------
# **每次都从窗口句柄重新取根**：久了的元素会失效（控件树重画过一轮就认不出来了）。
function 找类型([object]$类型) { @(($AE::FromHandle($h)).FindAll($TS::Descendants, (& $条件 $类型))) }
function 文本([string]$含) { @(找类型 $CT::Text | Where-Object { $_.Current.Name -like "*$含*" }) }
function 按编号([string]$id) {
  @(@(找类型 $CT::Text) + @(找类型 $CT::Button) + @(找类型 $CT::Edit) + @(找类型 $CT::ComboBox)) |
    Where-Object { $_.Current.AutomationId -eq $id }
}
function 数轨 { @(找类型 $CT::Button | Where-Object { $_.Current.Name -eq '折叠' }).Count }
function 曲库行 { @(找类型 $CT::ListItem) }
function 行里的文字($行) { @($行.FindAll($TS::Descendants, (& $条件 $CT::Text))) }
function 找行([string]$曲名) {
  @(曲库行 | Where-Object {
    $t = 行里的文字 $_
    $t.Count -gt 0 -and $t[0].Current.Name -eq $曲名
  }) | Select-Object -First 1
}
function 取值($e) { $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
function 设值($e, [string]$v) { $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($v) }
function 播放键 { (按编号 'PlayButton')[0] }
function 重播键 { (按编号 'RestartButton')[0] }
function 按钮字 { (播放键).Current.Name }
function 重播字 { (重播键).Current.Name }
function 重播可用 { (重播键).Current.IsEnabled }

# 「位置」那一格：先找「位置」标签，再取它右边紧挨着那格值（照抄 verify-20，不写死 Y）
function 位置原文 {
  $t = 找类型 $CT::Text
  $lbl = $t | Where-Object { $_.Current.Name -eq '位置' } | Select-Object -First 1
  if (-not $lbl) { return '(找不到「位置」标签)' }
  $lr = $lbl.Current.BoundingRectangle
  $v = $t | Where-Object { $r = $_.Current.BoundingRectangle
      [Math]::Abs($r.Y - $lr.Y) -lt 14 -and $r.X -gt $lr.X -and $r.X -lt ($lr.X + 160) } |
    Sort-Object { $_.Current.BoundingRectangle.X } | Select-Object -First 1
  if ($v) { $v.Current.Name } else { '(读不到)' }
}
function 位置小节 {
  $m = [regex]::Match((位置原文), '^\s*(\d+)')
  if ($m.Success) { [int]$m.Groups[1].Value } else { -1 }
}
# 「回到开头」的判据：位置读数 **≤ 2**。
# 为什么不死抠 1：这一格是**跟着秒在走**的（120 的速度下约 1 格/秒），从 Invoke 到脚本读到值
# 中间隔着 0.18 秒 —— 读到 2 是尺子的分辨力，不是「没回开头」。没回开头的话读到的是
# 按之前那个数（30 / 36 / 124 那种），跟 2 差着一个数量级。
# 每一条这样的断言都在 `≥30 小节` 之后按的 ↻，所以「2」不可能是「本来就在 2」。
function 断言回到开头([string]$名字) {
  $p = 位置小节
  断言真 $名字 ($p -le 2) "位置 = $p（$(位置原文)）"
}

# 那颗播放键**真按下去**：UIA 的 InvokePattern（和鼠标点等价，但不依赖坐标和前台，
# 也就不会像合成鼠标那样点到别人窗口上 —— 32 号那个教训）。
# **它是切换**：正在放的时候按就是暂停（和空格同一条 `TogglePlayback`，见抬头最后一条）。
function 按播放 { 要前台 '按播放键'; (播放键).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 700 }
# **只等 180 毫秒**：按完 ↻ 它就立刻开始放了，「播放头在第 1 小节」这一条只有在这个窗口里
# 才读得到（120 的速度下大约 1 格/秒，180 毫秒 ≈ 0.2 格）。等久了读到 2 —— 那是尺子慢，
# 不是 app 错（第一次跑就是这么红的，见抬头）。
function 按重播 { 要前台 '按 ↻ 重头播放'; (重播键).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 180 }
function 回车 { [System.Windows.Forms.SendKeys]::SendWait('{ENTER}'); Start-Sleep -Milliseconds 600 }
# 走 UI 的那条路填框（借 verify-20）：SetFocus 把焦点真放进框里，ValuePattern 写字，
# 再发回车。**不用合成鼠标点这个框**（32 号量过：鼠标点进去之后回车到不了处理器）。
function 填框($框, [string]$值) {
  要前台 '填框'
  [void]$框.SetFocus(); Start-Sleep -Milliseconds 300
  $框.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($值)
  Start-Sleep -Milliseconds 300
  回车
  # 焦点挪出输入框：不挪的话后面每一步的空格都打进框里（而空格正是这一票要按的键之一）
  [System.Windows.Forms.SendKeys]::SendWait('{TAB}'); Start-Sleep -Milliseconds 300
}
# 轮到第 n 小节（先退回去再加，省得「值没变、回车没生效」看不出区别）
function 跳小节([int]$n) {
  填框 (按编号 'JumpBox')[0] ([string]($n - 1))
  填框 (按编号 'JumpBox')[0] ([string]$n)
}

$跑完了 = $false
try {

# =====================================================================
"`n=== 1. 没装曲子的时候：↻ 是灰的，■ 停止 连影都没有 ==="
# =====================================================================
# 判据的两半。**先量「灰」再看别的**：这一票把判据从 `_song is not null` 改成了
# `_song is { Tracks.Count: > 0 }`，而空窗口正是唯一能把它量出来的状态。
$重播 = 按编号 'RestartButton'
$停 = 按编号 'StopButton'
断言真 '↻ 重头播放 那颗按钮存在、且只有一个' ($重播.Count -eq 1) "找到 $($重播.Count) 个"
断言真 '走带条上没有 ■ 停止 了' ($停.Count -eq 0) "找到 $($停.Count) 个"
if ($重播.Count -eq 1) {
  断言 '没装曲子时 ↻ 上的字' ($重播[0].Current.Name) '↻ 重头播放'
  断言真 '没装曲子的时候 ↻ 是灰的（判据：有轨才放得响）' (-not $重播[0].Current.IsEnabled) '灰的'
  断言真 '↻ 的 ToolTip 是新那一句' ($重播[0].Current.HelpText -ceq '重头播放：播放头回开头、视野回第一小节，立刻开始放') "读到「$($重播[0].Current.HelpText)」"
}
断言真 '窗口里再也读不到「停下来（急停是 F6）」那句话' ((文本 '急停是 F6').Count -eq 0) "数到 $((文本 '急停是 F6').Count) 处"

# =====================================================================
"`n=== 2. 载入 Carulli：↻ 亮起来 ==="
# =====================================================================
$原名 = 'Carulli_Duetto_No2_Op4'
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
Start-Sleep -Seconds 5
断言真 '曲库点开之后真的载进来了（4 条轨）' ((数轨) -eq 4) "$(数轨) 条轨"
断言真 '装了曲子之后 ↻ 亮了' (重播可用) '亮的'
断言 '刚开始播放键上的字' (按钮字) '▶ 播放'
"  位置 = $(位置原文)"

# 速度 120：原速 50 太慢（「等 3 秒看它有没有往前爬」读不出变化），240 又太快（见抬头）。
填框 (按编号 'BpmBox')[0] '120'
"  速度改成 120 之后，位置 = $(位置原文)"

# =====================================================================
"`n=== 3. 正在播的时候按 ↻ = 从头重放 ==="
# =====================================================================
跳小节 30
$起点 = 位置小节
断言真 '跳到了第 30 小节（后面才看得出「回到开头」）' ($起点 -ge 30) "位置 = $起点"
按播放
断言 '按播放之后按钮变成' (按钮字) '⏸ 暂停'
Start-Sleep -Seconds 3
$播到 = 位置小节
断言真 '播放头在往前走' ($播到 -gt $起点) "从 $起点 走到 $播到"
按重播
断言 '按 ↻ 之后按钮仍然是' (按钮字) '⏸ 暂停'
断言 '按 ↻ 之后播放头回到开头' (位置小节) 1
Start-Sleep -Seconds 3
$重播后 = 位置小节
"  按 ↻ 之后等了 3 秒，位置 = $(位置原文)"
断言真 '↻ 之后确实在往前放（不是停在开头）' ($重播后 -gt 1) "从第 1 小节走到第 $重播后 小节"
断言真 '是从头重放，不是接着刚才那口气放' ($重播后 -lt $播到) "按之前在第 $播到 小节，现在才第 $重播后 小节"

# =====================================================================
"`n=== 4. 暂停中按 ↻ = 回开头 + 立刻重放（不是停在「继续」上）==="
# =====================================================================
按播放
断言 '先停在「继续」上' (按钮字) '▶ 继续'
$冻1 = 位置小节
Start-Sleep -Seconds 2
断言 '暂停期间播放头冻住了（两次读数一样）' (位置小节) $冻1
按重播
断言 '按 ↻ 之后按钮变成' (按钮字) '⏸ 暂停'
断言真 '按 ↻ 之后**不是**停在「继续」上（真的开始放了）' ((按钮字) -ne '▶ 继续') "按钮上写着「$(按钮字)」"
断言 '按 ↻ 之后播放头回到开头' (位置小节) 1
Start-Sleep -Seconds 3
$暂停后重播 = 位置小节
"  按 ↻ 之后等了 3 秒，位置 = $(位置原文)"
断言真 '暂停中按 ↻ 之后确实在往前放' ($暂停后重播 -gt 1) "从第 1 小节走到第 $暂停后重播 小节"

# =====================================================================
"`n=== 5. 放完之后按 ↻ = 从开头再放一遍 ==="
# =====================================================================
跳小节 120
"  跳到第 120 小节，位置 = $(位置原文)"
$等 = 0
while ((按钮字) -ne '▶ 播放' -and $等 -lt 40) { Start-Sleep -Milliseconds 500; $等++ }
"  等了 $([Math]::Round($等 * 0.5, 1)) 秒"
断言 '放完之后按钮回到' (按钮字) '▶ 播放'
断言 '放完之后位置停在曲尾' (位置小节) 124
按重播
断言 '放完再按 ↻：按钮变成' (按钮字) '⏸ 暂停'
断言 '放完再按 ↻：播放头回到开头' (位置小节) 1
Start-Sleep -Seconds 3
$尾后重播 = 位置小节
"  按 ↻ 之后等了 3 秒，位置 = $(位置原文)"
断言真 '放完再按 ↻：确实在往前放' ($尾后重播 -gt 1) "从第 1 小节走到第 $尾后重播 小节"

# =====================================================================
"`n=== 6. 演奏器窗口那颗 ■ 急停 没被顺手改掉 ==="
# =====================================================================
# 两条**文件级**判据（UI 那半要打开演奏器窗口，见抬头）。
$改过 = @(& git -C $根 status --porcelain -- MidiPerformer.App/Views/PerformerWindow.axaml MidiPerformer.App/Views/PerformerWindow.axaml.cs)
断言真 'git 眼里演奏器窗口那两个文件一个字都没动' ($改过.Count -eq 0) $(if ($改过.Count) { $改过 -join ' / ' } else { '（空）' })
$急停源 = Get-Content (Join-Path $根 'MidiPerformer.App\Views\PerformerWindow.axaml') -Raw
断言真 '演奏器窗口里那颗按钮还叫 StopButton、还写着 ■ 急停' `
  ($急停源 -match 'x:Name="StopButton"' -and $急停源 -match 'Content="■ 急停"') '两样都在'
$急停码 = Get-Content (Join-Path $根 'MidiPerformer.App\Views\PerformerWindow.axaml.cs') -Raw
$判据处 = [regex]::Matches($急停码, 'StopButton\.IsEnabled').Count
断言真 '它的可用性判据两处（初始灰 + 按运行态亮）都还在' ($判据处 -eq 2) "数到 $判据处 处"

$跑完了 = $true
}
catch { Write-Host "`n★ 脚本跑到一半抛了：$_" }
finally {
  [void][V33]::ShowWindow($h, 9)
  [void]$proc.CloseMainWindow()
  if (-not $proc.WaitForExit(8000)) { $proc.Kill() }
  Start-Sleep -Milliseconds 600
  "`n实例已收（PID $($proc.Id)）"
}

"`n========== 结果 =========="
if (-not $跑完了) { '★ 跑到一半断了：断点之后的每一条都没验过，这不是全过'; $fail = $fail + 1 }
elseif ($fail -eq 0) { '全过' } else { "$fail 条红" }
exit $fail
