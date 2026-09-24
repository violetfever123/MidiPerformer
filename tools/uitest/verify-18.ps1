# 18 号工单：Ctrl+←/→ 只在焦点轨里走 + 点音符焦点跟随。
#
# 判据走**读数条上「选中」那一格**，不靠像素。这一格的轨号就是「刚才那个音在哪条轨上」，
# 而这条工单改的正是「下一个音去哪条轨找」。
#
# 要把两半分开验，关键是**让焦点和选中集待在两条轨上**，再看 Ctrl+→ 往哪边倒：
#   · 先 Ctrl+↑ 把焦点推到轨 01，再去点轨 03 的音符
#   · 这时按 Ctrl+→，落在轨 03 = 点击把焦点带过去了；落在轨 01 = 没带过去
#   · 反过来把焦点推回轨 01（选中集仍在轨 03），Ctrl+→ 落在轨 01 = 导航跟的是焦点轨
#
# 几个踩出来的坑，写在前面省得下次重踩：
#   · **点击前要确认那个像素归 app**（WindowFromPoint）。台面上压着别的窗口时，
#     点下去石沉大海，而「没点着」和「点着了没反应」从读数条上看一模一样。
#   · 音符是 8px 高的细横杠，固定一个 y 横扫过去会从杠缝里穿过去 —— 所以拿
#     find-note.ps1 按像素找杠，别盲扫。
#   · 读数条的位置别写死：窗口里多一条提示条，整页就往下挪 76px。
#   · **每一步用到的坐标都要现取。** 换焦点会把它那条轨滚进视野，
#     开头记下的坐标过几步就不是那条轨的卷帘了 —— 点歪了看着却像「这段逻辑没生效」。
#   · **点之前重新拽一次前台。** 找音符要起子进程，它偶尔把前台抢走，
#     那一下点击就只剩「激活窗口」的作用（见 点）。
#
# 用法: pwsh -File verify-18.ps1   （app 要先开着、已经载入多轨曲子）
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices;
public class V18 {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x,int y,int cx,int cy,uint f);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, IntPtr e);
  [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out PT p);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(PT p);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint f);
  [StructLayout(LayoutKind.Sequential)] public struct PT { public int X, Y; }
  public static bool Take(IntPtr h) {
    IntPtr fg = GetForegroundWindow();
    uint a = GetWindowThreadProcessId(fg, IntPtr.Zero), b = GetWindowThreadProcessId(h, IntPtr.Zero);
    AttachThreadInput(a, b, true);
    keybd_event(0x12,0,0,IntPtr.Zero); keybd_event(0x12,0,2,IntPtr.Zero);
    SetForegroundWindow(h);
    if (GetForegroundWindow() != h) ShowWindow(h, 9);
    if (GetForegroundWindow() != h) {
      SetWindowPos(h, new IntPtr(-1), 0,0,0,0, 0x0001|0x0002|0x0040);
      SetWindowPos(h, new IntPtr(-2), 0,0,0,0, 0x0001|0x0002|0x0040);
    }
    AttachThreadInput(a, b, false);
    SetFocus(h); System.Threading.Thread.Sleep(400);
    return GetForegroundWindow() == h;
  }
  /// 点下去，并回答「这一下落在谁身上」。false = 被别的窗口挡了，这一下不算数。
  public static bool Click(int x, int y, IntPtr app) {
    SetCursorPos(x, y); System.Threading.Thread.Sleep(250);
    PT p; GetCursorPos(out p);
    bool mine = GetAncestor(WindowFromPoint(p), 2) == app;
    mouse_event(0x0002,0,0,0,IntPtr.Zero); System.Threading.Thread.Sleep(120);
    mouse_event(0x0004,0,0,0,IntPtr.Zero); System.Threading.Thread.Sleep(500);
    return mine;
  }
  public static void Hover(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(300); }
  public static void Shift(bool down) { keybd_event(0x10, 0, (uint)(down ? 0 : 2), IntPtr.Zero); }
}
"@

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$C = { param($t) New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $t) }

$p = Get-Process -Name dotnet, MidiPerformer -EA SilentlyContinue |
  Where-Object { $_.MainWindowTitle -eq 'MIDI 演奏器' } | Select-Object -First 1
if (-not $p) { throw 'MidiPerformer 没在跑' }
$h = $p.MainWindowHandle
$root = $AE::FromHandle($h)
$脚本目录 = Split-Path -Parent $MyInvocation.MyCommand.Path

$fail = 0
function 断言([string]$名字, [string]$实际, [string]$期望) {
  if ($实际 -eq $期望) { "  OK   $名字 = $实际" }
  else { "  FAIL $名字 = $实际（期望 $期望）"; $script:fail++ }
}
function 文本 { @($root.FindAll($TS::Descendants, (& $C $CT::Text))) }

