# 18 号工单：Ctrl+←/→ 只在焦点轨里走 + 点音符焦点跟随。
#
# 判据走**读数条上「轨」那一格**，不靠像素。这一格的轨号就是「刚才那个音在哪条轨上」，
# 而这条工单改的正是「下一个音去哪条轨找」。
# （82 号：改版前读的是另一格——「选中」。21 号把读数条上两套读数并成了一套，
#  那一格**被有意删掉**了，改指到并进来之后的「轨」那一格；详见下面 读数条 那一段。）
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
using System; using System.Runtime.InteropServices; using System.Text;
public class V18 {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
  // 82 号：拽不回前台时**把「现在前台是谁」打进日志**（只有一个「拽不到」看不出所以然）
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")] public static extern uint PidOf(IntPtr h, out uint pid);
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
  /// 前台是不是就是它 —— Take 之后复检一次（Take 里那 400ms 里别人也可能插进来）。
  public static bool Mine(IntPtr h) { return GetForegroundWindow() == h; }
  /// 一个窗口是谁（类名 / 标题 / 句柄 / PID）—— 只给诊断用。
  public static string Desc(IntPtr h) {
    if (h == IntPtr.Zero) { return "（没有窗口）"; }
    var c = new StringBuilder(256); GetClassName(h, c, 256);
    var t = new StringBuilder(256); GetWindowText(h, t, 256);
    uint pid; PidOf(h, out pid);
    return "类名「" + c.ToString() + "」标题「" + t.ToString() + "」句柄 " + h.ToString() + " PID " + pid;
  }
  /// 某个屏幕坐标上压着的是谁（**点/拖「不算数」时把它打进日志**：
  /// 光说一句「被别的窗口挡了」，看日志的人不知道挡的是谁，也分不出「外人的窗」和「自家弹出来的东西」）。
  public static string At(int x, int y) { PT p; p.X = x; p.Y = y; return Desc(WindowFromPoint(p)); }
  /// 现在压在前台的是谁。
  public static string Fg() { return Desc(GetForegroundWindow()); }
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

# ==================== 读数条（82 号票改的就是这一段）====================
#
# 原文读的是**「选中」那一格**。21 号把读数条上**两套**读数并成了**一套**：
#   轨 / 音高 / 小节 / 拍位 / 时值，值是「**悬浮的音优先，没悬浮就用主选中的音**」
#   —— 见 MidiPerformer.App/Views/MainWindow.axaml 里 `<Border Grid.Row="3" Classes="readoutbar">`
#   那一段（ReadoutDetail + 五个 readout-key/readout-value），
#   依据是 docs/wireframe.html 那一段的注释（`<div class="readout">` 上面，见标注 6），原话：
#     「**一套读数，不是两套。** 从前左边是「悬停」那一套、右边是「选中」那一套，同一个音
#       在两处各显示一半，鼠标一移开左边变空、右边还留着 —— 看着像两台机器。现在合成一套：
#       轨 / 音高 / 小节 / 拍位 / 时值，**悬浮的音优先，没悬浮就用主选中的音**。」
#   以及 .scratch/midi-performer/issues/21-readout-merge-and-position-move.md
#   （它顺手删了 `Format.Selection` 和那条测试；验收里写着「悬停」「选中」两个旧标签都不在了）。
# **那一格「选中」是被有意删掉的，没有搬到别处**（全仓 .axaml 里 `Text="选中"` 零命中）——
# 所以这一票的路子是「**改指到并进来之后的那一格『轨』**」：轨号原本就在说
# 「刚才那个音在哪条轨」（21 号原话：「轨号要留下。合并之后它是「这个音在哪条轨」的唯一线索」）。
#
# 🔴 合并带来一件**必须处理**的事：这一格**悬浮优先**。鼠标正压在一个音上时，
#    它报的是**那个悬停的音**，与选中集无关 —— 而本脚本要验的一直是
#    「**选中的音**落在哪条轨」。不处理的话：
#      · 第 2 步「点完选中轨 03」会**不管点没点中都绿**（悬停的那个音就是刚点的那个）；
#      · 第 5 步（焦点在轨 01、鼠标还压在轨 03 的音上）读到 03 —— 看着像功能坏了。
#    ⇒ 每次读之前**先把鼠标挪出卷帘**（挪开鼠标），让读数回落到选中：指针一离开
#      PianoRollLane，它就在 OnPointerExited 里把悬停清成 null
#      （21 号在真机上量过这条路：「点中甲 → 悬停乙，读数跟着乙走；再移开 → 回到甲，不是变空」）。
#      第 5、9 步反过来是这个动作的**负控** —— 挪不动的话那两条必红。
#
# 五格连标签一起藏起来（没悬停也没选中）是**有意的** —— wireframe.html 标注 6 原话：
#   「两个都没有的时候，这五格**连标签一起藏起来**（不是显示一排破折号）——「还没载曲子」和
#     「载了但没悬停」是同一件事，不该一个一排 `—`、一个整块消失。」
# 那是「什么都没选中」这个**状态**，不是定位失败 ⇒ 返回 没有读数 这个记号，
# 好让 FAIL 那行看得见它。而「读数条那一行在、五格却缺了某一格」才是定位失败 ——
# 那种一律 **throw**（照上面 轨号文字 那句的规矩：定位不到就是定位不到，不返回 0）。
$读数标签 = @('轨','音高','小节','拍位','时值')
$没有读数 = '（没有读数：没悬停也没选中）'
$停车点 = $null

# 读数条那一行在哪：拿**「拍位」**当锚 —— 这个标签只在读数条上出现（拿全等比）；
# 「小节」会撞上导航条那句「跳到 … 小节」，「轨」会撞上轨头（见 轨号文字 的注释）。
# 整块藏起来时五格一个都不在，退回**提示行**那一行（提示行分两层，两句各留一个记号）。
function 读数条Y([object[]]$t) {
  $e = @($t | Where-Object { $_.Current.Name -eq '拍位' } | Select-Object -First 1)
  if ($e.Count) { return [int]$e[0].Current.BoundingRectangle.Y }
  $h = @($t | Where-Object {
      $_.Current.Name -like '空格 播放*' -or $_.Current.Name -like '*取消选中*' } | Select-Object -First 1)
  if ($h.Count) { return [int]$h[0].Current.BoundingRectangle.Y }
  return -1
}
# 取某格的标签右边紧挨着的那格值（不写死 Y）
function 取格([string]$标签, [int]$带Y, [object[]]$t) {
  $lbl = @($t | Where-Object {
      $_.Current.Name -eq $标签 -and [Math]::Abs($_.Current.BoundingRectangle.Y - $带Y) -lt 14 } |
    Select-Object -First 1)
  if ($lbl.Count -eq 0) { throw "读数条上找不到「$标签」那一格（这一行在 Y=$带Y）—— 定位不到就是定位不到，不返回空（82 号）" }
  $lr = $lbl[0].Current.BoundingRectangle
  # 值那一格**不可能是标签之一** —— 抽掉这个过滤，「某一格的值不见了」会被误判成
  # 「右边那个标签就是它的值」，于是该 throw 的地方悄悄返回一个看起来正常的字符串。
  $v = @($t | Where-Object { $r = $_.Current.BoundingRectangle
      [Math]::Abs($r.Y - $lr.Y) -lt 14 -and $r.X -gt $lr.X -and $r.X -lt ($lr.X + 120) -and
      $读数标签 -notcontains $_.Current.Name } |
    Sort-Object { $_.Current.BoundingRectangle.X } | Select-Object -First 1)
  if ($v.Count -eq 0) { throw "读数条上「$标签」右边没有值那一格 —— 定位不到就是定位不到（82 号）" }
  $v[0].Current.Name
}
# 五格拼成一行。形状照改版前那一格「选中」（它当时是 `轨 01 · C4（1）· 1.00 拍`）：
# 开头一定是「轨 NN」，后面几格留着 —— 「删完落到邻居上」这类断言要看得见**换了哪个音**。
function 读数一行([object[]]$t, [int]$带Y) {
  "轨 $(取格 '轨' $带Y $t) · $(取格 '音高' $带Y $t) · $(取格 '小节' $带Y $t) 小节 · $(取格 '拍位' $带Y $t) · $(取格 '时值' $带Y $t)"
}
# 读一次（一份 UIA 文本快照只走一趟树）
function 读数([object[]]$t) {
  $带Y = 读数条Y $t
  if ($带Y -lt 0) { throw '读数条定位不到：连提示行那句（「空格 播放…」/「…取消选中」）都不在 UIA 树里 —— 版式又变了（82 号）' }
  $在 = @($t | Where-Object {
      $读数标签 -contains $_.Current.Name -and
      [Math]::Abs($_.Current.BoundingRectangle.Y - $带Y) -lt 14 })
  if ($在.Count -eq 0) { return $没有读数 }
  读数一行 $t $带Y
}
# 把鼠标挪出卷帘（理由见上）。停在**工具栏最左边那条内边距**上：那儿是 Border.toolbar
# 自己的底色，底下没有按钮、没有悬浮效果、也没有 ToolTip —— 而它在卷帘之外。
function 挪开鼠标 {
  if (-not $script:停车点) {
    $w = $root.Current.BoundingRectangle
    $script:停车点 = @([int]($w.X + 6), [int]($w.Y + 14))
  }
  [V18]::Hover($script:停车点[0], $script:停车点[1])
}
# 不挪鼠标读一次 —— **只给诊断那一行用**：把「鼠标压在轨 01 的音上时读数报的是 01」
# 这件事摆出来，那是「悬浮优先」这条规矩的现场证据。
function 此刻读数 { 读数 (文本) }
# 🔴 判据要用的那一份：先挪开鼠标（让读数回落到**选中**），再读。
function 选中读数 { 挪开鼠标; 读数 (文本) }
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
#
# 拽前台：**有界重试**。（82 号）
#
# 上面那条「点之前重新拽一次前台」是有来由的，但**只拽一次不够**：find-note 那个子进程
# 抢前台是**异步**的 —— 它可能正好落在 Take 自己那 400ms 里，于是这一次就报 false。
# 这里最多试 4 次、每次隔 500ms；4 次都拽不回来，**照抛**。
# 这是**对齐环境，不是放松判据**：判据一个字没动，只是把「一次必失就算输」改成有界重试。
# 每次成功之后再用 Mine 复检一眼（Take 里那 400ms 里别人也可能插进来）。
function 拽([int]$试 = 4) {
  for ($i = 1; $i -le $试; $i++) {
    if ([V18]::Take($h) -and [V18]::Mine($h)) { return $true }
    "     ⚠ 第 $i 次没拽回前台（现在前台是 $([V18]::Fg())）—— 隔一下再拽"
    Start-Sleep -Milliseconds 500
  }
  return $false
}
function 点([int]$x, [int]$y) {
  if (-not (拽)) { throw "拽不到前台 —— 台面上有别的窗口压着（现在前台是 $([V18]::Fg())）" }
  return [V18]::Click($x, $y, $h)
}

# 点一下，**并确认这一下有生效**：读数动过了才算数。（82 号）
#
# 为什么需要这个：第 10 步那一下「点空白」实机里被吞过一次 ——
# 点完读数跟点之前**一模一样**（轨 03 · B5… · 2.00 拍），于是后面 Ctrl+→ 还落在轨 03，
# 看着像「点空白不挪焦点」。吞掉的原因在脚手架这一侧：找音符起子进程，它偶尔抢一下前台，
# 那一下点击只剩「激活窗口」的作用。
# 判「吞没吞」的办法是**看状态有没有动**（点空白本就该把选中清掉）；没动就**原样补点一次**，
# 补完还是没动，后面的断言照红 —— **判据一个字没松**，补过几次会打进日志。
$补点 = 0
function 点准([int]$x, [int]$y) {
  $前 = 选中读数
  if (-not (点 $x $y)) { throw "点下去被别的窗口挡了 —— ($x,$y) 上压着 $([V18]::At($x,$y))" }
  if ((选中读数) -ne $前) { return $true }
  $script:补点++
  "     ⚠ 这一下点下去读数没动（$前）—— 被吞了，原样补点一次"
  if (-not (点 $x $y)) { throw "点下去被别的窗口挡了 —— ($x,$y) 上压着 $([V18]::At($x,$y))" }
  return ((选中读数) -ne $前)
}

# 起 find-note 子进程：**先试 -WindowStyle Hidden**（别让它冒出一个会抢前台的控制台窗），
# 没拿到坐标就**退回普通起法** —— 这样「起法」这桩事就永远压不住「找音符」这个正题。
# 为什么留这条退路：实测 pwsh 7.6.6 里 `-WindowStyle Hidden` 配 `-File` 是好的（6/6 一致），
# 但配 `-Command` 会让子进程**直接死掉（退出码 -1、无输出）**—— 同一个开关两种行为，
# 那就别赌它永远好使：拿不到坐标就退回去（真实失败照样是真实失败，退回去也一样红）。
function 跑找音符([string[]]$argv) {
  $出 = @(& pwsh -NoProfile -WindowStyle Hidden -File (Join-Path $脚本目录 'find-note.ps1') @argv 2>&1)
  if (@($出 | Where-Object { $_ -match '^\d+,\d+$' }).Count) { return $出 }
  "     ⚠ 隐藏起法这一趟没拿到坐标 —— 退回普通起法再来一次"
  @(& pwsh -NoProfile -File (Join-Path $脚本目录 'find-note.ps1') @argv 2>&1)
}

# 拿 find-note.ps1 按像素找到这一轨里一条音符杠的中点；-Blank 则找没有音符的一个点
function 找音符([int]$track, [switch]$Blank) {
  $argv = @('-Track', $track)
  if ($Blank) { $argv += '-Blank' }
  $out = 跑找音符 $argv
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
"「轨」那一格现在读作：$(选中读数)（此刻还没点过音符，多半是「没有读数」）"
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
$落对 = 点准 $n3[0] $n3[1]
if (-not $落对) { throw "点下去被别的窗口挡了 —— ($($n3[0]),$($n3[1])) 上压着 $([V18]::At($n3[0],$n3[1]))，把台面清干净再跑" }
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
#
# 82 号：这里顺带把「**悬浮优先**」这条规矩摆出来当现场证据 —— 下面两行读数**必然不同**：
#   上面那份鼠标还压在轨 01 的音上（读数报的是那个悬停的音），下面那份挪开了（回落到选中集）。
#   这一条也正是 挪开鼠标 那个动作的负控：挪不动的话它就报 轨 01，本就该红。
"9) 鼠标在轨 01 的音符上划来划去 —— 焦点不许因此跑掉"
foreach ($dx in -30, -10, 10, 30) { [V18]::Hover(($n1[0] + $dx), $n1[1]) }
Start-Sleep -Milliseconds 400
$压着 = 此刻读数
按键 '^{RIGHT}'
"     鼠标还压在轨 01 的音上时，读数报的是：$压着（悬浮优先）"
"     挪开鼠标之后（= 选中集）：$(选中读数)"
断言 '悬浮之后 Ctrl+→ 仍在' (选中轨) '轨 03'
""

# ---------- 10. 点该轨空白也要挪焦点 ----------
# 这条和「点音符」走的是同一句 SetFocusedTrack（摆在捕获之后、分支之前），
# 但值得单独走一遍 —— 万一哪天有人把那句话挪进「命中了音符」那个分支里，只有这条会响
"10) 点轨 01 的空白处（不是音符）—— 焦点也要跟过去"
$b1 = 找音符 1 -Blank
"     轨 01 的空白点在 ($($b1[0]), $($b1[1]))"
# 82 号：实机里这一下被吞过一次（点完读数跟点之前一字不差）—— 所以走**点准**：
# 读数没动就原样补点一次，补完还不算数，下面那条断言照红。
if (-not (点准 $b1[0] $b1[1])) { throw "点空白点了两次，读数都没动（$([V18]::At($b1[0],$b1[1]))）—— 不猜，直接停" }
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
# 这一下**不走 点准**：Shift 点同一个音有可能是「再加选一次 = 反而取消」，
# 补点一下就可能把刚选上的音又摘掉 —— 那会把这条判据自己弄坏。
# 挡在前面的还是 拽 的有界重试（前台拽不回来就不点），比补点稳。
$落对 = 点 $n1b[0] $n1b[1]
[V18]::Shift($false)
if (-not $落对) { throw "点下去被别的窗口挡了 —— ($($n1b[0]),$($n1b[1])) 上压着 $([V18]::At($n1b[0],$n1b[1]))" }
"     点完读数：$(选中读数)"
按键 '^{RIGHT}'
"     再 Ctrl+→：$(选中读数)"
断言 'Shift 加选之后 Ctrl+→ 落在' (选中轨) '轨 01'
""

if ($补点 -gt 0) {
  # 这一行是**证据**，不是判据：补过点说明这一趟环境抖了一下（见 点准 那一段）。
  # 补点之后那几条断言仍然照原样判 —— 补点也压不住的时候，它们就是红的。
  "（这一趟有 $补点 次点击被吞掉、原样补点过 —— 脚手架侧的抖动，判据没放宽）"
}
if ($fail -eq 0) { "全过" } else { "$fail 条没过" }
# 81 号票：裁决行 + 退出码。裁决行是给 run-all.ps1 复核用的记号（它拿这行跟退出码对，
# 对不上就把这一条降级成红）—— 少了它，这条脚本在总表里会被当成「没有裁决」而**降级成红**。
"==== uitest 裁决 不过=$fail"
exit $fail
