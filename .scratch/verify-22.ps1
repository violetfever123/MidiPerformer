# 22 号工单：导航条从「全曲密度柱」改成「焦点轨的音符块」。
#
# 判据分六层：
#
#   · **画的是块、不是柱** —— 全条颜色直方图里最多的那个非底色应当是实心 `Accent`。
#     旧密度柱是 `Note` 半透明压在底色上（合成约 (62,74,88)，B−R 只有 26），
#     新块是 `Accent` #74ABDD = (116,171,221)，B−R 105。差三倍多，一测就分得开。
#   · **跟着焦点轨走** —— 这条是工单的核心，光验「变了」不够。本曲前两条轨一条 1605 个音、
#     一条 27 个，所以判据是**密度塌下去**：换到那条稀疏的，块列数该掉到零头。
#     再 Ctrl+↑ 回来，逐列指纹要**逐字回到原样** —— 只验「变了」的话，噪声也能让它过。
#   · **视口框没动** —— 还是 AccentSoft 实底 + AccentLine 1px 边，宽度 = 可见 4 小节占整曲的比例。
#   · **播放头** —— 和卷帘同色的红线，1 逻辑像素宽，跳转之后落在**视口框里**。
#   · **点按吸附整小节** —— 一小节的中间和它 ±0.3 小节点出来是同一个小节号，跨一格才 +1。
#   · **两把尺子**（只量不断言）—— 见第 6 节，条上有两套横向刻度对不齐，量出来记下。
#
# 导航条是自绘的 `RollNavStrip`，UIA 里没有它的框。**条的边界由它自己那条 1px 的 Line 边找出来**
#（`Render` 最后一行描的那个 Rect），不靠旁边的文字推 —— 推出来的会差十几像素，
# 而下面的断言全是按「占整条宽度的百分之几」算的。
#
# 四个坑，都踩过：
#   · **窗口尺寸是尺子的一部分。** 条内 y 1270..1355、焦点条 x 400..470 只在
#     「2360x1520 物理」这一种摆法下成立（应用默认的 1180x760 逻辑尺寸）。
#     脚本开头会**自己把窗口摆成这个尺寸**，所以别把那段删了 —— 见那里的注释。
#   · 第一版拿 `屏幕坐标` 当 `位图坐标` 用（UIA 给的是屏幕，`CopyFromScreen` 出来的位图
#     原点在窗口左上角），所有 x 都偏了一个窗口左上角。凡是 UIA 坐标取像素，先减 `$win.X/$win.Y`。
#   · 「位置」「跳到」那几个外框有时也是 Line 色的竖线，会一起被扫进来。真边是**成对**的
#     （1 逻辑像素 = 2 物理像素），按对取。
#   · **Ctrl+↑/↓ 要先点一下卷帘。** 焦点停在输入框里时 `OnWindowKeyDown` 整个让开（刻意的），
#     上一版点的是「小节导航」那四个字 —— TextBlock 不可聚焦，键盘焦点还在原来的框里，
#     于是 Ctrl+↓ 什么也没发生，缩略图指纹自然一模一样，看着像功能坏了。
#
# 前置（脚本自己不做）：app 开着、曲库里那一首 Carulli 已经载入。
# 用法: pwsh -NoProfile -File verify-22.ps1
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public class V22 {
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
  public static void Click(int x, int y) {
    SetCursorPos(x, y); System.Threading.Thread.Sleep(150);
    mouse_event(0x0002, 0,0,0, IntPtr.Zero); mouse_event(0x0004, 0,0,0, IntPtr.Zero);
    System.Threading.Thread.Sleep(330);
  }
  // 按一下再松开。**不用 SendKeys**：它把键投给「当前有焦点的控件」，
  // 而空格这件事恰恰是「不管焦点在谁身上、窗口层先吃掉」—— 要走就得走正常输入队列
  //（keybd_event 进的就是前台窗口那条）。
  public static void Key(byte vk) {
    keybd_event(vk, 0, 0, IntPtr.Zero);
    System.Threading.Thread.Sleep(50);
    keybd_event(vk, 0, 2, IntPtr.Zero);
    System.Threading.Thread.Sleep(120);
  }
}
"@

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$条件 = { param($t) New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $t) }

$p = Get-Process -Name MidiPerformer -EA SilentlyContinue |
  Where-Object { $_.MainWindowTitle -eq 'MIDI 演奏器' } | Select-Object -First 1
if (-not $p) { throw 'MidiPerformer 没在跑' }
$h = $p.MainWindowHandle
$root = $AE::FromHandle($h)
$win = $root.Current.BoundingRectangle

$fail = 0
function 断言真([string]$名字, [bool]$条件, [string]$原文) {
  if ($条件) { "  OK   $名字（$原文）" } else { "  FAIL $名字（$原文）"; $script:fail++ }
}
function 全部([object]$类型) { @($root.FindAll($TS::Descendants, (& $条件 $类型))) }
function 取文本 { 全部 $CT::Text }
function 找文本([string]$名字) { 取文本 | Where-Object { $_.Current.Name -eq $名字 } | Select-Object -First 1 }
function 按编号([string]$id) {
  @(@(全部 $CT::Text) + @(全部 $CT::Button)) |
    Where-Object { $_.Current.AutomationId -eq $id } | Select-Object -First 1
}

