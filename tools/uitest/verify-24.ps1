# 24 号工单：卷帘标尺上的小节数字看得清了。
#
# 三样一起改（字号 10→12、InkFaint→Ink、加粗），外加 RulerHeight 18→22。
# 判据分两层：
#
#   · **字本身** —— 亮度和笔画粗细。这一层的好处是不需要「改之前」的照片：
#     亮度直接对着**令牌的值**比（暗色主题下 Ink=#E7EBF1 亮度 234、InkFaint=#6E7987 亮度 119），
#     落在 119 那一档就是没改，落在 234 那一档就是改了。加粗看笔画宽度（200% 缩放下
#     12px 的粗体竖笔约 5 个物理像素，常规体约 3 个）。
#   · **几何跟着走了** —— `RulerHeight` 是几何常量，被 PlotHeight / YAtPitch / PitchAtY /
#     框选带子 / beat 线起点 / 每条轨的卷帘高度一起消费。这条不写新断言，而是**对着一份旧数字比对**：
#     21 号那几轮跑在改之前的二进制上，量到轨头「01」和「02」的 Y 差是 **612**（物理像素）。
#     标尺 +4 逻辑像素 = +8 物理像素，所以现在应当是 **620**。差多少就是改了多少。
#   · **对齐没歪** —— 音符是脚本自己截图扫出来的（不写死坐标），扫到之后悬停上去，
#     读数条必须读出那个音。扫和点是两套坐标（像素扫描 vs 命中测试），对得上就说明没偏。
#
# 前置（脚本自己不做）：app 开着、曲库里那一首 Carulli 已经载入
#（.scratch/load-song.ps1 就是干这个的）。
#
# 用法: pwsh -NoProfile -File verify-24.ps1
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public class V24 {
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
  public static void Down(int x, int y) {
    SetCursorPos(x, y); System.Threading.Thread.Sleep(200);
    mouse_event(0x0002, 0,0,0, IntPtr.Zero); System.Threading.Thread.Sleep(260);
  }
  public static void DragTo(int x, int y) {
    SetCursorPos(x, y); System.Threading.Thread.Sleep(300);
  }
  public static void Up() {
    mouse_event(0x0004, 0,0,0, IntPtr.Zero); System.Threading.Thread.Sleep(320);
  }
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
# 条件脚本块。**名字不要图省事叫 $C** —— PowerShell 变量名不区分大小写，
# 底下扫像素时随手写的 `$c = $bmp.GetPixel(...)` 会把它整个盖掉，
# 之后每次 `& $条件` 都变成「拿一个 Color 当命令跑」。第一版就是这么死的：
# 第 2 节量完数字、第 3 节一进来 `找文本` 就报「术语 'Color [A=255, R=30, G=42, B=58]' 不会被识别」。
$条件 = { param($t) New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $t) }

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
function 全部([object]$类型) { @($root.FindAll($TS::Descendants, (& $条件 $类型))) }
function 取文本 { 全部 $CT::Text }
function 找文本([string]$名字) { 取文本 | Where-Object { $_.Current.Name -eq $名字 } | Select-Object -First 1 }
function 按编号([string]$id) { 全部 $CT::Button | Where-Object { $_.Current.AutomationId -eq $id } | Select-Object -First 1 }

$播放键 = 按编号 'PlayButton'
if (-not $播放键 -or -not $播放键.Current.IsEnabled) { throw '还没载入曲子 —— 先跑 .scratch/load-song.ps1' }
if (-not [V24]::Take($h)) { throw '拽不到前台' }

function 截图 {
  $bmp = New-Object System.Drawing.Bitmap([int]$win.Width, [int]$win.Height)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen([int]$win.X, [int]$win.Y, 0, 0, $bmp.Size)
  $g.Dispose()
  $bmp
}
function 亮度($c) { 0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B }

# ---------- 1. 轨头的间距（量标尺 +4 有没有传导出去）----------
#
# 21 号那几轮跑在改之前的二进制上，量到的是 612。这不是估的，是脚本自己打出来的旧数字。
"=== 1. 轨头 01 / 02 的间距（改之前是 612）==="
$t1 = 找文本 '01'; $t2 = 找文本 '02'
if (-not $t1 -or -not $t2) { throw '找不到轨头 01 / 02' }
$y1 = [int]$t1.Current.BoundingRectangle.Y; $y2 = [int]$t2.Current.BoundingRectangle.Y
$距 = $y2 - $y1
"  01 @ Y=$y1   02 @ Y=$y2   间距 = $距"
断言 '每条轨的卷帘高度 +4 逻辑像素（= +8 物理像素）' $距 620

