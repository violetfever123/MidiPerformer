# 21 号工单：读数栏合并（悬浮优先、选中兜底）+ 位置读数搬到导航条 + 删时长 + 删「没有循环」。
#
# 判据分三层，越往下越接近「这张工单的正题」：
#   · **哪几个字在屏幕上** —— 悬停/选中两套标签没了、时长没了、没有循环没了。UIA 直接读得到。
#   · **位置在哪儿** —— 「位置」必须在「跳到」左边、和它同一行；走带条上不许再有第二个。
#     这一层只能靠坐标，写成坐标断言。
#   · **合并的行为** —— 悬浮时显示悬浮的音，鼠标一移开**回落到选中那个音**（不是变空）。
#     这是「合并」的直接兑现，也是最容易做成「移开就变空」的一条，所以单开一节量。
#
# 前置（脚本自己不做）：app 开着、曲库里那一首 Carulli 已经载入
#（.scratch/load-song.ps1 就是干这个的）。
#
# 音符的像素位置由脚本**自己截图扫出来**，不写死坐标：窗口挪过、曲子换了、
# 以后标尺高度改了（24 号就在改标尺），写死的坐标会静悄悄地指到空白处 ——
# 于是「悬停没反应」看起来像功能坏了，而其实是脚本指错了地方。
#
# 用法: pwsh -NoProfile -File verify-21.ps1
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Windows.Forms, UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public class V21 {
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
  // 合成按键 / 合成鼠标都要求目标窗口在前台，拽一次要连 Alt 一起打（和 verify-19/20 同一套）
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
    SetFocus(h); System.Threading.Thread.Sleep(300);
    return GetForegroundWindow() == h;
  }
  public static void Move(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(280); }
  public static void Click(int x, int y) {
    SetCursorPos(x, y); System.Threading.Thread.Sleep(140);
    mouse_event(0x0002, 0,0,0, IntPtr.Zero); mouse_event(0x0004, 0,0,0, IntPtr.Zero);
    System.Threading.Thread.Sleep(340);
  }
}
"@

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$C = { param($t) New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $t) }

$p = Get-Process -Name MidiPerformer -EA SilentlyContinue |
  Where-Object { $_.MainWindowTitle -eq 'MIDI 演奏器' } | Select-Object -First 1
if (-not $p) { throw 'MidiPerformer 没在跑' }
$h = $p.MainWindowHandle
$root = $AE::FromHandle($h)
$win = $root.Current.BoundingRectangle

$fail = 0
function 断言([string]$名字, $实际, $期望) {
  if ("$实际" -eq "$期望") { "  OK   $名字 = $实际" }
  else { "  FAIL $名字 = $实际（期望 $期望）"; $script:fail++ }
}
function 断言真([string]$名字, [bool]$条件, [string]$原文) {
  if ($条件) { "  OK   $名字（$原文）" } else { "  FAIL $名字（$原文）"; $script:fail++ }
}

function 全部([object]$类型) { @($root.FindAll($TS::Descendants, (& $C $类型))) }
function 取文本 { 全部 $CT::Text }
function 按编号([string]$id) { 全部 $CT::Button | Where-Object { $_.Current.AutomationId -eq $id } | Select-Object -First 1 }
function 找文本([string]$名字) { 取文本 | Where-Object { $_.Current.Name -eq $名字 } | Select-Object -First 1 }

$播放键 = 按编号 'PlayButton'
if (-not $播放键) { throw '找不到 PlayButton' }
if (-not $播放键.Current.IsEnabled) { throw '还没载入曲子 —— 先跑 .scratch/load-song.ps1' }

function 前台 { if (-not [V21]::Take($h)) { throw '拽不到前台 —— 台面上有别的窗口压着' } }

# ---------- 读数条那一条在哪儿 ----------
#
# 拿**右边那句快捷键提示**定位。它是这一条里唯一一个「不管有没有悬停/选中都在」的长文本，
# 用它当锚，才能在「标签连值都藏起来」的时候也问得出「读数条现在在哪一行」——
# 而「藏起来之后读数条还在不在原来的位置」正是这张工单的一条验收。
function 读数条Y {
  $a = 取文本 | Where-Object { $_.Current.Name -like '空格 播放*' } | Select-Object -First 1
  if ($a) { [int]$a.Current.BoundingRectangle.Y } else { -1 }
}