$播放键 = 按编号 'PlayButton'
if (-not $播放键 -or -not $播放键.Current.IsEnabled) { throw '还没载入曲子 —— 先跑 .scratch/open-app.ps1' }

# **先确认载入的是那一首。** 曲库里现在有两首（另一首叫 `cargo`，23 小节 11 条轨），
# 而 `.scratch/load-song.ps1` 是**按坐标**双击某一行的 —— 点歪一格就换了另一首，
# 而脚本剩下那五节照样往下跑，只是量出来的全是另一首歌的数。踩过一次：
# 读数成了「3 / 23 小节」，于是「第 50 小节中点」「挪半个条宽」这些断言全在量一首
# 只有 23 小节的曲子，十几条一起红，看着像功能全坏了，其实只是载错了曲子。
#
# 本曲：124 小节、4 条轨，01 轨 1605 个音、02 轨 27 个音 —— 下面所有容差都是按它定的。
# （这里直接现取「位置」那个文本 —— 读数那个辅助函数定义在下面，这会儿还没有）
$期望小节 = 124
$t0 = (按编号 'PositionText').Current.Name
if ($t0 -notmatch '(\d+)\s*/\s*(\d+)') { throw "位置读数看不懂：$t0" }
if ([int]$Matches[2] -ne $期望小节) {
  throw "载入的不是那一首：该是 $期望小节 小节，现在是 $($Matches[2]) 小节。先跑 .scratch/open-app.ps1（它按名字点 Carulli）"
}
if (-not [V22]::Take($h)) { throw '拽不到前台' }

# ---------- 先把窗口摆成写这些尺子时的那一种尺寸 ----------
#
# 下面两段扫描是按**像素**写的：条内 y 1270..1355、焦点条 x 400..470。它们只在
# 「窗口 2360x1520 物理」这一种摆法下成立 —— 也就是应用**默认**的 1180x760 逻辑尺寸
#（200% 缩放）。而 `.scratch/open-app.ps1` 把窗口摆成 **3040x1740**（屏幕 3072x1920），
# 于是照「先 open-app、再 verify」这个最自然的顺序跑，会在这里报「找不到导航条的上下边」——
# 看着像功能坏了，其实只是尺子换了把。
#
# 试过把尺子改成自适应的：拿「哪几行有 Accent 音符块」去圈条的邻域 —— 圈不出来，
# 窗口里 Accent 色的东西到处都是（标题、轨道头、按钮上的图标），全窗口每一行都有。
# 与其继续猜，不如**自己把窗口摆回去**：尺子是死的，窗口是活的，摆窗口只要一句。
# 副作用只有「脚本会动你的窗口尺寸」，比「时过时不过」划算。
#
# **要摆两次。** `SetWindowPos` 摆的是**外框**，而 UIA 的 `BoundingRectangle` 报的是
# 去掉那圈不可见拖拽边之后的范围，两者实测差着 x+13、y+58、宽−26、高−71。
# 只摆一次的话窗口落在 2334x1449（逻辑 1167x724，比默认的 1180x760 矮了 36 逻辑像素），
# 条就滑到扫描范围外面去了 —— 报出来还是「找不到条的边」。
# 所以先摆一次量出这个差，再按差补一次。
# 参数名**不能叫 `$h`**：外层那个 `$h` 是窗口句柄，脚本块的 param 会把它盖掉，
# 于是 `SetWindowPos($h, …)` 拿到的是高度 1520 当句柄使，`FromHandle(1520)` 报
# 「无法识别的错误」。踩过一次。
$摆 = { param($摆X, $摆Y, $摆宽, $摆高)
  [void][V22]::SetWindowPos($h, [IntPtr]::Zero, $摆X, $摆Y, $摆宽, $摆高, 0x0004 -bor 0x0010)
  Start-Sleep -Milliseconds 700
  ($AE::FromHandle($h)).Current.BoundingRectangle
}
$r1 = & $摆 405 450 2360 1520
# 外框 x 要落在 405 −（量到的 x − 405）= 810 − 量到的 x；宽要落在 2360 +（2360 − 量到的宽）= 4720 − 量到的宽。y、高同理。
$win = & $摆 (810 - [int]$r1.X) (900 - [int]$r1.Y) (4720 - [int]$r1.Width) (3040 - [int]$r1.Height)
$root = $AE::FromHandle($h)
if ([Math]::Abs([int]$win.Width - 2360) -gt 4 -or [Math]::Abs([int]$win.Height - 1520) -gt 4) {
  throw "窗口摆不回 2360x1520（现在 $([int]$win.Width)x$([int]$win.Height)）—— 屏幕是不是比这还小？"
}
"  窗口已摆成 $([int]$win.Width)x$([int]$win.Height) 物理（屏幕坐标 $([int]$win.X),$([int]$win.Y)）"