# ---------- 2. 标尺上那排小节数字 ----------
#
# 标尺就在轨头正下方。**这里有两个坑，都踩过。**
#
# 坑一：UIA 的 BoundingRectangle 是**屏幕**坐标，位图的 (0,0) 是窗口左上角 ——
# 拿屏幕 Y 直接去 GetPixel 会量到别的地方（第一版就量到了音符块，于是「一个亮像素都没有」）。
# 凡是拿 UIA 坐标取像素，一律先减 $win.X / $win.Y。
#
# 坑二：**别写死「亮字」**。产品是暗色（`App.axaml` 里 `RequestedThemeVariant="Dark"`），
# 但这张单要的正是「两个主题下都看得清」。所以底色在数字上方两行现取，
# 由它决定这轮是深底亮字还是浅底深字 —— 同一份脚本两个主题都能跑，方向不写死。
#
# 判据不靠「改之前的照片」，靠**令牌本身的值**（拿同一把尺从十六进制算出来，不手抄）：
#   · 暗色 Ink #E7EBF1 → 234 ／ InkFaint #6E7987 → 119
#   · 亮色 Ink #171C23 →  27 ／ InkFaint #8A94A1 → 147
# 量出来的均值**离 Ink 近、离 InkFaint 远** = 用的是 Ink。没改的话正好反过来。
"`n=== 2. 标尺上的小节数字 ==="
$分界 = [int]($t1.Current.BoundingRectangle.Y + $t1.Current.BoundingRectangle.Height - $win.Y)
$左 = [int]($t1.Current.BoundingRectangle.X - $win.X) + 4
$右 = [int]($win.Width) - 20
$bmp = 截图

function 令牌亮度([string]$hex) {
  $v = [Convert]::ToInt32($hex, 16)
  亮度 ([System.Drawing.Color]::FromArgb(($v -shr 16) -band 0xFF, ($v -shr 8) -band 0xFF, $v -band 0xFF))
}
$票 = $null
$暗票 = @{ 墨 = 令牌亮度 'E7EBF1'; 淡 = 令牌亮度 '6E7987' }
$明票 = @{ 墨 = 令牌亮度 '171C23'; 淡 = 令牌亮度 '8A94A1' }

$底 = 0.0; $n = 0
for ($x = $左; $x -lt $右; $x += 7) { $底 += 亮度 $bmp.GetPixel($x, $分界 + 2); $n++ }
$底 = $底 / $n
$深底 = $底 -lt 128          # 深色底 —— 这个主题下「墨」比底亮
$向 = if ($深底) { 1 } else { -1 }   # 墨点离 128 往哪个方向偏
$票 = if ($深底) { $暗票 } else { $明票 }
$主题 = if ($深底) { "暗色（底色 $([int]$底)，深底亮字）" } else { "亮色（底色 $([int]$底)，浅底深字）" }
"  这一轮是 $主题"

$带 = @()
for ($y = $分界 + 4; $y -lt $分界 + 90; $y++) {
  $数 = 0
  for ($x = $左; $x -lt $右; $x++) {
    if (((亮度 $bmp.GetPixel($x, $y)) - 128) * $向 -gt 0) { $数++ }
  }
  $带 += ,@{ Y = $y; 数 = $数 }
}
# 只认**最前面那一段连续的行**：数字是一排字，落在连着的一二十行里。
# 后面再出现的成片墨点是音符 —— 亮色主题下音符 #4A5563 亮度 83，也在 128 的「暗」侧，
# 会被一起判成墨；不掐掉的话字高会被撑大，断言就成了摆设。
$起 = -1
for ($i = 0; $i -lt $带.Count; $i++) { if ($带[$i].数 -ge 5) { $起 = $i; break } }
if ($起 -lt 0) { $bmp.Dispose(); throw "轨头底下那一带（$主题）一个墨点都没有 —— 小节数字没画出来？" }
$末 = $起
while (($末 + 1) -lt $带.Count -and $带[$末 + 1].数 -ge 5) { $末++ }

$上 = $带[$起].Y; $下 = $带[$末].Y
"  字所在的行（位图 y）：$上 .. $下（高 $($下 - $上 + 1) 物理像素）"
foreach ($r in $带[$起..$末]) { "    y=$($r.Y)  墨点 $($r.数)" }

