# 19 号工单：Delete/Backspace 删选中 + 空白处拖框改成「只选中」。
#
# 判据分两层：
#   · 读数条上「选中」那一格 —— 说清「现在选着谁」「有没有落回邻居」。
#   · **像素** —— 说清「那个音到底还在不在」。这一条是这张工单的正题：
#     从前框选是「松手就删」，所以「拖完那个音还在不在」正是新旧两版的分水岭，
#     而读数条说不清这件事（框住的音还在，读数条也照样报得出一个音）。
#     find-note.ps1 按像素找**第一条**音符横杠，报出它横跨的 x 区间；删掉它之后
#     那一杠要么换一个位置、要么整条轨空掉，两种都看得见。
#
# 几处踩过的坑（从 verify-18 继承）：
#   · 点之前要确认那个像素归 app（WindowFromPoint），台面上压着别的窗口时点下去石沉大海。
#   · **点/拖之前重新拽一次前台** —— 找音符要起子进程，它偶尔把前台抢走，
#     那一下手势就只剩「激活窗口」的作用，看着却像「这段逻辑没生效」（见 点 / 拖）。
#   · 每一步用到的坐标都要现取：视图会滚，开头记的坐标过几步就不是那个音了。
#   · 框选要**按在空白上**（按在音符身上就是拖动），所以 y 用 find-note -Blank
#     报出来的那一行 —— 那一行是它扫出来的「整行没有长横杠」。
#
# 用法: pwsh -File verify-19.ps1   （app 要先开着、已经载入曲子）
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices;
public class V19 {
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
  /// 横拖一道。分几步走：一步跳到终点的话，Avalonia 只收到「按下 → 抬起」，
  /// 中间那一串 PointerMoved 一个都没有，拖动就白拖了。
  public static bool Drag(int x1, int y1, int x2, int y2, IntPtr app) {
    SetCursorPos(x1, y1); System.Threading.Thread.Sleep(250);
    PT p; GetCursorPos(out p);
    bool mine = GetAncestor(WindowFromPoint(p), 2) == app;
    mouse_event(0x0002,0,0,0,IntPtr.Zero); System.Threading.Thread.Sleep(150);
    const int steps = 10;
    for (int i = 1; i <= steps; i++) {
      SetCursorPos(x1 + (x2 - x1) * i / steps, y1 + (y2 - y1) * i / steps);
      System.Threading.Thread.Sleep(60);
    }
    mouse_event(0x0004,0,0,0,IntPtr.Zero); System.Threading.Thread.Sleep(600);
    return mine;
  }
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
function 断言真([string]$名字, [bool]$条件, [string]$原文) {
  if ($条件) { "  OK   $名字（$原文）" } else { "  FAIL $名字（$原文）"; $script:fail++ }
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
function 选中读数 {
  $lbl = 文本 | Where-Object { $_.Current.Name -eq '选中' } | Select-Object -First 1
  if (-not $lbl) { return '(找不到「选中」标签)' }
  $lr = $lbl.Current.BoundingRectangle
  $t = 文本 | Where-Object { $r = $_.Current.BoundingRectangle
      [Math]::Abs($r.Y - $lr.Y) -lt 14 -and $r.X -gt $lr.X -and $r.X -lt ($lr.X + 120) } |
    Sort-Object { $_.Current.BoundingRectangle.X } | Select-Object -First 1
  if ($t) { $t.Current.Name } else { '(读不到)' }
}
function 按键([string]$k, [int]$歇 = 700) {
  [System.Windows.Forms.SendKeys]::SendWait($k); Start-Sleep -Milliseconds $歇
}

# 点一下 / 拖一道。**之前重新把窗口拽到前台**（理由见文件头）。
function 点([int]$x, [int]$y) {
  if (-not [V19]::Take($h)) { throw '拽不到前台 —— 台面上有别的窗口压着' }
  return [V19]::Click($x, $y, $h)
}
function 拖([int]$x1, [int]$y1, [int]$x2, [int]$y2) {
  if (-not [V19]::Take($h)) { throw '拽不到前台 —— 台面上有别的窗口压着' }
  return [V19]::Drag($x1, $y1, $x2, $y2, $h)
}

# 按像素报出这一轨**最上面那条音符横杠**：中心点、以及它横跨的 x 区间。
# 找不到（这一轨空了 / 滚在窗口外）返回 $null。
function 取音符([int]$track) {
  $out = @(& pwsh -NoProfile -File (Join-Path $脚本目录 'find-note.ps1') -Track $track 2>&1)
  $行 = $out | Where-Object { $_ -match '^\d+,\d+$' } | Select-Object -Last 1
  if (-not $行) { return $null }
  $xy = $行 -split ','
  $跨 = $out | Where-Object { $_ -match '横跨 x (\d+)\.\.(\d+)' } | Select-Object -First 1
  $l = -1; $r = -1
  if ($跨 -and $跨 -match '横跨 x (\d+)\.\.(\d+)') { $l = [int]$Matches[1]; $r = [int]$Matches[2] }
  return @{ X = [int]$xy[0]; Y = [int]$xy[1]; L = $l; R = $r }
}
# 两条横杠是不是「同一个音」：左右沿都在容差里。
# 不能写成逐像素相等 —— 撤销会把它还原的那个音**滚进视野**，卷帘横挪一两像素，
# 同一根杠量出来就差了 2px。差 2px 是滚动，差 200px 才是「没还原」。
function 同一处($甲, $乙, [int]$容差 = 6) {
  if (-not $甲 -or -not $乙) { return $false }
  return ([Math]::Abs($甲.L - $乙.L) -le $容差) -and ([Math]::Abs($甲.R - $乙.R) -le $容差)
}
# 离 (x,y) 最近的**空白像素** —— 框选要按在空白上（按在音符身上就成了拖动，不是框选）。
# 不要求整行都空，只要**按下去那一点**空就行，所以就近找。
function 取空白点([int]$x, [int]$y) {
  $out = @(& pwsh -NoProfile -File (Join-Path $脚本目录 'find-note.ps1') -Track 1 -Near "$x,$y" 2>&1)
  $行 = $out | Where-Object { $_ -match '^\d+,\d+$' } | Select-Object -Last 1
  if (-not $行) { return $null }
  $xy = $行 -split ','
  return @{ X = [int]$xy[0]; Y = [int]$xy[1] }
}

# ---------- 轨头在哪 ----------
# 76 号：判据换成 轨号们（按「折叠」按钮锚定行），不再写死 x 440..490 —— 理由见上面的函数注释。
$heads = 轨号们
if ($heads.Count -lt 1) { throw "一条轨都没找到 —— 先载入一首多轨曲子" }
"轨头 $($heads.Count) 条，Y = $(($heads | ForEach-Object { [int]$_.Current.BoundingRectangle.Y }) -join ', ')"
"「选中」那一格现在读作：$(选中读数)"
""

if (-not [V19]::Take($h)) { throw '拽不到前台 —— 台面上有别的窗口压着' }

# ---------- 0. 把焦点顶到轨 01，顺带把它滚进视野 ----------
# 顺序有讲究：换焦点会把它那条轨滚进视野，所以**先顶焦点再找音符**。
"0) Ctrl+↑ 六下（把焦点顶到轨 01，顺带把它滚进视野）"
按键 '^{UP}' 500 | Out-Null
1..5 | ForEach-Object { 按键 '^{UP}' 250 | Out-Null }
# 76 号：判据同上面（轨号们）；换焦点之后轨列表会滚动，所以这里**重新取一次**。
$h1 = [int](轨号文字 1).Current.BoundingRectangle.Y
"     轨 01 的轨头现在在 Y=$h1"
if ($h1 -lt 0) { throw '轨 01 还在窗口外 —— 换焦点没把它滚进来' }

$n0 = 取音符 1
if (-not $n0) { throw '轨 01 里按像素找不到音符 —— 换一首有音的曲子' }
"     轨 01 最上面那条音符：(X=$($n0.X), Y=$($n0.Y))，横跨 x $($n0.L)..$($n0.R)"
""

# 框选的横向范围：往两边各探出去 30px，区间仍然盖住这个音
#（NotesInRange 按起点命中，from < 起点 < to 就够）
$框左 = $n0.L - 30
$框右 = $n0.R + 30
# 76 号：这里以前写死 `470`（「别探到轨头里去」）—— 470 = 旧版式那个 424px 侧栏 + 余量。
# 新版式侧栏没了、卷帘从轨号那一列（x≈30）就开始，写死 470 会把**框左推到音符右面去**，
# 框就盖不住那个音，「框选没有删掉音符」于是变成假红。左界同样从定位到的轨号文字推出来。
$卷帘左 = [int](轨号文字 1).Current.BoundingRectangle.X + 4
if ($框左 -lt $卷帘左) { $框左 = $卷帘左 }
if ($框右 -le $框左) { throw "框选范围是空的（左 $框左 ≥ 右 $框右）—— 定位或版式不对，不硬拖一把了事（76 号）" }
$空0 = 取空白点 $框左 $n0.Y
if (-not $空0) { throw "($框左,$($n0.Y)) 旁边找不到空白像素 —— 框选验不了" }
"框选范围 x $框左..$框右，按在 ($($空0.X),$($空0.Y))（空白，上下挪了 $($空0.Y - $n0.Y)px）"
""

# ---------- 1. 正题：框选只选中，不删 ----------
"1) 在空白处横拖一道框住那个音 —— 这个音**必须还在**"
if (-not (拖 $框左 $空0.Y $框右 $空0.Y)) { throw '拖出去被别的窗口挡了 —— 把台面清干净再跑' }
"     拖完读数：$(选中读数)"
$n1 = 取音符 1
if (-not $n1) {
  断言真 '框选没有删掉音符' $false '框完这一轨一个音都没有了 —— 说明它还是「松手就删」'
} else {
  断言真 '框选没有删掉音符' (同一处 $n1 $n0) "第一个音的横杠仍在 x $($n1.L)..$($n1.R)（框之前 $($n0.L)..$($n0.R)）"
}
断言真 '框选之后选中集不是空的' ((选中读数) -ne '—') "读数 = $(选中读数)"
""

# ---------- 2. 框住的那批能接着删掉 ----------
"2) 紧接着按 Delete —— 框住的那个音这下该没了"
按键 '{DEL}'
$n2 = 取音符 1
if (-not $n2) {
  "  OK   删掉了（这一轨) 已经没有音符"
} else {
  断言真 'Delete 删掉了框住的那个音' ($n2.L -ne $n0.L) "第一个音的横杠从 x $($n0.L) 挪到了 x $($n2.L)"
}
"     删完读数：$(选中读数)"
""

# ---------- 3. 撤销整批还原 ----------
"3) Ctrl+Z —— 整批还原（一次删除 = 撤销栈上一格）"
按键 '^z'
$n3 = 取音符 1
if (-not $n3) {
  断言真 '撤销把音还原了' $false '撤销之后这一轨还是没有音'
} else {
  断言真 '撤销把音还原了' (同一处 $n3 $n0) "第一个音的横杠回到 x $($n3.L)..$($n3.R)（原来是 $($n0.L)..$($n0.R)）"
}
""

# ---------- 4. Backspace 和 Delete 一样 ----------
"4) Backspace 也该能删（两个键都绑）"
$n4 = 取音符 1
if (-not (点 $n4.X $n4.Y)) { throw '点下去被别的窗口挡了' }
"     点完读数：$(选中读数)"
按键 '{BS}'
$n4b = 取音符 1
if (-not $n4b) {
  "  OK   Backspace 删掉了（这一轨已经没有音符）"
} else {
  断言真 'Backspace 删掉了选中的音' ($n4b.L -ne $n4.L) "第一个音的横杠从 x $($n4.L) 挪到了 x $($n4b.L)"
}
按键 '^z'
$n4c = 取音符 1
if (-not $n4c) {
  断言真 '撤销还原' $false '撤销之后这一轨还是没有音'
} else {
  断言真 '撤销还原' (同一处 $n4c $n4) "横杠回到 x $($n4c.L)..$($n4c.R)（原来是 $($n4.L)..$($n4.R)）"
}
""

# ---------- 5. 删完落到邻居 ----------
"5) 删一个音，选中该落到时间上最近的邻居（优先右边），不是清空"
$n5 = 取音符 1
if (-not (点 $n5.X $n5.Y)) { throw '点下去被别的窗口挡了' }
$前 = 选中读数
"     删之前读数：$前"
按键 '{DEL}'
$后 = 选中读数
"     删之后读数：$后"
断言真 '删完读数变了（落到邻居上）' ($后 -ne $前) "「$前」→「$后」"
断言真 '删完仍然落在同一条轨上' ($后 -match '^(?:轨\s*)?01\b') "$后"
按键 '^z'
$n5b = 取音符 1
断言真 '撤销还原' (同一处 $n5b $n5) "横杠回到 x $($n5b.L)..$($n5b.R)（原来是 $($n5.L)..$($n5.R)）"
""

# ---------- 6. 没选中时按删除键什么都不该发生 ----------
"6) 一个音都没选中时按 Delete —— 一个音都不该少"
按键 '{ESC}' 300 | Out-Null
# 点空白处：按下即清空选中（拖起来的框选什么也没框到，选中集是空的）
$n6 = 取音符 1
if (-not (拖 $框左 $空0.Y ($框左 + 8) $空0.Y)) { throw '拖出去被别的窗口挡了' }
"     点完读数：$(选中读数)"
$n6b = 取音符 1
断言真 '清空选中没有动谱面' ($n6b -and $n6b.L -eq $n6.L) "横杠仍在 x $($n6b.L)"
$前6 = 选中读数
按键 '{DEL}'
断言真 '没选中时 Delete 什么都没干' ((选中读数) -eq $前6) "读数还是「$前6」"
""

# ---------- 7. 框选不分音高 ----------
"7) 同一段横向区间，按在**离那个音 60px 远**的地方拖 —— 照样框住它（只按时间命中）"
$n7 = 取音符 1
$空7 = 取空白点 $框左 ($n7.Y + 60)
if (-not $空7) { throw "($框左,$($n7.Y + 60)) 旁边找不到空白像素" }
"     按在 ($($空7.X),$($空7.Y))（离那个音 $($空7.Y - $n7.Y)px，约 $([Math]::Round(($空7.Y - $n7.Y) / 7)) 个音高行）"
if (-not (拖 $框左 $空7.Y $框右 $空7.Y)) { throw '拖出去被别的窗口挡了' }
$读7 = 选中读数
"     拖完读数：$读7"
断言真 '纵向拖多远都框得住' (($读7 -ne '—') -and ($读7 -match '轨\s*01')) "$读7"
$n7b = 取音符 1
断言真 '框完那个音还在' (同一处 $n7b $n7) "横杠仍在 x $($n7b.L)..$($n7b.R)"
按键 '{DEL}'
$n7c = 取音符 1
if (-not $n7c) {
  "  OK   接着 Delete 删掉了（这一轨已经没有音符）"
} else {
  断言真 '接着 Delete 删掉了' ($n7c.L -ne $n7.L) "第一个音的横杠从 x $($n7.L) 挪到了 x $($n7c.L)"
}
按键 '^z'
$n7d = 取音符 1
断言真 '撤销还原' (同一处 $n7d $n7) "横杠回到 x $($n7d.L)..$($n7d.R)"
""

# ---------- 8. 多选整批删：一次命令 = 一格撤销 ----------
# 判据是「第一个音挪到哪儿了」：只删掉一个的话，它会挪到紧挨着的下一个（x≈1732，
# 前面第 2、4、5 步量的就是这个数）；整批删掉的话，它会一口气跳到框选范围之外。
# 撤销那一步同理：一次 Ctrl+Z 要是只还回来一个音，第一个音就回不到原处 —— 那就说明
# 「整批删」实际是按音符逐个记的账（一个音一格撤销），和「一次调用 = 一格撤销」不符。
"8) 框一大段（x $框左..2900）—— 框住的那一批该被**一次**删掉，一次 Ctrl+Z 全回来"
$n8 = 取音符 1
if (-not (拖 $框左 $空0.Y 2900 $空0.Y)) { throw '拖出去被别的窗口挡了' }
$读8 = 选中读数
"     拖完读数：$读8   框选范围 x $框左..2900"
$n8a = 取音符 1
断言真 '框完一个音都没少' (同一处 $n8a $n8) "横杠仍在 x $($n8a.L)..$($n8a.R)"
按键 '{DEL}'
$n8b = 取音符 1
if (-not $n8b) {
  "  OK   整段一次删光（这一轨已经没有音符）"
} else {
  断言真 '删的是一整批，不是只有一个' ($n8b.L -gt 2900) "第一个音一口气挪到了 x $($n8b.L)（只删一个的话它只会挪到紧挨着的下一个，x≈1732）"
}
按键 '^z'
$n8c = 取音符 1
断言真 '一次 Ctrl+Z 把整批还回来' (同一处 $n8c $n8) "横杠回到 x $($n8c.L)..$($n8c.R)"
""

if ($fail -eq 0) { "全过" } else { "$fail 条没过" }
# 81 号票：裁决行 + 退出码。裁决行是给 run-all.ps1 复核用的记号（它拿这行跟退出码对，
# 对不上就把这一条降级成红）—— 少了它，这条脚本在总表里会被当成「没有裁决」而**降级成红**。
"==== uitest 裁决 不过=$fail"
exit $fail