function 截图 {
  $bmp = New-Object System.Drawing.Bitmap -ArgumentList ([int]$win.Width), ([int]$win.Height)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen([int]$win.X, [int]$win.Y, 0, 0, $bmp.Size)
  $g.Dispose()
  $bmp
}
function 亮度($c) { 0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B }
# 六种签名，各认一种东西。**都只看颜色本身，不看位置**，窗口挪了也不受影响。
function 是块($c)   { (($c.B - $c.R) -gt 60) -and ((亮度 $c) -gt 110) }        # Accent     #74ABDD 实心块
function 是框线($c) { (($c.B - $c.R) -gt 60) -and ((亮度 $c) -le 110) }        # AccentLine #3A5F85 1px 边
function 是框底($c) { ($c.R -eq 28) -and ($c.G -eq 46) -and ($c.B -eq 66) }    # AccentSoft #1C2E42 实底
function 是红($c)   { (($c.R - $c.G) -gt 60) -and (($c.R - $c.B) -gt 60) }     # Stop       #E08080 播放头
function 是Line($c) { ($c.R -eq 51) -and ($c.G -eq 60) -and ($c.B -eq 70) }    # Line       #333C46 条的边框
function 是分隔($c) { ($c.R -eq 39) -and ($c.G -eq 46) -and ($c.B -eq 55) }    # LineSoft   #272E37 每 4 小节一根

function 段($xs) {
  if ($xs.Count -eq 0) { return '(无)' }
  $out = @(); $s = $xs[0]; $q = $xs[0]
  for ($i = 1; $i -lt $xs.Count; $i++) {
    if ($xs[$i] - $q -gt 1) { $out += "$s..$q"; $s = $xs[$i] }
    $q = $xs[$i]
  }
  $out += "$s..$q"
  $out -join '  '
}
function 读数 { (按编号 'PositionText').Current.Name }
function 读小节 { $t = 读数; if ($t -match '(\d+)') { [int]$Matches[1] } else { -1 } }

# ---------- 0. 导航条的边界 ----------
"=== 0. 导航条的边界（自绘控件，UIA 里没有框，靠它自己那条边找）==="
# 第一帧要重试：这个脚本的头一次截图紧跟在 `Take()` 把窗口拽到前台之后，
# 偶尔会抓在 DWM 还没重绘完的那一瞬（整片底色，一条 Line 都没有）。重试三次。
$横边 = @()
for ($试 = 1; $试 -le 4; $试++) {
  $bmp = 截图
  $横边 = @()
  for ($y = 1270; $y -le 1355; $y++) { if (是Line $bmp.GetPixel(300, $y)) { $横边 += $y } }
  if ($横边.Count -ge 2) { break }
  $看见 = @()
  for ($y = 1270; $y -le 1355; $y++) { $c = $bmp.GetPixel(300, $y); $看见 += "$($c.R),$($c.G),$($c.B)" }
  $bmp.Dispose()
  "  第 $试 次截图里没有导航条的边；x=300 那一列取到 $((($看见 | Select-Object -Unique) -join ' / '))，重试"
  Start-Sleep -Milliseconds 800
}
if ($横边.Count -lt 2) { throw '找不到导航条的上下边（重试四次都没有）' }
$内上 = $横边[0] + 2; $内下 = $横边[-1] - 1
$线列 = @()
for ($x = 0; $x -lt [int]$win.Width; $x++) {
  $n = 0
  for ($y = $内上; $y -le $内下; $y++) { if (是Line $bmp.GetPixel($x, $y)) { $n++ } }
  if ($n -ge 30) { $线列 += $x }
}
# **条宽先从横边那一行直接数出来。** 条的上边框本身就是一条横跨整条的 Line 横线，
# 数它有多少个 Line 像素就是条宽，不用绕竖边。
$横宽 = 0
for ($y = $横边[0]; $y -le $横边[0] + 1; $y++) {
  $n = 0
  for ($x = 0; $x -lt [int]$win.Width; $x++) { if (是Line $bmp.GetPixel($x, $y)) { $n++ } }
  if ($n -gt $横宽) { $横宽 = $n }
}
$bmp.Dispose()
# 只认成对的（1 逻辑像素 = 2 物理像素）：这一列后面跟着同色，再后面不是
$对起 = @($线列 | Where-Object { $线列 -contains ($_ + 1) -and -not ($线列 -contains ($_ - 1)) })
$对尾 = @($线列 | Where-Object { $线列 -contains ($_ - 1) -and -not ($线列 -contains ($_ + 1)) })
"  Line 色的列：$($线列 -join ', ')"
"  横边那一行上数出 $横宽 个 Line 像素（= 条宽）"
if ($对起.Count -lt 1 -or $对尾.Count -lt 1) {
  throw "找不出条的成对竖边：起头 $($对起 -join ',')、收尾 $($对尾 -join ',')"
}
#
# **左边界取第一对，右边界取"宽度最接近 $横宽"的那一对。** 不能简单地取最后一对：
# 条右边别处还会出现成对的 Line 竖线（实测有过一对 1789/1790），把它当右边界，
# 条宽就从 1636 变成 1652 —— 后面所有按比例算的东西全跟着偏，
# §6 就是这么报出「第 94 小节，下标 92.249，差 -0.751」这条假故障的：
# 红线其实稳稳待在 1368（两次跑逐像素一样），是**除数**被换掉了。
$候选 = @()
foreach ($l in $对起) {
  foreach ($r in $对尾) {
    if ($r -le $l) { continue }
    $候选 += [pscustomobject]@{ 左 = $l; 右 = $r; 宽 = $r - $l + 1; 差 = [Math]::Abs(($r - $l + 1) - $横宽) }
  }
}
$条 = $候选 | Sort-Object 差 | Select-Object -First 1
"  最贴合横宽的竖边对：x $($条.左)..$($条.右)（宽 $($条.宽)，与横边差 $($条.差)）"
if ($条.差 -gt 8) { throw "数出来的条宽（横边 $横宽）和竖边对（宽 $($条.宽)）对不上" }
$条左 = $条.左; $条右 = $条.右
$条宽 = $条.宽
"  导航条：位图 x $条左..$条右（宽 $条宽 物理 = $($条宽/2) 逻辑）"
断言真 '条的左右竖边各是 1 逻辑像素（= 2 物理像素）宽的一对' `
  (($对尾 -contains ($条左 + 1)) -and ($对起 -contains ($条右 - 1))) `
  "左 $条左、右 $条右；左的搭档在对里 $($对尾 -contains ($条左 + 1))，右的搭档在对里 $($对起 -contains ($条右 - 1))"