$总 = 0; $和 = 0.0; $最宽 = 0
for ($y = $上; $y -le $下; $y++) {
  $连 = 0
  for ($x = $左; $x -lt $右; $x++) {
    $点 = $bmp.GetPixel($x, $y)
    if (((亮度 $点) - 128) * $向 -gt 0) {
      $总++; $和 += 亮度 $点; $连++
      if ($连 -gt $最宽) { $最宽 = $连 }
    } else { $连 = 0 }
  }
}
$bmp.Dispose()
$均 = if ($总 -gt 0) { [int]($和 / $总) } else { 0 }
"  墨点 $总 个，平均亮度 $均，最宽连续段 $最宽 物理像素"
"  令牌：Ink = $([int]$票.墨) ／ InkFaint = $([int]$票.淡)"

断言真 '小节数字用的是 Ink，不是 InkFaint' `
  ([Math]::Abs($均 - $票.墨) -lt [Math]::Abs($均 - $票.淡)) `
  "$主题 均值 $均，离 Ink 差 $([Math]::Abs($均 - $票.墨))、离 InkFaint 差 $([Math]::Abs($均 - $票.淡))"
断言真 '字高对得上 12px 字号（200% 缩放下约 17~24 物理像素；10px 只有 14）' `
  ((($下 - $上 + 1) -ge 15) -and (($下 - $上 + 1) -le 26)) "实测 $($下 - $上 + 1) 物理像素"
断言真 '笔画够粗（加粗：竖向笔画约 5 物理像素，常规体约 3）' `
  ($最宽 -ge 4) "最宽连续段 $最宽 物理像素"

# ---------- 3. 对齐：扫出来的音符点上去，读数条要读出它 ----------
#
# 扫是像素坐标、点是命中测试，两套对得上才说明 RulerHeight 改了之后两边没分家。
#
# 阈值**也跟着主题走**（第一版写死 >60，那是暗色专用的）：暗色音符 #5E6B7A 亮度 105、底 40，
# 60 夹在中间；亮色音符 #4A5563 亮度 83、底 233，得往另一头取。
# 真正干活的其实不是阈值而是**宽度** —— 音符是横的，小节线是竖的（任何一行上只有 2px），
# 所以阈值取松、靠「连续段 ≥ 24px」分辨。
"`n=== 3. 音符位置和命中测试还对得上 ==="
$e1 = 找文本 '01'; $e2 = 找文本 '02'
$bmp2 = 截图
# +110 跳过轨头那一排**和标尺**（标尺上的小节号现在更浓了，更要跳过）
$top = [int]($e1.Current.BoundingRectangle.Y - $win.Y) + 110
$bot = [int]($e2.Current.BoundingRectangle.Y - $win.Y) - 14
$音符阈值 = if ($深底) { 60 } else { 150 }
$行 = @()
for ($y = $top; $y -lt $bot; $y += 2) {
  $最好 = 0; $最好X = -1; $连 = 0; $起2 = -1
  for ($x = $左; $x -lt $右; $x += 2) {
    $点 = $bmp2.GetPixel($x, $y)
    $是音符底 = if ($深底) { (亮度 $点) -gt $音符阈值 } else { (亮度 $点) -lt $音符阈值 }
    if ($是音符底) { if ($连 -eq 0) { $起2 = $x }; $连 += 2 }
    else { if ($连 -gt $最好) { $最好 = $连; $最好X = $起2 }; $连 = 0 }
  }
  if ($连 -gt $最好) { $最好 = $连; $最好X = $起2 }
  if ($最好 -ge 24) { $行 += ,@{ Y = $y; X = $最好X; 宽 = $最好 } }
}
$bmp2.Dispose()
$并 = @()
foreach ($b in $行) {
  if ($并.Count -gt 0 -and ($b.Y - $并[-1].末) -le 4) {
    $m = $并[-1]; $m.末 = $b.Y; $m.行数++
    if ($b.宽 -gt $m.宽) { $m.宽 = $b.宽; $m.X = $b.X; $m.Y = $b.Y }
  } else { $并 += @{ Y = $b.Y; 末 = $b.Y; X = $b.X; 宽 = $b.宽; 行数 = 1 } }
}
$音 = @($并 | Where-Object { $_.行数 -ge 3 } | ForEach-Object {
    @{ X = [int]($win.X + $_.X + $_.宽 / 2); Y = [int]($win.Y + $_.Y) } })
"  扫到 $($音.Count) 条音符带"
断言真 '改完标尺还扫得到音符' ($音.Count -ge 2) "扫到 $($音.Count) 条"