# 一行里某个标签右边紧挨着那格值。Y 给 ±14 的容差（字体的基线在不同控件上差几个像素），
# X 只往右找 90px 以内 —— 读数条上「键」和「值」就是这个间距。
function 取格([string]$标签, [double]$带Y) {
  $t = 取文本
  $lbl = $t | Where-Object {
    $_.Current.Name -eq $标签 -and [Math]::Abs($_.Current.BoundingRectangle.Y - $带Y) -lt 14
  } | Select-Object -First 1
  if (-not $lbl) { return '(缺)' }
  $lr = $lbl.Current.BoundingRectangle
  $v = $t | Where-Object {
    $r = $_.Current.BoundingRectangle
    [Math]::Abs($r.Y - $lr.Y) -lt 14 -and $r.X -gt $lr.X -and $r.X -lt ($lr.X + 90)
  } | Sort-Object { $_.Current.BoundingRectangle.X } | Select-Object -First 1
  if ($v) { $v.Current.Name } else { '(读不到)' }
}

function 读数([string]$标签) { 取格 $标签 (读数条Y) }

# 读数条上有没有这个标签（量「标签连值一起藏起来了」靠它）
function 读数条上有([string]$标签) {
  $y = 读数条Y
  if ($y -lt 0) { return $false }
  @(取文本 | Where-Object {
      $_.Current.Name -eq $标签 -and [Math]::Abs($_.Current.BoundingRectangle.Y - $y) -lt 14
    }).Count -gt 0
}

function 有这句话([string]$片段) {
  @(取文本 | Where-Object { $_.Current.Name -like "*$片段*" }).Count -gt 0
}