断言真 '条宽落在合理区间（窗口 1180 逻辑宽，导航条占中间那一栏）' `
  ($条宽 -gt 1200 -and $条宽 -lt 1800) "宽 $条宽 物理"

function 取列 {
  $b = 截图
  $o = [ordered]@{ 块 = @(); 框底 = @(); 框线 = @(); 红 = @(); 分隔 = @() }
  for ($x = $条左; $x -le $条右; $x++) {
    $n块 = 0; $n底 = 0; $n线 = 0; $n红 = 0; $n隔 = 0
    for ($y = $内上; $y -le $内下; $y++) {
      $c = $b.GetPixel($x, $y)
      if (是块 $c) { $n块++ } elseif (是框底 $c) { $n底++ }
      elseif (是框线 $c) { $n线++ } elseif (是红 $c) { $n红++ }
      elseif (是分隔 $c) { $n隔++ }
    }
    if ($n块 -ge 2) { $o.块 += $x }
    if ($n底 -ge 20) { $o.框底 += $x }
    if ($n线 -ge 20) { $o.框线 += $x }
    if ($n红 -ge 20) { $o.红 += $x }
    if ($n隔 -ge 40) { $o.分隔 += $x }
  }
  $b.Dispose()
  $o
}
function 指纹($列) { (($列.块 | ForEach-Object { $_ - $条左 }) -join ',') }

# 取一帧，**连带把「位置」读数一起钉在同一时刻**。
#
# 为什么不能分开取：`取列` 要逐像素扫整条，一次一秒上下，而 `RefreshReadout()` 和重绘
# 并不是同一个瞬间完成的 —— 跳转刚落地时，一帧里可能是新读数配旧红线，或者反过来。
# 分开取过一次，量出「读数 83、红线在下标 113.8」这种自相矛盾的一对，
# 断言红线的三条全红，看着像播放头乱跳，其实是我把两个时刻的数拼在了一起。
# 所以读数在截图**前后各读一次**，两次一致才认；不一致就重取（跳转还在落地）。
function 稳帧([string]$说明) {
  $列 = $null; $前 = -1; $后 = -2
  for ($i = 1; $i -le 4; $i++) {
    $前 = 读小节
    $列 = 取列
    $后 = 读小节
    if ($前 -eq $后) { break }
    "    （$说明：第 $i 次取帧时读数从 $前 变到 $后，重取）"
    Start-Sleep -Milliseconds 400
  }
  [pscustomobject]@{ 列 = $列; 读 = $后 }
}

# FocusBar：焦点轨左边贴的那条 3 逻辑像素 Accent 竖条（TrackLaneView.axaml 里 IsVisible 只给焦点轨）。
# **和缩略图无关的独立信号** —— 缩略图两条轨万一长得一样，「指纹变了」就证不了「焦点换了」。
function 焦点带 {
  $b = 截图; $o = @()
  for ($y = 0; $y -lt [int]$win.Height; $y++) {
    $n = 0
    for ($x = 400; $x -le 470; $x++) { if (是块 $b.GetPixel($x, $y)) { $n++ } }
    if ($n -ge 4) { $o += $y }
  }
  $b.Dispose()
  if ($o.Count -eq 0) { return '(无)' }
  $r = @(); $s = $o[0]; $q = $o[0]
  for ($i = 1; $i -lt $o.Count; $i++) { if ($o[$i] - $q -gt 1) { $r += "$s..$q"; $s = $o[$i] }; $q = $o[$i] }
  $r += "$s..$q"
  (($r | Where-Object { $t = $_ -split '\.\.'; ([int]$t[1] - [int]$t[0]) -ge 60 }) -join '  ')
}

$列0 = 取列
"  块占 $($列0.块.Count) 列；框底 $(段 $列0.框底)；框线 $(段 $列0.框线)；播放头 $(段 $列0.红)"
"  每 4 小节的分隔线：$(段 $列0.分隔)"

# ---------- 1. 画的是实心 Accent 块，不是半透明密度柱 ----------
"`n=== 1. 缩略图画的是实心 Accent 块 ==="
$bmp = 截图
$直 = @{}
for ($x = $条左; $x -le $条右; $x++) {
  for ($y = $内上; $y -le $内下; $y++) {
    $c = $bmp.GetPixel($x, $y)
    $k = "$($c.R),$($c.G),$($c.B)"
    if ($直.ContainsKey($k)) { $直[$k]++ } else { $直[$k] = 1 }
  }
}
$bmp.Dispose()
"  条上最多的 6 种颜色："
$直.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 6 |
  ForEach-Object { "    $($_.Key)  x$($_.Value)" }