$甲 = $音[0]
[V24]::Move($甲.X, $甲.Y)
# 读数条那一套（锚在右边那句快捷键提示上，和 verify-21/31 同一个锚）
function 读一套 {
  $t = @(取文本)
  $锚 = $t | Where-Object { $_.Current.Name -like '空格 播放*' } | Select-Object -First 1
  $o = [ordered]@{ 可见 = $false; 轨 = '(缺)'; 音高 = '(缺)'; 小节 = '(缺)'; 拍位 = '(缺)'; 时值 = '(缺)' }
  if (-not $锚) { return $o }
  $y = [int]$锚.Current.BoundingRectangle.Y
  foreach ($标签 in @('轨', '音高', '小节', '拍位', '时值')) {
    $lbl = $t | Where-Object { $_.Current.Name -eq $标签 -and [Math]::Abs($_.Current.BoundingRectangle.Y - $y) -lt 14 } | Select-Object -First 1
    if (-not $lbl) { continue }
    $o.可见 = $true
    $lr = $lbl.Current.BoundingRectangle
    $v = $t | Where-Object {
      $r = $_.Current.BoundingRectangle
      [Math]::Abs($r.Y - $lr.Y) -lt 14 -and $r.X -gt $lr.X -and $r.X -lt ($lr.X + 90)
    } | Sort-Object { $_.Current.BoundingRectangle.X } | Select-Object -First 1
    $o[$标签] = if ($v) { $v.Current.Name } else { '(读不到)' }
  }
  $o
}
$读 = 读一套
"  悬停到扫出来的第一个音 → 轨 $($读['轨']) / 音高 $($读['音高']) / 小节 $($读['小节']) / 拍位 $($读['拍位']) / 时值 $($读['时值'])"
断言真 '扫出来的坐标点上去，命中测试认得那个音' $读.可见 '像素扫描和命中测试没分家'
断言真 '读出来的是个真音高（不是破折号）' (($读['音高'] -ne '(缺)') -and ($读['音高'] -ne '(读不到)')) "音高 = $($读['音高'])"

