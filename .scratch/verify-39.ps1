# 39 号工单的实机验证：**整曲长度只按没折叠的轨算**。
#
# 用法: pwsh -NoProfile -File verify-39.ps1     （脚本自己起 app、自己收尾）
#
# 用户 2026-09-20 的原话：
#   「例如共十小节 减掉末尾两个小节 我希望是直接消失 而不是空着小节继续播放
#     如果还有其他音轨（无折叠）就继续播放」
# 以及他对口径的选择：**整曲长度只算没折叠的轨**（另一选项是「所有轨」）。
#
# 屏幕上的唯一证人 = 走带条那一格「位置 N / M 小节」里的 **M**
#（`MainWindow.RefreshView` 里那句 `Format.Position(BarOfTick(playhead), controller.BarCount)`）。
# 主窗上没有时长读数（进度条在演奏器窗里），所以 M 就是这一票在全程序里唯一看得见的那一面。
#
# 曲子用 Carulli（4 条轨，3/4 拍、PPQ 480 ⇒ 一小节 1440 tick）。
# 各轨末尾是**从曲库工程里量出来的**（不是猜的）：
#   轨1 guitare 178080 → 124 小节    轨2 guitare  44640 → 31 小节
#   轨3 violon  178080 → 124 小节    轨4 violon   49680 → 35 小节
# 于是每一节都挑得出一个**能区分两种错法**的数：
#   · 折叠轨2/轨4（短的）→ M 一动都不该动（挡住「按下标第一条算」= 124、也挡住「按最短那条算」= 31）
#   · 折叠轨1（长的之一）→ M 还是 124（轨3 那条长的还在）
#   · 折叠轨1+轨3 → M 必须**恰好 35**（= 剩下最长的轨4；按最短那条算会是 31、按整份谱面算是 124）
#   · 四条全折叠 → M 回到 124（兜底退回整份谱面；实现成「曲子空了」会是 1）
#
# 前提：**非提权**（照抄 36 号：本脚本不点「开始演奏」，但同样不许提权跑）。

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @"
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public class V39 {
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
  public static string Describe(IntPtr h) {
    if (h == IntPtr.Zero) return "（空）";
    var c = new StringBuilder(256); GetClassName(h, c, 256);
    var t = new StringBuilder(256); GetWindowText(h, t, 256);
    uint pid; GetWindowThreadProcessIdOut(h, out pid);
    return "句柄 " + h + " 类名'" + c + "' 标题'" + t + "' PID " + pid;
  }
  [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
  public static extern uint GetWindowThreadProcessIdOut(IntPtr h, out uint pid);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public static void Raise(IntPtr h) {
    SetWindowPos(h, new IntPtr(-1), 0,0,0,0, 0x0001|0x0002|0x0010);
    SetWindowPos(h, new IntPtr(-2), 0,0,0,0, 0x0001|0x0002|0x0010);
  }
  public static IntPtr At(int x, int y) { POINT p; p.X = x; p.Y = y; return WindowFromPoint(p); }
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
function 断言([string]$名字, $实际, $期望) {
  if ("$实际" -eq "$期望") { "  OK   $名字 = $实际" }
  else { "  FAIL $名字 = $实际（期望 $期望）"; $script:fail++ }
}
# **逐字**比对。PowerShell 的 `-eq` 对字符串不区分大小写，比文案得用 `-ceq`。
function 断言字([string]$名字, [string]$实际, [string]$期望) {
  if ($实际 -ceq $期望) { "  OK   $名字（逐字相同）" }
  else { "  FAIL $名字 读到「$实际」，期望「$期望」"; $script:fail++ }
}

# ---------- 起一个干净实例 ----------
$exe = Join-Path $PSScriptRoot '..\MidiPerformer.App\bin\Debug\net8.0\MidiPerformer.exe'
if (-not (Test-Path $exe)) { throw "没找到 $exe —— 先编译" }
Get-Process -Name MidiPerformer -EA SilentlyContinue | ForEach-Object {
  [void][V39]::ShowWindow($_.MainWindowHandle, 9)
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

if (-not [V39]::Take($h)) { throw '拽不到前台' }
$摆 = { param($摆X, $摆Y, $摆宽, $摆高)
  [void][V39]::SetWindowPos($h, [IntPtr]::Zero, $摆X, $摆Y, $摆宽, $摆高, 0x0004 -bor 0x0010)
  Start-Sleep -Milliseconds 700
  ($AE::FromHandle($h)).Current.BoundingRectangle
}
# 摆进**工作区里**（26 号量出来的坑：这台机器任务栏从 y=1824 起）。
# 这一票要**四条轨的折叠开关都能点到**：这台机器 3072x1920、任务栏从 1824 起，
# 照 36 号那样摆 2360x1480 的话第 4 条轨的开关落在窗口底边**外面**（量到 y=1893，窗口底 1780），
# 点下去是压在别的窗口上。所以这里直接开满工作区，够不着的那颗再用 ScrollIntoView 滚进来（见 `折叠按钮`）。
$wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
"屏幕 3072x1920、工作区 $($wa.Width)x$($wa.Height)（任务栏从 1824 起）"
$win = & $摆 $wa.X $wa.Y $wa.Width $wa.Height
"窗口 $([int]$win.Width)x$([int]$win.Height) @ $([int]$win.X),$([int]$win.Y)（底边 $([int]($win.Y + $win.Height))）"

foreach ($等 in 1..20) {
  $root = $AE::FromHandle($h)
  if (@($root.FindAll($TS::Descendants, (& $条件 $CT::Edit)) | Where-Object { $_.Current.AutomationId -eq 'SongNameBox' }).Count) { break }
  Start-Sleep -Milliseconds 500
}
Start-Sleep -Seconds 2
$root = $AE::FromHandle($h)
$停车点 = @([int]($win.X + $win.Width * 0.6), [int]($win.Y + $win.Height * 0.45))

# ---------- 取数 ----------
function 找类型([object]$类型) { @($root.FindAll($TS::Descendants, (& $条件 $类型))) }
function 按钮([string]$名) { @(找类型 $CT::Button | Where-Object { $_.Current.Name -eq $名 }) }
function 按编号([string]$id) {
  @(@(找类型 $CT::Text) + @(找类型 $CT::Button) + @(找类型 $CT::Edit) + @(找类型 $CT::Pane) +
    @(找类型 $CT::Custom) + @(找类型 $CT::ListItem)) |
    Where-Object { $_.Current.AutomationId -eq $id }
}
function 找行([string]$曲名) {
  @(找类型 $CT::ListItem | Where-Object {
    $t = @($_.FindAll($TS::Descendants, (& $条件 $CT::Text)))
    $t.Count -gt 0 -and $t[0].Current.Name -eq $曲名 }) | Select-Object -First 1
}
function 取值($e) { $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
function 数轨 { @(找类型 $CT::Button | Where-Object { $_.Current.Name -eq '折叠' -or $_.Current.Name -eq '展开' }).Count }
function 焦点是谁 {
  $f = [System.Windows.Automation.AutomationElement]::FocusedElement
  if (-not $f) { return '（没有焦点元素）' }
  "$($f.Current.ControlType.ProgrammaticName) id='$($f.Current.AutomationId)' 名='$($f.Current.Name)'"
}
function 焦点是输入框 { $f = [System.Windows.Automation.AutomationElement]::FocusedElement; $f -and $f.Current.ControlType -eq $CT::Edit }

# 「位置」那一格：先找「位置」标签，再取它右边紧挨着那格值（照抄 verify-20/33/34，不写死 Y）。
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
function 位置N { $m = [regex]::Match((位置原文), '^\s*(\d+)'); if ($m.Success) { return [int]$m.Groups[1].Value } return -1 }
function 位置M { $m = [regex]::Match((位置原文), '(\d+)\s*小节'); if ($m.Success) { return [int]$m.Groups[1].Value } return -1 }

# 第 $序 条轨的折叠开关。**一个按钮两句话**（收着的时候写「展开」），所以两个名字都算数；
# 轨序按 Y 排 —— 和 23/26/36 同一把尺子（轨号文字会和读数条那一格撞名，不能当锚）。
function 折叠按钮([int]$序) {
  $bs = @(找类型 $CT::Button | Where-Object { $_.Current.Name -eq '折叠' -or $_.Current.Name -eq '展开' } |
          Sort-Object { $_.Current.BoundingRectangle.Y })
  if ($序 -lt 1 -or $序 -gt $bs.Count) { throw "第 $序 条轨的折叠开关不在场（共 $($bs.Count) 个）" }
  $bs[$序 - 1]
}
function 开关字([int]$序) { (折叠按钮 $序).Current.Name }
# 把开关滚进视野再点。（卷帘那一片是 `LanesScroll`；轨高了以后第 4 条的开关会落在窗口外面 ——
# 探针里量到 y=1893、窗口底 1811，点下去压的是别的窗口。ScrollItemPattern 一叫就回来了。）
# 注意：滚动**会挪动别的开关**（滚完第 1 条的开关跑到 -209），所以每次点之前都要重新滚、重新取矩形。
function 露面([int]$序, [string]$谁) {
  $b = 折叠按钮 $序
  try { $b.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView()
        Start-Sleep -Milliseconds 500 } catch { }
  $r = $b.Current.BoundingRectangle
  if ($r.Y -lt $win.Y -or ($r.Y + $r.Height) -gt ($win.Y + $win.Height) -or
      $r.X -lt $win.X -or ($r.X + $r.Width) -gt ($win.X + $win.Width)) {
    throw "第 $序 条轨的开关滚进来之后还在窗口外面（$([int]$r.X),$([int]$r.Y)，窗口 y $([int]$win.Y)..$([int]($win.Y + $win.Height))）——「$谁」没法点"
  }
  $r
}
function 折([int]$序, [string]$为什么) {
  $字 = 开关字 $序
  "  · 第 $序 条轨的开关现在是「$字」——$为什么"
  $r = 露面 $序 "第 $序 条轨的「$字」"
  点 ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2)) "第 $序 条轨的「$字」"
  Start-Sleep -Milliseconds 600
  $期望 = if ($字 -eq '折叠') { '展开' } else { '折叠' }
  断言 "第 $序 条轨的开关翻面了（点之前是「$字」）" (开关字 $序) $期望
}

# ---------- 清场 / 前台 / 点 ----------
function 浮层([object[]]$别窗) {
  $要收 = @()
  foreach ($w in $别窗) {
    $缓冲 = New-Object System.Text.StringBuilder 256
    [void][V39]::GetClassName($w, $缓冲, 256)
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
    [void][V39]::Take($h)
    if ([V39]::GetForegroundWindow() -eq $h) {
      Start-Sleep -Milliseconds 250
      $中横 = [int]($win.X + $win.Width / 2); $中纵 = [int]($win.Y + $win.Height / 2)
      if ([V39]::GetForegroundWindow() -eq $h -and [V39]::PidAt($中横, $中纵) -eq $脚本PID) { return }
      Write-Host "    [「$谁」前台是它了，但窗口正中压着 $([V39]::Describe([V39]::At($中横, $中纵))) —— 再抬一次]"
    }
    Start-Sleep -Milliseconds 500
  }
  throw "「$谁」之前没能让 app 既在前台、又没被压住（前台是 $([V39]::Describe([V39]::GetForegroundWindow()))）"
}
function 清场([int]$横, [int]$纵, [string]$谁) {
  for ($i = 1; $i -le 6; $i++) {
    要前台 $谁
    [V39]::Move($停车点[0], $停车点[1]); Start-Sleep -Milliseconds 500
    if ([V39]::PidAt($横, $纵) -eq $脚本PID) {
      $要收 = 浮层 @([V39]::Others($脚本PID, $h))
      if ($要收.Count -eq 0) { return }
      Write-Host "    ↺「$谁」之前 app 还开着 $($要收.Count) 个浮层 —— 再挪一次光标"
    } else {
      Write-Host "    ↺「$谁」之前 $横,$纵 上压着 $([V39]::Describe([V39]::At($横, $纵)))（第 $i 次）"
    }
    Start-Sleep -Milliseconds 400
  }
  throw "「$谁」之前清不干净：$横,$纵 上还压着 $([V39]::Describe([V39]::At($横, $纵)))"
}
function 点([int]$横, [int]$纵, [string]$谁) {
  清场 $横 $纵 $谁
  [V39]::Move($停车点[0], $停车点[1]); Start-Sleep -Milliseconds 500
  [V39]::Move($横, $纵); Start-Sleep -Milliseconds 60
  [V39]::Press()
}
function 确保前台([string]$谁) {
  if ([V39]::GetForegroundWindow() -eq $h) { return $true }
  "  ！！ 「$谁」之前前台掉了 —— 拽回来；"
  return [V39]::Take($h)
}

# 写值 + 回车提交（照抄 verify-34）。
function 回车提交($框, [string]$值) {
  [void](确保前台 '回车提交')
  [void]$框.SetFocus(); Start-Sleep -Milliseconds 300
  $框.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($值)
  Start-Sleep -Milliseconds 300
  [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
  Start-Sleep -Milliseconds 800
}

$跑完了 = $false
try {

# =====================================================================
"`n=== 0. 干净实例、还没载曲子：位置读数是破折号，屏幕上没有「N / M 小节」这样的字 ==="
# =====================================================================
# 一个都没载的时候 `RefreshView` 会在「还没有控制器」那一句返回，位置读数停在初始的「—」上。
断言字 '没载曲子时位置读数是破折号' (位置原文) '—'
断言 '没载曲子时 M 读不出来' (位置M) -1

# ---------- 载入 Carulli（4 条轨、长短不一）----------
$曲名 = 'Carulli_Duetto_No2_Op4'
$行 = 找行 $曲名
if (-not $行) { throw "曲库里没有「$曲名」" }
if (-not (确保前台 '载歌')) { throw '拽不到前台' }
[void]$行.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 400
[void]$行.SetFocus()
Start-Sleep -Milliseconds 400
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
Start-Sleep -Seconds 5

# =====================================================================
"`n=== 1. 载入之后：M = 124（整份谱面），4 条轨 ==="
# =====================================================================
"  位置读数 = 「$(位置原文)」"
断言 '载入 Carulli 之后有 4 条轨' (数轨) 4
断言 '载入之后 M = 整份谱面的 124 小节' (位置M) 124
# 先确认四颗开关都够得着（第 4 条轨的开关本来落在窗口外面，靠 ScrollIntoView 滚进来）——
# 不先验这一步的话，后面每节都会以「点不到」的样子红，看不出是脚本的锅还是功能的锅。
foreach ($序 in 1..4) {
  $r = 露面 $序 "预检"
  "  第 $序 条轨的开关滚进视野后 @ $([int]$r.X),$([int]$r.Y)"
}
断言真 '四条轨的开关都够得着' $true '（露面的越界检查没抛就是够得着）'

# =====================================================================
"`n=== 2. 折叠**短的**那两条（轨2 = 31 小节、轨4 = 35 小节）→ M 一动都不该动 ==="
# =====================================================================
# 这一节挡的是「按下标第一条算」和「按最短那条算」两种错法：两种都会让 M 掉下来。
$M前 = 位置M
折 2 '短的一条（末尾 31 小节）'
断言 '折叠轨2（短）之后 M 不变（它本来就不是决定长度的那个）' (位置M) $M前
折 4 '另一条短的（末尾 35 小节）'
断言 '折叠轨2+轨4（两条都短）之后 M 还是不变' (位置M) $M前
折 4 '展开轨4'
折 2 '展开轨2'
断言 '把两条短的都展开回来：M 仍是 124' (位置M) 124

# =====================================================================
"`n=== 3. 折叠**长的之一**（轨1，末尾 124 小节）→ M 还是 124（轨3 那条长的还在）==="
# =====================================================================
折 1 '长的一条（末尾 124 小节）'
断言 '折叠轨1 之后 M 还是 124（另一条长的还在）' (位置M) 124
折 1 '展开轨1'
断言 '展开轨1：M 仍是 124' (位置M) 124

# =====================================================================
"`n=== 4. 两条长的都折叠 → M 从 124 掉到 **35**（这一票的正题）==="
# =====================================================================
# 35 = 剩下最长的轨4（49680 tick ÷ 1440 = 34.5 → 35 小节）。
# 这个数**同时挡掉两种错法**：按最短那条算会是 31、按整份谱面算是 124。
折 1 '长的一条（末尾 124 小节）'
折 3 '另一条长的（末尾 124 小节）'
"  位置读数 = 「$(位置原文)」"
断言 '两条长的都折叠之后 M = 剩下最长的那条（35 小节）' (位置M) 35
断言真 'M 确实变小了（不是原地不动）' ((位置M) -lt 124) "124 → $(位置M)"

# =====================================================================
"`n=== 5. 四条全折叠 → M **回到 124**（兜底退回整份谱面，不是「曲子空了」）==="
# =====================================================================
折 2 '把第三条也收起来'
折 4 '把第四条也收起来（现在四条全收着）'
"  位置读数 = 「$(位置原文)」"
断言 '四条全折叠：M 退回整份谱面 124（不是 1、也不是 0）' (位置M) 124

# =====================================================================
"`n=== 6. 全部展开 → M 回到 124 ==="
# =====================================================================
折 2 '展开轨2'
折 4 '展开轨4'
折 1 '展开轨1'
折 3 '展开轨3（四条全开着）'
断言 '四条全展开：M 回到 124' (位置M) 124

# =====================================================================
"`n=== 7. 播放头停在曲子里面，折叠之后被拉回**新曲尾** ==="
# =====================================================================
# 用户要的那句话的另一半：「直接消失」之后，播放头不能停在已经不存在的地方。
# 先跳到第 120 小节（`OnJumpKeyDown` 会把播放头也搬过去），再折叠两条长的 ——
# 曲子只剩 35 小节，位置读数必须落到「35 / 35」，不许停在 120 / 35。
$小节框 = (按编号 'JumpBox')[0]
if (-not $小节框) { throw '找不到 JumpBox' }
回车提交 $小节框 '120'
"  跳到 120 之后：位置 = 「$(位置原文)」"
断言 '跳小节把播放头搬到了 120' (位置N) 120

折 1 '长的一条（末尾 124 小节）'
折 3 '另一条长的（末尾 124 小节）'
"  折叠之后：位置 = 「$(位置原文)」"
断言 '折叠之后播放头被拉回新曲尾所在的 35 小节' (位置N) 35
断言字 '位置读数逐字就是「35 / 35 小节」' (位置原文) '35 / 35 小节'

折 1 '展开轨1'
折 3 '展开轨3'
断言 '展开回来：M 回到 124（播放头停在 35 上不动）' (位置M) 124

  $跑完了 = $true
}
catch {
  Write-Host "`n★ 脚本跑到一半抛了：$_"
}
finally {
  try {
    $落 = @([V39]::Others($脚本PID, $h))
    if ($落.Count) { Write-Host "`n收尾：还开着的别的顶层窗 $(($落 | ForEach-Object { [V39]::Describe($_) }) -join ' ;; ')" }
  } catch { }
}

"`n========== 结果 =========="
if (-not $跑完了) {
  '★ 跑到一半断了：断点之后的每一条都没验过，这不是全过'
  $fail = $fail + 1
} elseif ($fail -eq 0) { '全过' } else { "$fail 条红" }
[void][V39]::ShowWindow($h, 9)
[void]$proc.CloseMainWindow()
if (-not $proc.WaitForExit(8000)) { $proc.Kill() }
exit $fail