$非底 = $直.GetEnumerator() | Where-Object { $_.Key -ne '26,31,38' -and $_.Key -ne '28,46,66' } |
  Sort-Object Value -Descending | Select-Object -First 1
$rgb = $非底.Key -split ','
"  最多的非底色 = $($非底.Key) x$($非底.Value)"
断言真 '最多的非底色是 Accent #74ABDD（116,171,221）' `
  ([Math]::Abs([int]$rgb[0] - 116) -le 8 -and [Math]::Abs([int]$rgb[1] - 171) -le 8 -and [Math]::Abs([int]$rgb[2] - 221) -le 8) `
  "实测 ($($rgb -join ',')) —— 半透明密度柱压出来的是 (62,74,88) 一带，差得远"
断言真 '块铺得有相当规模' ($列0.块.Count -gt 300) "占了 $($列0.块.Count) 列"

# ---------- 2. 跟着焦点轨换 ----------
#
# **换轨之前必须点一下卷帘**，把键盘焦点从输入框里拿回来（见文件头的坑）。
# 点卷帘顺带把焦点轨设成那一条，所以基线在点完之后取。
#
# 判据不只是「变了」，而是**密度塌下去**：本曲 01 轨 1605 个音、02 轨只有 27 个。
# 缩略图若还画的是全曲密度，换轨就不会有任何变化 —— 那正是这条工单要改掉的东西。
"`n=== 2. 换焦点轨，缩略图跟着换 ==="
[V22]::Click([int]($win.X + 1000), [int]($win.Y + 600))
Start-Sleep -Milliseconds 500
$帧A = 稳帧 '基线'
$列A = $帧A.列
$带A = 焦点带
$指纹A = 指纹 $列A
"  点了卷帘 → 读数 $(读数) ；FocusBar $带A ；块占 $($列A.块.Count) 列"
[System.Windows.Forms.SendKeys]::SendWait('^{DOWN}')
Start-Sleep -Milliseconds 900
$列B = (稳帧 '换轨').列
$带B = 焦点带
"  Ctrl+↓ → FocusBar $带B ；块占 $($列B.块.Count) 列"
断言真 'Ctrl+↓ 之后焦点轨换了（FocusBar 挪到另一条轨）' ($带B -ne $带A) "FocusBar $带A → $带B"
断言真 '缩略图跟着换成新焦点轨 —— 从 1605 个音的那条换到 27 个音的那条，块该塌下去' `
  ($列B.块.Count -lt ($列A.块.Count * 0.5)) `
  "块列数 $($列A.块.Count) → $($列B.块.Count)（新焦点轨只有 27 个音）"
[System.Windows.Forms.SendKeys]::SendWait('^{UP}')
Start-Sleep -Milliseconds 900
$帧C = 稳帧 '换回来'
$列C = $帧C.列
"  Ctrl+↑ → FocusBar $(焦点带) ；块占 $($列C.块.Count) 列"
断言真 'Ctrl+↑ 换回来，缩略图逐字回到原样' ((指纹 $列C) -eq $指纹A) `
  "块列数 $($列A.块.Count) → $($列B.块.Count) → $($列C.块.Count)"

# ---------- 3. 视口框：外观没变、跟着视图动 ----------
#
# 框宽 = 可见 4 小节 / 总小节 × 条宽。总小节从「位置」读数里读，不写死。
# 框底那一段**允许播放头在中间打一个洞**：播放头画在框之后（不然后画就被框盖住），
# 而它绝大多数时候正好落在可见窗口里 —— 那正是它最该被看见的时候。
"`n=== 3. 视口框 ==="
$t = 读数
if ($t -notmatch '(\d+)\s*/\s*(\d+)') { throw "位置读数看不懂：$t" }
$总小节 = [int]$Matches[2]
$预期框宽 = [int][Math]::Round(4.0 / $总小节 * $条宽)
"  位置读数 = $t ；总小节 = $总小节 ；预期框宽 = 4/$总小节 × $条宽 = $预期框宽"
$框左0 = if ($列C.框底.Count) { $列C.框底[0] } else { -1 }
$框右0 = if ($列C.框底.Count) { $列C.框底[-1] } else { -1 }
断言真 '视口框是 AccentSoft 实底' ($列C.框底.Count -gt 0) "在 $(段 $列C.框底)"
断言真 '视口框宽度 = 可见 4 小节占整曲的比例（播放头打的洞算在内）' `
  ([Math]::Abs(($框右0 - $框左0 + 1) - $预期框宽) -le 8) `
  "实测 $($框右0 - $框左0 + 1)（框底像素 $($列C.框底.Count) 个，差的是播放头那两列），预期 $预期框宽"
断言真 '视口框右边那条 AccentLine 1px 边还在' ($列C.框线.Count -ge 1) "框线在 $(段 $列C.框线)"