# ---------- 轨号文字：定位轨头用（76 号）----------
# ⚠️ 以前这里（下面两处）写的是「Text 名匹配 ^\d{2}$ 且 **x 落在 440..490**」——
# 440..490 是**旧版式**里轨号那格的位置。40 号把侧栏撤掉之后轨号是**第 1 列**
# （MidiPerformer.App/Views/TrackLaneView.axaml:193 是列序的权威），x≈30，
# 旧判据于是**一条轨都找不到**。
# 现在照 tools/uitest/verify-36.ps1:255 的 轨号文字()：先拿**折叠按钮**当锚定行
# （每条轨的轨头上都有一颗，按 Y 排就是轨序），再在那一行的 Y 附近按**名字全等**认轨号 ——
# 不依赖任何 x 区间，版式再挪也不瞎。
# 为什么不横扫 `^\d{2}$`：读数条上「轨」那一格**也是 '02' 这种两位数**，会撞名 —— verify-36.ps1:246 记着这笔账。
# **定位不到就 throw，绝不返回 0 条**：以前返回 0 之后脚本继续往下跑，
# 产出的是「看起来像结论的垃圾」—— 76 号就是这条教训。
function 轨锚 { @($root.FindAll($TS::Descendants, (& $C $CT::Button)) |
    Where-Object { $_.Current.Name -eq '折叠' } | Sort-Object { $_.Current.BoundingRectangle.Y }) }
function 轨号文字([int]$序) {
  $锚 = 轨锚
  if ($锚.Count -eq 0) { throw '一条轨都定位不到：UIA 树里连一颗「折叠」按钮都没有 —— 曲子没载进来，或者版式又变了（76 号）' }
  if ($序 -lt 1 -or $序 -gt $锚.Count) { throw "第 $序 条轨不在场（共 $($锚.Count) 条轨）" }
  $y = $锚[$序 - 1].Current.BoundingRectangle.Y
  $c = @(文本 | Where-Object {
      $_.Current.Name -ceq ('{0:D2}' -f $序) -and
      [Math]::Abs($_.Current.BoundingRectangle.Y - $y) -lt 40 })
  if ($c.Count -ne 1) { throw "第 $序 条轨的轨号文字不唯一（$($c.Count) 个）—— 定位不到就是定位不到，不返回 0（76 号）" }
  $c[0]
}
function 轨号们 { @(1..@(轨锚).Count | ForEach-Object { 轨号文字 $_ }) }

# 「选中」那一格的值：先找到「选中」标签，再取它右边紧挨着的那格值 —— 不写死 Y
#
# 81 号票顺带修的一处（**只加诊断，判据一个字没动**）：定位不到标签时，光看比对那行
# `(找不到「选中」标签)` 分不出「标签被版式删了」和「读数条这会儿读不出来」。
# 这里补**一行**（只说一次）把话说明白 —— 免得后面十条 FAIL 看起来像十个不同的毛病。
# ⚠️ 返回的哨兵字符串**保持原样**：它是 82 号票要处理的东西（改判据或者删），
#    这一票只让它更好读。也**不 throw**：一 throw 就死在第一处，后面九条断言一条都跑不到，
#    那反而更不好读（断点之后那些断言**一条都没验过**）。
function 选中读数 {
  $lbl = 文本 | Where-Object { $_.Current.Name -eq '选中' } | Select-Object -First 1
  if (-not $lbl) {
    if (-not $script:说过找不到选中) {
      $script:说过找不到选中 = $true
      Write-Host '  ! 读数条上定位不到「选中」那一格 —— 后面每条要读它的断言都会 FAIL（十条一起红是同一个原因，不是十个毛病）'
    }
    return '(找不到「选中」标签)'
  }
  $lr = $lbl.Current.BoundingRectangle
  $t = 文本 | Where-Object { $r = $_.Current.BoundingRectangle
      [Math]::Abs($r.Y - $lr.Y) -lt 14 -and $r.X -gt $lr.X -and $r.X -lt ($lr.X + 120) } |
    Sort-Object { $_.Current.BoundingRectangle.X } | Select-Object -First 1
  if ($t) { $t.Current.Name } else { '(读不到)' }
}
# 从读数里抠出轨号；抠不出就照原样返回，好让 FAIL 那行看得见原文
function 选中轨 {
  $s = 选中读数
  if ($s -match '^(?:轨\s*)?(\d{2})\b') { "轨 $($Matches[1])" } else { $s }
}
function 按键([string]$k, [int]$歇 = 700) {
  [System.Windows.Forms.SendKeys]::SendWait($k); Start-Sleep -Milliseconds $歇
}