# ---------- 4. 标尺底线 + 框选带子顶边 ----------
#
# 工单里这两条都写着「跟着新的 RulerHeight 对齐」。代码上看它们确实共用同一个常量
#（底线画在 `RulerHeight + 0.5`，带子 `MarqueeRect.Y = RulerHeight`）—— 但那是**写出来的**。
# 这一节真去量：底线的颜色和位置、以及真拖一次框选带子看它的顶边落在哪。
#
# 顺带白捡一条：**由底线反推轨顶，再量数字离轨顶多远**，可以独立验证
# 「12px 的字在 22px 标尺里居中」—— 代码里的算式是 `(RulerHeight - RulerFontSize) / 2`，
# 也就是 (22−12)/2 = 5 逻辑像素 = 10 物理像素。量出来对得上，说明这两个数没写岔。
"`n=== 4. 标尺底线 + 框选带子顶边 ==="
$bmp3 = 截图
$InkFaint亮 = $票.淡
$底线行 = @()
for ($y = $上; $y -lt ($上 + 60); $y++) {
  $中 = 0; $计 = 0
  for ($x = $左; $x -lt $右; $x += 3) {
    $计++
    if ([Math]::Abs((亮度 $bmp3.GetPixel($x, $y)) - $InkFaint亮) -le 25) { $中++ }
  }
  if ($中 -ge ($计 * 0.9)) { $底线行 += $y }
}
"  标尺底线落在位图 y：$(if ($底线行.Count) { ($底线行 -join ', ') } else { '(没找到)' })"
断言真 '标尺底线找得到，而且是**一整条**横线' ($底线行.Count -ge 1 -and $底线行.Count -le 4) "占了 $($底线行.Count) 行"
断言真 '底线用的是 InkFaint（比原来的 Line 实一档）' ($底线行.Count -ge 1) `
  "令牌 InkFaint = $([int]$InkFaint亮)（原来的 Line 暗色是 58、亮色是 205，都不在这一档）"

if ($底线行.Count -ge 1) {
  $线 = $底线行[0]
  $轨顶 = $线 - 44                      # 底线画在 lane 局部 RulerHeight+0.5 → 22 逻辑 = 44 物理
  # 该量的是「数字落在标尺带里」，不是「数字居中于标尺」—— 这两个不是一回事。
  # 代码算的是 `(RulerHeight - RulerFontSize) / 2` 给 `DrawText` 的**行盒顶**，
  # 而行盒顶不等于字形墨迹顶：12px 的字在 200% 下墨迹还要再往下沉约 9 物理像素。
  # 第一版按行盒算式断言「应当 10」，红了 —— **是期望写错了，不是代码错了**。
  #（顺带量到一条：墨迹中心比标尺中心低约 5 物理像素。行盒居中的必然结果，不好看也谈不上碍事。）
  $字距 = $上 - $轨顶
  $心差 = (($上 + $下) / 2) - ($轨顶 + 22)
  "  由底线反推：轨顶 = $轨顶，标尺 169..$线，数字墨迹 $上..$下（离轨顶 $字距，中心比标尺中心低 $([int]$心差)）"
  断言真 '数字整个落在标尺带里（没溢出到轨头，也没压过底线）' `
    (($上 -gt $轨顶) -and ($下 -lt $线)) "轨顶 $轨顶 < 字顶 $上 ，字底 $下 < 底线 $线"
  断言真 '数字墨迹没有贴着标尺上下沿（上下各留得出空）' `
    ((($上 - $轨顶) -ge 4) -and (($线 - $下) -ge 4)) "上留 $($上 - $轨顶)、下留 $($线 - $下) 物理像素"
}

# 框选带子：在轨 1 的卷帘里找一处**空白**按下（按到音符上会变成拖动，那是另一条路），
# 往左上拖出一段，趁松手前截图，找带子左右两条实边（palette.Accent，全不透明，最好认）。
$e2y = [int]($e2.Current.BoundingRectangle.Y - $win.Y)
$按X = $左 + 40
$按Y = -1
for ($y = ($e2y - 26); $y -gt ($上 + 8); $y--) {
  $l = 亮度 $bmp3.GetPixel($按X, $y)
  $空 = if ($深底) { $l -lt 70 } else { $l -gt 180 }
  if ($空) { $按Y = $y; break }
}
$bmp3.Dispose()
"  空白按下点（位图）：x=$按X y=$按Y"
if ($按Y -lt 0) { throw "轨 1 的卷帘里没找到空白处，框选这一节做不了" }

[V24]::Down([int]($win.X + $按X), [int]($win.Y + $按Y))
[V24]::DragTo([int]($win.X + $按X + 200), [int]($win.Y + $按Y - 90))
[V24]::DragTo([int]($win.X + $按X + 420), [int]($win.Y + $按Y - 190))
$bmp4 = 截图
$栏 = @()
# 带子实边的签名只有一个条件：**B 比 R 高出 60 以上**。
# 暗色 Accent #74ABDD 差 105、亮色 Accent #2C6BA8 差 124 —— 两个主题都够；
# 而底（暗 28 / 亮 10）、音符（暗 28 / 亮 25）、播放头 Stop（暗 −96）全在外面。
# 第一版还加了「亮度 > 110」，那是暗色专用的：亮色 Accent 亮度只有 95，会把带子整条漏掉。
for ($x = $左; $x -lt $右; $x++) {
  $顶 = -1; $底 = -1; $数 = 0
  for ($y = $上; $y -lt $e2y; $y++) {
    $p = $bmp4.GetPixel($x, $y)
    if (($p.B - $p.R) -gt 60) { if ($顶 -lt 0) { $顶 = $y }; $底 = $y; $数++ }
  }
  if ($数 -ge 20) { $栏 += ,@{ X = $x; 顶 = $顶; 底 = $底 } }
}
$bmp4.Dispose()
[V24]::Up()
if ($栏.Count -lt 2) { throw "没找到框选带子的两条实边（只找到 $($栏.Count) 条竖线）" }
$带顶 = ($栏 | ForEach-Object { $_.顶 } | Measure-Object -Minimum).Minimum
$带底 = ($栏 | ForEach-Object { $_.底 } | Measure-Object -Maximum).Maximum
"  带子实边 $($栏.Count) 条，顶边 y=$带顶，底边 y=$带底"
断言真 '框选带子的顶边和标尺下沿对齐（没有压到标尺上，也没从谱面中间开始）' `
  ([Math]::Abs($带顶 - $底线行[0]) -le 3) "带子顶 $带顶 ／ 标尺底线 $($底线行[0])"
断言真 '带子一直铺到卷帘底部' ($带底 -ge ($e2y - 40)) "带子底 $带底，轨 2 轨头在 $e2y"

# 收尾：把这一次框选清掉，别把选中状态留给下一张工单的验证
[V24]::Move([int]($win.X + $按X), [int]($win.Y + $按Y - 400))
[V24]::Click([int]($win.X + 60), [int]($win.Y + $按Y))

"`n$(if ($fail -eq 0) { '全过' } else { "$fail 条不过" })"
# 81 号票：裁决行 —— run-all.ps1 拿它跟退出码复核，对不上就把这一条降级成红（别删）。
"==== uitest 裁决 不过=$fail"
exit $fail