$跳X = [int]($条左 + $条宽 * 0.75)
[V22]::Click([int]($win.X + $跳X), [int]($win.Y + $内上 + 20))
Start-Sleep -Milliseconds 700
$帧75 = 稳帧 '跳到 3/4'
$列75 = $帧75.列
$框左75 = if ($列75.框底.Count) { $列75.框底[0] } else { -1 }
$框右75 = if ($列75.框底.Count) { $列75.框底[-1] } else { -1 }
"  点条 3/4 处（位图 x=$跳X）→ 读数 $($帧75.读) ；框底 $(段 $列75.框底)"
断言真 '跳到后段之后视口框跟着挪过去了' ($框左75 -gt ($条左 + $条宽 * 0.4)) `
  "框左 $框左75（出发时 $框左0），条中线在 $([int]($条左 + $条宽/2))"
断言真 '视口框宽度没变（还是 4 小节）' `
  ([Math]::Abs(($框右75 - $框左75) - ($框右0 - $框左0)) -le 4) `
  "$($框右0 - $框左0 + 1) → $($框右75 - $框左75 + 1)"

# ---------- 4. 播放头线 ----------
#
# 新增的那条：和卷帘同色（Stop #E08080），1 逻辑像素宽。
# 该量的不是「它在某个绝对坐标上」，是**它在视口框里** ——
# 这条线存在的理由就是回答「我正在看的这一段，播到哪儿了」。
"`n=== 4. 缩略图上的播放头线 ==="
断言真 '条上有一条 Stop 色的竖线' ($列75.红.Count -ge 1) "在 $(段 $列75.红)"
$红75 = if ($列75.红.Count) { $列75.红[0] } else { -1 }
"  播放头 x=$红75 ；框 $框左75..$框右75"
断言真 '播放头是 1 逻辑像素宽（= 2 物理像素）' ($列75.红.Count -le 4) "占了 $($列75.红.Count) 列"
断言真 '播放头落在视口框里（跳过去之后就看得见自己在哪）' `
  ($红75 -ge ($框左75 - 3) -and $红75 -le ($框右75 + 3)) "红 $红75 ／ 框 $框左75..$框右75"

$退X = [int]($条左 + $条宽 * 0.25)
[V22]::Click([int]($win.X + $退X), [int]($win.Y + $内上 + 20))
Start-Sleep -Milliseconds 700
$帧25 = 稳帧 '跳到 1/4'
$列25 = $帧25.列
$红25 = if ($列25.红.Count) { $列25.红[0] } else { -1 }
$动 = $红75 - $红25
"  点条 1/4 处 → 读数 $($帧25.读) ；播放头 $红25（刚才是 $红75，往左挪了 $动）"
断言真 '播放头跟着播放位置往左走' ($红25 -ge 0 -and $动 -gt ($条宽 * 0.3)) "挪了 $动 物理像素，条宽 $条宽"
断言真 '挪的幅度约等于「3/4 减 1/4」（半个条宽，容一个半小节）' `
  ([Math]::Abs($动 - ($条宽 * 0.5)) -le ($条宽 / $总小节 * 1.5)) `
  "挪了 $动，半个条宽是 $([int]($条宽 * 0.5))，一小节宽 $([int]($条宽 / $总小节))"

# ---------- 5. 点按吸附整小节 ----------
#
# **先说清「分数」和「小节号」差一。** 条上横坐标 x 折成的小节**下标**是 0 起的：
# `NavBarAtX` 算的是 `round(x / 条宽 × 总小节)` 再夹到 `[0, 总小节-1]`，
# 而下标 i 对应界面上写着的**第 i+1 小节**。所以第 N 小节的**起点**落在分数 N−1 上。
#
# 于是取第 N 小节的中点（分数 N−1）为中心，往左 0.3、往右 0.3 都还在 round 到 N−1 的区间
#（`[N-1.5, N-0.5)`）里，三下该是同一个号；再往右整整一小节（分数 N）才该 +1。
# **别用 N−0.5 / N+0.5 这种写在刀刃上的分数** —— 差 0.5 正好是 round 的边界，
# 上一版就是这么写的，`49.8` 一 round 成了 50（第 51 小节），看着像吸附坏了。
"`n=== 5. 点按吸附整小节 ==="
#
# **吸附要看播放头落在哪，光看小节号看不出来。** 读数报的是「播放头在第几小节」，
# 不管吸附不吸附，它都是那个小节号 —— 不吸附时播放头停在半小节上，号还是同一个号。
# 所以真正的证据是**像素**：同一小节里两个偏左偏右的点，吸住了的话播放头该落在
# **同一个像素**上；没吸住的话两点差 0.6 小节，按 13.2 物理像素一小节算就是 **8 个像素**的差
# —— 8 像素是个一眼能分的大数，不是容差里的小数。
#
# 顺带记一句上一版为什么改：上一版只断言小节号，采样点取在分数 49.0 / 48.7 / 49.3。
# 一小节才 13 物理像素宽，±0.3 小节 = ±4 像素，离 round 的翻转点（49.5）只剩 2~3 像素，
# 于是**条宽从 1636 变成 1652** 这件与它无关的事，就能把 49.3 那个点从「第 50 小节」推到
# 「第 51 小节」，断言红了 —— 红的不是吸附，是采样点选得太贴边。
$小节宽 = $条宽 / $总小节
$目标 = 50
function 红列 {
  $b = 截图; $o = @()
  for ($x = $条左; $x -le $条右; $x++) {
    $n = 0
    for ($y = $内上; $y -le $内下; $y++) { if (是红 $b.GetPixel($x, $y)) { $n++ } }
    if ($n -ge 20) { $o += $x }
  }
  $b.Dispose()
  if ($o.Count) { $o[0] } else { -1 }
}
function 点条([double]$分数) {
  $x = [int]($条左 + $小节宽 * $分数)
  [V22]::Click([int]($win.X + $x), [int]($win.Y + $内上 + 20))
  Start-Sleep -Milliseconds 700
  [pscustomobject]@{ 号 = 读小节; 红 = 红列; 点X = $x }
}
$甲 = 点条 ($目标 - 1)
$乙 = 点条 ($目标 - 1.3)
$丙 = 点条 ($目标 - 0.7)
$丁 = 点条 ($目标)
"  第 $目标 小节中点（分数 $($目标-1)）→ 读数 $($甲.号)、播放头 x=$($甲.红)"
"  −0.3 小节（分数 $($目标-1.3)）  → 读数 $($乙.号)、播放头 x=$($乙.红)"
"  +0.3 小节（分数 $($目标-0.7)）  → 读数 $($丙.号)、播放头 x=$($丙.红)"
"  +1 小节（分数 $目标）          → 读数 $($丁.号)、播放头 x=$($丁.红)"
断言真 '读数形式是「N / 总 小节」' ((读数) -match "^\d+\s*/\s*$总小节\s*小节$") "读数 = $(读数)"
断言真 '小节中点处点出来就是那个小节' ($甲.号 -eq $目标) "$($甲.号)（期望 $目标）"
断言真 '同一小节里偏 0.3 也还是那个小节' (($乙.号 -eq $甲.号) -and ($丙.号 -eq $甲.号)) `
  "$($甲.号) → $($乙.号) / $($丙.号)"
断言真 '吸附到了格线上：同一小节里偏左偏右，播放头落在同一个像素' `
  ($乙.红 -eq $甲.红 -and $丙.红 -eq $甲.红 -and $甲.红 -ge 0) `
  "x $($甲.红) / $($乙.红) / $($丙.红)（不吸附的话该差 $([int]($小节宽 * 0.6)) 像素）"
断言真 '跨过一个小节才 +1' ($丁.号 -eq ($甲.号 + 1)) "$($甲.号) → $($丁.号)"
断言真 '跨过一小节，播放头也确实挪了一小节那么宽' `
  ([Math]::Abs(($丁.红 - $甲.红) - $小节宽) -le 4) `
  "挪了 $($丁.红 - $甲.红) 物理像素，一小节 $([Math]::Round($小节宽,1))"

# ---------- 6. 播放头落在它自己那条格线上 ----------
#
# 条上横坐标有两条来路，公式本来**可证明完全相同**：
#   · **分隔线**用 `scene.BarWidth = Width / BarCount`，画在 `round(bar·BarWidth)+0.5`；
#   · **播放头**用 `NavXAtTick(tick) = tick/TotalTicks·Width`，而 `TotalTicks = TicksPerBar·BarCount`，
#     于是 `XAtTick((N-1)·TicksPerBar) = (N-1)·BarWidth` —— 同一个式子。
#
# 所以「第 N 小节的格线在哪儿」可以两条路各算一次：一条从条宽推，一条就看红线的实际位置。
# 断言的是**红线落在读数所指那一小节的格线上**（容半小节）—— 这正是用户看得见的那件事：
# 「跳到 94 小节」之后，缩略图上那根红线该落在第 94 小节那一格，不该压着邻格。
#
# 顺带把分隔线的尺度量出来当旁证。**分隔线会被音符块盖住而漏检**，
# 漏检那一处间距会是正常值的两倍，直接取平均就把它算进去了 ——
# 上一版就是这么得到「两把尺子差 3.3%」的假象的。剔掉超过中位数 1.5 倍的间距再平均。
"`n=== 6. 播放头落在它自己那条格线上 ==="
$每小节物理 = $条宽 / $总小节
#
# **除数要对一遍。** §0 量的是「条的外框有多宽」（靠 Line 色的边框像素找），
# 而 §3 的「视口框该多宽」、§6 的「红线落在第几小节」除的都是**尺子画格线用的那个宽度** ——
# 两者不一定相等：实测有过一轮，外框量出 1652（边框行从 139 到 1790 是实心的），
# 可格线间距说每小节 13.19、124 小节只有 1636，中间差着 16 物理像素（尺子右边留了一条空白）。
# 那一轮 §6 就报出过「第 94 小节，下标 92.249，差 -0.751」这条**假故障**：红线稳稳待在 1368，
# 两次跑逐像素一样，是**除数**被换掉了。
#
# 所以这里不猜哪个对，**当场拿尺子自己画的格线间距验一次**：格线是曲子里真实的小节线，
# 它的间距是独立于外框量出来的第二个数。两个数对不上就报出来，
# 别再让它悄悄换掉后面所有按比例算的东西的除数。
if ($列0.分隔.Count -ge 3) {
  $起 = @(); $p = -99
  foreach ($x in $列0.分隔) { if ($x - $p -gt 1) { $起 += $x }; $p = $x }
  $间距 = @(); for ($i = 1; $i -lt $起.Count; $i++) { $间距 += ($起[$i] - $起[$i-1]) }
  $中位 = ($间距 | Sort-Object)[[int]($间距.Count / 2)]
  $正常 = @($间距 | Where-Object { $_ -le ($中位 * 1.5) })
  $漏掉 = $间距.Count - $正常.Count
  $平均 = ($正常 | Measure-Object -Average).Average
  "  分隔线 $($起.Count) 根；间距中位数 $中位 物理像素（= 4 小节）；"
  "  其中 $漏掉 处间距异常（被音符块盖住漏检），剔掉后 $($正常.Count) 段平均 $([Math]::Round($平均,3)) → 每小节 $([Math]::Round($平均/4,3)) 物理"
  断言真 '外框量出来的条宽，和尺子自己画的格线间距对得上（除数没被换掉）' `
    ([Math]::Abs($每小节物理 - $平均 / 4) -le ($每小节物理 * 0.01)) `
    "外框算 $([Math]::Round($每小节物理,3))、格线算 $([Math]::Round($平均/4,3)) 物理一小节，差 $([Math]::Round([Math]::Abs($每小节物理 - $平均/4) / $每小节物理 * 100, 2))%"
}
"  按条宽算，每小节 = $([Math]::Round($每小节物理, 3)) 物理"
foreach ($对 in @(@($帧25.读, $红25), @($帧75.读, $红75))) {
  $号 = $对[0]; $x = $对[1]
  if ($x -lt 0 -or $号 -le 1) { continue }
  $下标 = ($x - $条左) / $每小节物理
  "  读数第 $号 小节，红线在 x=$x → 折回下标 $([Math]::Round($下标,3))（该是 $($号 - 1)）"
  断言真 "红线落在读数所指那一小节的格线上（第 $号 小节）" `
    ([Math]::Abs($下标 - ($号 - 1)) -le 0.5) `
    "下标 $([Math]::Round($下标,3)) vs 期望 $($号 - 1)，差 $([Math]::Round($下标 - $号 + 1,3)) 小节"
}