# 点一下。**点之前重新把窗口拽到前台。**
#
# 这一条也是踩出来的：找音符那几步会起子进程（pwsh 跑 find-note.ps1），
# 它偶尔把前台抢走，于是紧接着那一下点击只是「激活窗口」，没送到卷帘上 ——
# 而 WindowFromPoint 照样说这个像素归 app（窗口铺满整屏，盖没盖住它都归它），
# 检查全过、行为没发生，看着就像「点音符没挪焦点」。
# 同一次点击在这一版之前时过时不过，就是它。
function 点([int]$x, [int]$y) {
  if (-not [V18]::Take($h)) { throw '拽不到前台 —— 台面上有别的窗口压着' }
  return [V18]::Click($x, $y, $h)
}

# 拿 find-note.ps1 按像素找到这一轨里一条音符杠的中点；-Blank 则找没有音符的一个点
function 找音符([int]$track, [switch]$Blank) {
  $argv = @('-Track', $track)
  if ($Blank) { $argv += '-Blank' }
  $out = & pwsh -NoProfile -File (Join-Path $脚本目录 'find-note.ps1') @argv 2>&1
  $行 = @($out) | Where-Object { $_ -match '^\d+,\d+$' } | Select-Object -Last 1
  if (-not $行) { throw "轨 $track 找不到音符：`n$($out -join "`n")" }
  $xy = $行 -split ','
  return @([int]$xy[0], [int]$xy[1])
}

# ---------- 轨头在哪 ----------
# 76 号：判据换成 轨号们（按「折叠」按钮锚定行），不再写死 x 440..490 —— 理由见上面的函数注释。
$heads = 轨号们
if ($heads.Count -lt 3) { throw "只找到 $($heads.Count) 条轨 —— 这首曲子不够验" }
"轨头 $($heads.Count) 条，Y = $(($heads | ForEach-Object { [int]$_.Current.BoundingRectangle.Y }) -join ', ')"
"「选中」那一格现在读作：$(选中读数)"
""

if (-not [V18]::Take($h)) { throw '拽不到前台 —— 台面上有别的窗口压着' }

# ---------- 0. 先把焦点顶到轨 01 ----------
# 顺序有讲究：换焦点会把它那条轨滚进视野，所以**先顶焦点再找音符**。
# 反过来先找的话，轨 01 可能正滚在窗口外，它的卷帘一行都够不着。
"0) Ctrl+↑ 六下（把焦点顶到轨 01，顺带把它滚进视野）"
按键 '^{UP}' 500 | Out-Null
1..5 | ForEach-Object { 按键 '^{UP}' 250 | Out-Null }
# 76 号：判据同上面（轨号们）；换焦点之后轨列表会滚动，所以这里**重新取一次**。
$heads2 = 轨号们
$h1 = [int]$heads2[0].Current.BoundingRectangle.Y
"     轨 01 的轨头现在在 Y=$h1"
if ($h1 -lt 0) { throw '轨 01 还在窗口外 —— 换焦点没把它滚进来' }

# ---------- 1. 找要点的音符 ----------
"1) 按像素找音符"
$n3 = 找音符 3; "     轨 03 的音符在 ($($n3[0]), $($n3[1]))"
$n1 = 找音符 1; "     轨 01 的音符在 ($($n1[0]), $($n1[1]))"
""

# ---------- 2. 点轨 03 的音符 ----------
"2) 点轨 03 的音符"
$落对 = 点 $n3[0] $n3[1]
if (-not $落对) { throw '点下去被别的窗口挡了 —— 把台面清干净再跑' }
"     点完读数：$(选中读数)"
断言 '点轨 03 的音符之后「选中」' (选中轨) '轨 03'
""

# ---------- 3. 核心：点击把焦点带过去了没有 ----------
# 焦点若还留在轨 01，这一下 Ctrl+→ 会落到轨 01；焦点跟着点击走了才会落轨 03
"3) 紧接着按 Ctrl+→ —— 焦点跟没跟着点击走，就看这一下"
按键 '^{RIGHT}'
"     读数：$(选中读数)"
断言 '点击之后 Ctrl+→ 落在' (选中轨) '轨 03'
""

# ---------- 4. 把焦点推回轨 01，选中集留在轨 03 ----------
"4) Ctrl+↑ 六下（焦点回轨 01），选中集不该跟着动"
1..6 | ForEach-Object { 按键 '^{UP}' 300 | Out-Null }
"     读数：$(选中读数)"
断言 '换焦点之后「选中」' (选中轨) '轨 03'
""

# ---------- 5. 核心：导航跟的是焦点轨 ----------
"5) 焦点在轨 01、选中的音在轨 03 —— Ctrl+→ 该落在轨 01"
按键 '^{RIGHT}'
"     读数：$(选中读数)"
断言 'Ctrl+→ 落到' (选中轨) '轨 01'
""

# ---------- 6. 换到轨 03，同样落回它自己 ----------
"6) Ctrl+↓ 两下（焦点到轨 03），再按 Ctrl+→"
按键 '^{DOWN}' 300 | Out-Null
按键 '^{DOWN}' 300 | Out-Null
按键 '^{RIGHT}'
"     读数：$(选中读数)"
断言 'Ctrl+→ 落到' (选中轨) '轨 03'
""

# ---------- 7. 连按，一步都不许跨轨 ----------
"7) 在轨 03 上连按 6 次 Ctrl+→ —— 全程必须留在轨 03"
$跑偏 = 0
for ($i = 1; $i -le 6; $i++) {
  按键 '^{RIGHT}' 450 | Out-Null
  $t = 选中轨
  if ($t -ne '轨 03') { "     第 $i 步跑到 $t"; $跑偏++ }
}
断言 '连按 6 次跑偏次数' "$跑偏" '0'
""

# ---------- 8. 往回也一样 ----------
"8) 在轨 03 上连按 8 次 Ctrl+← —— 全程必须留在轨 03"
$跑偏 = 0
for ($i = 1; $i -le 8; $i++) {
  按键 '^{LEFT}' 450 | Out-Null
  $t = 选中轨
  if ($t -ne '轨 03') { "     第 $i 步跑到 $t"; $跑偏++ }
}
断言 '连按 8 次跑偏次数' "$跑偏" '0'
""

# ---------- 9. 悬浮不许挪焦点 ----------
# 鼠标扫过轨 01 的音符（悬浮读数会变），焦点得钉在轨 03
"9) 鼠标在轨 01 的音符上划来划去 —— 焦点不许因此跑掉"
foreach ($dx in -30, -10, 10, 30) { [V18]::Hover(($n1[0] + $dx), $n1[1]) }
Start-Sleep -Milliseconds 400
按键 '^{RIGHT}'
"     读数：$(选中读数)"
断言 '悬浮之后 Ctrl+→ 仍在' (选中轨) '轨 03'
""

# ---------- 10. 点该轨空白也要挪焦点 ----------
# 这条和「点音符」走的是同一句 SetFocusedTrack（摆在捕获之后、分支之前），
# 但值得单独走一遍 —— 万一哪天有人把那句话挪进「命中了音符」那个分支里，只有这条会响
"10) 点轨 01 的空白处（不是音符）—— 焦点也要跟过去"
$b1 = 找音符 1 -Blank
"     轨 01 的空白点在 ($($b1[0]), $($b1[1]))"
if (-not (点 $b1[0] $b1[1])) { throw '点下去被别的窗口挡了' }
"     点完读数：$(选中读数)"
按键 '^{RIGHT}'
"     再 Ctrl+→：$(选中读数)"
断言 '点空白之后 Ctrl+→ 落在' (选中轨) '轨 01'
""

# ---------- 11. Shift 加选也要挪焦点 ----------
# **坐标要现取。** 这儿的坑是踩出来的：开头顶焦点的 Ctrl+↑ 会把轨 01 滚进视野，
# 可后面换焦点到轨 03 又会滚一次 —— 第 1 步记下的 (759, 431) 到这儿早就不是轨 01 的卷帘了。
# 那一下 Shift 点于是落在轨 03 上（焦点老实跟到了轨 03），却看着像「Shift 不挪焦点」。
#
# 所以先 Ctrl+↑ 把轨 01 顶回来并滚进视野，**再重新找一次音符**，然后才换到轨 02 动手。
"11) Ctrl+↑ 六下把轨 01 顶回视野，重新找它的音符，焦点挪到轨 02，再 Shift 点轨 01 的音符"
1..6 | ForEach-Object { 按键 '^{UP}' 300 | Out-Null }
$n1b = 找音符 1
"     轨 01 的音符现在在 ($($n1b[0]), $($n1b[1]))"
按键 '^{DOWN}' 300 | Out-Null
[V18]::Shift($true)
$落对 = 点 $n1b[0] $n1b[1]
[V18]::Shift($false)
if (-not $落对) { throw '点下去被别的窗口挡了' }
"     点完读数：$(选中读数)"
按键 '^{RIGHT}'
"     再 Ctrl+→：$(选中读数)"
断言 'Shift 加选之后 Ctrl+→ 落在' (选中轨) '轨 01'
""

if ($fail -eq 0) { "全过" } else { "$fail 条没过" }
# 81 号票：裁决行 + 退出码。裁决行是给 run-all.ps1 复核用的记号（它拿这行跟退出码对，
# 对不上就把这一条降级成红）—— 少了它，这条脚本在总表里会被当成「没有裁决」而**降级成红**。
"==== uitest 裁决 不过=$fail"
exit $fail