# ---------- 找音符的像素位置 ----------
#
# 音符是深底上的浅灰横条：扫亮度、找够长的横向连续段就能认出来。
# 只看第一条轨的卷帘，上下界由「01」和「02」两个轨头文本夹出来 —— 又一处不写死坐标。
function 找音符 {
  $e1 = 找文本 '01'; $e2 = 找文本 '02'
  if (-not $e1 -or -not $e2) { throw '找不到轨头 01 / 02' }

  $bmp = New-Object System.Drawing.Bitmap([int]$win.Width, [int]$win.Height)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen([int]$win.X, [int]$win.Y, 0, 0, $bmp.Size)
  $g.Dispose()

  # +110：跳过轨头那一排**和标尺**。标尺上那四个小节号也是亮的，
  # 而且「1」这种字的连续段够长，会被当成一个音符 —— 那就会悬停到 ruler 上，什么都读不到。
  $top = [int]($e1.Current.BoundingRectangle.Y - $win.Y) + 110
  $bot = [int]($e2.Current.BoundingRectangle.Y - $win.Y) - 14
  $left = [int]($e1.Current.BoundingRectangle.X - $win.X) + 4
  $right = [int]($win.Width) - 20

  $行 = @()
  for ($y = $top; $y -lt $bot; $y += 2) {
    $最好 = 0; $最好X = -1; $连 = 0; $起 = -1
    for ($x = $left; $x -lt $right; $x += 2) {
      $c = $bmp.GetPixel($x, $y)
      # 阈值只能取 60：音符的亮度（~85-105）和**竖向小节线**（~83）是重叠的，
      # 靠亮度分不开。分开它们的是**宽度** —— 小节线是竖的，在任何一行上只有 2px 宽，
      # 而音符是横的。所以这里阈值取低，真正干活的是下面那条「连续段 ≥ 24px」。
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

  # 相邻的行并成一条「带」（一个音高一条带），带里取最长的那一段的中点当落点
  $并 = @()
  foreach ($b in $行) {
    if ($并.Count -gt 0 -and ($b.Y - $并[-1].末) -le 4) {
      $m = $并[-1]; $m.末 = $b.Y; $m.行数++
      if ($b.宽 -gt $m.宽) { $m.宽 = $b.宽; $m.X = $b.X; $m.Y = $b.Y }
    } else {
      $并 += @{ Y = $b.Y; 末 = $b.Y; X = $b.X; 宽 = $b.宽; 行数 = 1 }
    }
  }
  # 只留够厚的带：网格线不会有 24px 连续，杂散的一两行也不算
  return @($并 | Where-Object { $_.行数 -ge 3 } | ForEach-Object {
      @{ X = [int]($win.X + $_.X + $_.宽 / 2); Y = [int]($win.Y + $_.Y) }
    })
}

# ---------- 0. 找两个音高不同的音 ----------
"=== 0. 前置 ==="
前台
$音 = @(找音符)
"  扫到 $($音.Count) 条音符带"
if ($音.Count -lt 2) { throw "扫出来的音符不到两条 —— 卷帘上是不是没音？" }
$甲 = $音[0]
# 取和甲 Y 差得最开的那条：Y 差得开 = 音高不同，才分得清
# 「读数跟着换了」和「读数压根没动」
$乙 = $音 | Sort-Object { -[Math]::Abs($_.Y - $甲.Y) } | Select-Object -First 1
"  甲 @ $($甲.X),$($甲.Y)   乙 @ $($乙.X),$($乙.Y)（Y 差 $([Math]::Abs($乙.Y - $甲.Y))px）"
if ([Math]::Abs($乙.Y - $甲.Y) -lt 6) { throw '两个音挨太近，分不出音高差别' }

# 空白处：左面板（曲库那一带），离卷帘足够远
$空白X = [int]($win.X + 60); $空白Y = [int]($win.Y + $win.Height * 0.6)

# ---------- 1. 载入之后：不悬浮也不选中 → 整块藏起来 ----------
"`n=== 1. 不悬浮也不选中：整块藏起来 ==="
[V21]::Move($空白X, $空白Y)
断言真 '没有悬停时读数条上没有「轨」' (-not (读数条上有 '轨')) '标签连值一起藏'
断言真 '没有悬停时读数条上没有「音高」' (-not (读数条上有 '音高')) '标签连值一起藏'
$高_无 = $播放键.Current.BoundingRectangle.Y
$y_无 = 读数条Y
"  读数条 Y = $y_无，走带条 Y = $([int]$高_无)"

# ---------- 2. 悬浮一个音 → 一套读数，且这一条不跳 ----------
"`n=== 2. 悬浮：一套读数（轨/音高/小节/拍位/时值）==="
[V21]::Move($甲.X, $甲.Y)
foreach ($标签 in @('轨','音高','小节','拍位','时值')) {
  断言真 "悬浮时有「$标签」" (读数条上有 $标签) '合起来之后轨号是「这个音在哪条轨」的唯一线索'
}
断言真 '「悬停」这个标签没了' (-not (读数条上有 '悬停')) '两套合成一套'
断言真 '「选中」这个标签没了' (-not (读数条上有 '选中')) '两套合成一套'
$高_有 = $播放键.Current.BoundingRectangle.Y
$y_有 = 读数条Y
断言 '读数条高度不变（走带条没跳）' $高_有 $高_无
断言 '读数条自己那一行也没挪' $y_有 $y_无
"  悬浮甲 → 轨 $(读数 '轨') / 音高 $(读数 '音高') / 小节 $(读数 '小节') / 拍位 $(读数 '拍位') / 时值 $(读数 '时值')"

# ---------- 3. 移开（还没有选中）→ 又藏起来 ----------
"`n=== 3. 移开、又没有选中 → 整块藏回去 ==="
[V21]::Move($空白X, $空白Y)
断言真 '移开之后读数条上没有「音高」' (-not (读数条上有 '音高')) '没有选中的音可回落，就藏起来'
断言 '藏起来之后读数条还是那一行' (读数条Y) $y_无

# ---------- 4. 选中一个音之后，移开回落到它 ----------
"`n=== 4. 移开音符 → 回落到选中的音（不是变空）==="
[V21]::Click($甲.X, $甲.Y)
$甲音高 = 读数 '音高'
"  点中甲之后 → 音高 $甲音高"
断言真 '点中甲之后读数条有内容' (($甲音高 -ne '(缺)') -and ($甲音高 -ne '(读不到)')) "音高 = $甲音高"

[V21]::Move($乙.X, $乙.Y)
$乙音高 = 读数 '音高'
"  悬浮到乙 → 音高 $乙音高"
断言真 '悬浮时读数跟着悬浮那个音走' ($乙音高 -ne $甲音高) "甲 $甲音高 → 乙 $乙音高"

[V21]::Move($空白X, $空白Y)
$回落 = 读数 '音高'
"  移开之后 → 音高 $回落"
断言 '移开之后回落到选中的甲' $回落 $甲音高
断言真 '移开之后读数条还在' (读数条上有 '音高') '选中兜底 —— 这正是「合并」要兑现的那一条'
断言 '移开之后高度也没变' $播放键.Current.BoundingRectangle.Y $高_无

# ---------- 5. 位置读数搬到导航条 ----------
"`n=== 5. 位置读数在「跳到」左边 ==="
$位置 = 找文本 '位置'
$跳到 = 找文本 '跳到'
$位置们 = @(取文本 | Where-Object { $_.Current.Name -eq '位置' })
断言 '全窗只有一处「位置」' $位置们.Count 1
if ($位置 -and $跳到) {
  $pr = $位置.Current.BoundingRectangle; $jr = $跳到.Current.BoundingRectangle
  断言真 '「位置」在「跳到」左边' ($pr.X -lt $jr.X) "位置 X=$([int]$pr.X) < 跳到 X=$([int]$jr.X)"
  断言真 '「位置」和「跳到」同一行' ([Math]::Abs($pr.Y - $jr.Y) -lt 14) '同一行 = 在导航条上'
  断言真 '「位置」在走带条上方' ($pr.Y -lt $播放键.Current.BoundingRectangle.Y) '走带条上那个位置栏已经删了'
}
$前 = 取格 '位置' $位置.Current.BoundingRectangle.Y
"  现在位置 = $前"
$框 = 全部 $CT::Edit | Where-Object { $_.Current.AutomationId -eq 'JumpBox' } | Select-Object -First 1
if ($框) {
  前台
  [void]$框.SetFocus(); Start-Sleep -Milliseconds 250
  $框.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('7')
  Start-Sleep -Milliseconds 250
  [System.Windows.Forms.SendKeys]::SendWait('{ENTER}'); Start-Sleep -Milliseconds 800
  $后 = 取格 '位置' $位置.Current.BoundingRectangle.Y
  "  跳到第 7 小节之后 = $后"
  断言真 '位置读数跟着走' ($后 -ne $前) "$前 → $后"
  断言真 '位置显示的是小节号' ($后 -match '^\s*7\s*/') '第 7 小节 / 124 小节'
}

# ---------- 6. 走带条：位置搬走、时长删掉 ----------
"`n=== 6. 走带条 ==="
$走带Y = $播放键.Current.BoundingRectangle.Y
断言 '走带条上没有「位置」' (@(取文本 | Where-Object {
    $_.Current.Name -eq '位置' -and [Math]::Abs($_.Current.BoundingRectangle.Y - $走带Y) -lt 20
  }).Count) 0
断言真 '全窗都没有「时长」两个字' (-not (有这句话 '时长')) '标签和值一起删'

# ---------- 7. 视图范围文字没了 ----------
"`n=== 7. 视图范围文字（第 X–Y 小节 / 共 N）==="
断言 '全窗没有「第 X–Y 小节」这种文字' (@(取文本 | Where-Object {
    $_.Current.Name -match '第\s*\d+\s*[–-]\s*\d+\s*小节'
  }).Count) 0

# ---------- 8. 走带条右边那句话 ----------
"`n=== 8. 「没有循环」删了，安全说明留着 ==="
断言真 '「没有循环」四个字没了' (-not (有这句话 '没有循环')) '那句是给外人解释设计取舍的'
断言真 '「放完自动停止并松开所有按键」还留着' (有这句话 '放完自动停止并松开所有按键') '这句说的是不卡键，真有用'

"`n$(if ($fail -eq 0) { '全过' } else { "$fail 条不过" })"
# 81 号票：裁决行 —— run-all.ps1 拿它跟退出码复核，对不上就把这一条降级成红（别删）。
"==== uitest 裁决 不过=$fail"
exit $fail