# ---------- 7. 真播起来，播放头自己往右爬 ----------
#
# 第 4 节量的是**跳转**推着播放头动（seek 推的）。工单第 4 条验收说的是「跟着**播放**移动」，
# 这是另一回事 —— 跳转只要一次重绘，播放要时钟一帧一帧推。所以这里真按空格开播再取几帧。
#
# 空格 = 走带条上那颗「▶ 从当前位置播放」：`MainWindow.OnWindowKeyDown` 抢在所有控件前面处理，
# 而且会先把键盘焦点收回窗口（`Button` 对空格走的是**类处理器**，光标了 `Handled` 拦不住）。
# 但输入框那条路它整块让开 —— 第 2 节已经点过卷帘，焦点不在输入框里。
#
# **走带条上那颗播放按钮点不到**：窗口下沿约 180 物理像素压在任务栏底下（任务栏顶在 y≈1824）。
# 所以空格是这条路唯一的口子。
"`n=== 7. 真按空格播起来，播放头自己往右爬 ==="
if ([V22]::GetForegroundWindow() -ne $h) { throw '窗口不在前台 —— 空格会打到别的窗口上（第 2 节点过卷帘之后本该一直在前台）' }
$播前 = 稳帧 '开播之前'
$播前红 = if ($播前.列.红.Count) { $播前.列.红[0] } else { -1 }
"  开播前：读数 $($播前.读) 小节 ；播放头 x=$播前红"
[V22]::Key(0x20)
$爬 = @()
foreach ($i in 1..4) {
  Start-Sleep -Milliseconds 1400
  $列 = 取列
  $读 = 读小节
  $红 = if ($列.红.Count) { $列.红[0] } else { -1 }
  "  开播后第 $i 帧：读数 $读 小节 ；播放头 x=$红"
  $爬 += [pscustomobject]@{ 读 = $读; 红 = $红 }
}
[V22]::Key(0x20)
Start-Sleep -Milliseconds 1200
$停列 = 取列
$停一 = 读小节; $停红一 = if ($停列.红.Count) { $停列.红[0] } else { -1 }
Start-Sleep -Milliseconds 1500
$停二 = 读小节; $停红二 = if ((取列).红.Count) { (取列).红[0] } else { -1 }
"  停之后：读数 $停一 → $停二 小节、播放头 x=$停红一 → $停红二（1.5 秒都没再动才叫停住了）"
断言真 '按了空格确实在播（读数自己往前走，不是靠跳转）' ($爬[-1].读 -gt $播前.读) `
  "第 $($播前.读) 小节 → 第 $($爬[-1].读) 小节"
断言真 '缩略图上的播放头跟着播放往右爬' ($爬[-1].红 -gt $播前红) "x $播前红 → $($爬[-1].红)"
断言真 '整个采样过程里播放头一步没往回退' `
  (@(1..($爬.Count - 1) | Where-Object { $爬[$_].红 -lt $爬[$_ - 1].红 }).Count -eq 0) `
  "各帧 x：$(($爬 | ForEach-Object { $_.红 }) -join ', ')"
断言真 '每一帧都量到了播放头（不是量到一半丢了）' (@($爬 | Where-Object { $_.红 -lt 0 }).Count -eq 0) `
  "各帧 x：$(($爬 | ForEach-Object { $_.红 }) -join ', ')"
断言真 '再按一次空格就停住了（停后读数和播放头都不再动）' (($停二 -eq $停一) -and ($停红二 -eq $停红一)) `
  "停时 读数 $停一 / x $停红一，1.5 秒后 读数 $停二 / x $停红二"

"`n$(if ($fail -eq 0) { '全过' } else { "$fail 条不过" })"
exit $fail
