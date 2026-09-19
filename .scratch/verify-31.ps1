# 31 号工单：界面删掉「按值认音的选中镜像」之后，改动跑完选中的**还是不是同一个音**。
#
# 31 删掉的是这么一套东西：记下选中的音长什么样（轨+音高+起点+时值+力度），
# 命令跑完之后拿这份内容去新谱子里**重新把它找出来**，再把下标装回选中集。
# 它为什么会出错，就是这张单要根除的东西 —— 而它错在哪儿，决定了这里该量什么：
#
#   · **挪动会重排 `Track.Notes`**（29 收口的不变量，只按 StartTick 稳定排序）。
#     所以**挪时间**是真重排，**挪音高不会重排**（同起点时稳定排序保持原相对次序）。
#     镜像要是拿「挪之前那份内容」去找，重排之后找到的就是**另一个音**。
#   · **撤销 / 重做**方向相反，镜像得把值往回算 —— 算错就丢选中。
#   · **删除**之后那条内容在谱子里没了，镜像找不到，选中要么空掉要么落到错的音上。
#
# 所以每一节都用**读数条**当量具：它是 21 号刚合并出来的那一条，显示的正是
# 「此刻算在选中头上的那个音」的轨/音高/小节/拍位/时值。判据是同一条：
# **改完位置动了、别的字段一个不动；再改回去，五格逐字回到原样。**
# 逐字回到原样 = 身份没丢 —— 镜像认错音的话，回来的是别人。
#
# 前置（脚本自己不做）：app 开着、曲库里那一首 Carulli 已经载入
#（.scratch/load-song.ps1 就是干这个的）。
#
# 用法: pwsh -NoProfile -File verify-31.ps1
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Windows.Forms, UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public class V31 {
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

function 前台 { if (-not [V31]::Take($h)) { throw '拽不到前台 —— 台面上有别的窗口压着' } }

# ---------- 量具：读数条那一套五格 ----------
#
# 锚在**右边那句快捷键提示**上 —— 它是这一条里唯一「不管有没有悬停/选中都在」的文本
# （和 verify-21 同一个锚，理由也一样）。
function 读一套 {
  $t = @(取文本)
  $锚 = $t | Where-Object { $_.Current.Name -like '空格 播放*' } | Select-Object -First 1
  $o = [ordered]@{ 可见 = $false; 轨 = '(缺)'; 音高 = '(缺)'; 小节 = '(缺)'; 拍位 = '(缺)'; 时值 = '(缺)' }
  if (-not $锚) { return $o }
  $y = [int]$锚.Current.BoundingRectangle.Y

  foreach ($标签 in @('轨', '音高', '小节', '拍位', '时值')) {
    $lbl = $t | Where-Object {
      $_.Current.Name -eq $标签 -and [Math]::Abs($_.Current.BoundingRectangle.Y - $y) -lt 14
    } | Select-Object -First 1
    if (-not $lbl) { continue }
    $o.可见 = $true
    $lr = $lbl.Current.BoundingRectangle
    # 「键」右边紧挨着那格就是「值」：同 Y、X 在 90px 以内、取最近的一个
    $v = $t | Where-Object {
      $r = $_.Current.BoundingRectangle
      [Math]::Abs($r.Y - $lr.Y) -lt 14 -and $r.X -gt $lr.X -and $r.X -lt ($lr.X + 90)
    } | Sort-Object { $_.Current.BoundingRectangle.X } | Select-Object -First 1
    $o[$标签] = if ($v) { $v.Current.Name } else { '(读不到)' }
  }
  $o
}
function 串($o) {
  "可见=$($o.可见) 轨=$($o['轨']) 音高=$($o['音高']) 小节=$($o['小节']) 拍位=$($o['拍位']) 时值=$($o['时值'])"
}

# 合成按键。SendKeys 发给前台窗口，而方向键/删除这几条是**窗口级隧道**接的
# （OnWindowKeyDown），所以焦点在卷帘上就够了 —— 只要别停在输入框里（那里整块让开）。
function 键([string]$k, [int]$等 = 420) {
  [System.Windows.Forms.SendKeys]::SendWait($k)
  Start-Sleep -Milliseconds $等
}

# ---------- 找音符的像素位置 ----------
# 和 verify-21 同一套：截图扫亮度找横向连续段，上下界由「01」「02」两个轨头文本夹出来。
# 阈值取 60、真正干活的是「连续段 ≥ 24px」—— 音符和竖向小节线的亮度是重叠的，靠宽度才分得开。
function 找音符 {
  $e1 = 找文本 '01'; $e2 = 找文本 '02'
  if (-not $e1 -or -not $e2) { throw '找不到轨头 01 / 02' }

  $bmp = New-Object System.Drawing.Bitmap([int]$win.Width, [int]$win.Height)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen([int]$win.X, [int]$win.Y, 0, 0, $bmp.Size)
  $g.Dispose()

  # +110 跳过轨头那一排**和标尺**（标尺上的小节号也是亮的，会被当成音符）
  $top = [int]($e1.Current.BoundingRectangle.Y - $win.Y) + 110
  $bot = [int]($e2.Current.BoundingRectangle.Y - $win.Y) - 14
  $left = [int]($e1.Current.BoundingRectangle.X - $win.X) + 4
  $right = [int]($win.Width) - 20

  $行 = @()
  for ($y = $top; $y -lt $bot; $y += 2) {
    $最好 = 0; $最好X = -1; $连 = 0; $起 = -1
    for ($x = $left; $x -lt $right; $x += 2) {
      $c = $bmp.GetPixel($x, $y)
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

  $并 = @()
  foreach ($b in $行) {
    if ($并.Count -gt 0 -and ($b.Y - $并[-1].末) -le 4) {
      $m = $并[-1]; $m.末 = $b.Y; $m.行数++
      if ($b.宽 -gt $m.宽) { $m.宽 = $b.宽; $m.X = $b.X; $m.Y = $b.Y }
    } else {
      $并 += @{ Y = $b.Y; 末 = $b.Y; X = $b.X; 宽 = $b.宽; 行数 = 1 }
    }
  }
  return @($并 | Where-Object { $_.行数 -ge 3 } | ForEach-Object {
      @{ X = [int]($win.X + $_.X + $_.宽 / 2); Y = [int]($win.Y + $_.Y); 宽 = $_.宽 }
    })
}

# ---------- 0. 前置：点中一个音，鼠标挪开，记下它的读数 ----------
"=== 0. 前置 ==="
前台
$音 = @(找音符)
"  扫到 $($音.Count) 条音符带"
if ($音.Count -lt 2) { throw "扫出来的音符不到两条 —— 卷帘上是不是没音？" }

# 挑**最靠窗口中间**的一条：左右都留得下挪动的余地（下面要往两边各挪好几格）
$中 = $win.X + $win.Width / 2
$甲 = $音 | Sort-Object { [Math]::Abs($_.X - $中) } | Select-Object -First 1
"  选中甲 @ $($甲.X),$($甲.Y)（带长 $($甲.宽)px）"

# 空白处：左边曲库那一带，离卷帘足够远 —— 挪到那儿读数就**回落到选中**，
# 于是量到的是「选中是谁」，而不是「鼠标正压着谁」。这是 21 号验过的那条回落。
$空白X = [int]($win.X + 60); $空白Y = [int]($win.Y + $win.Height * 0.6)

[V31]::Move($甲.X, $甲.Y)
$悬 = 读一套
断言真 '甲 上面确实压着一个音' $悬.可见 "悬停读数：$(串 $悬)"

[V31]::Click($甲.X, $甲.Y)
[V31]::Move($空白X, $空白Y)
$R0 = 读一套
断言真 '点中之后移开鼠标，读数回落到选中那个音' $R0.可见 "R0：$(串 $R0)"
if (-not $R0.可见) { throw '点了却没选中 —— 后面每一节都要量「选中的还是不是它」，没法继续' }
$R0串 = 串 $R0

# ---------- 1. 挪时间：这条会**真重排** Track.Notes ----------
#
# 挪时间是把起点改了，`WithNotes` 只按 StartTick 稳定排序 —— 起点一动就跨过邻居，
# 数组里前后位置真的换了。镜像要是按「挪之前那份内容」去找，重排之后就认成别人。
"`n=== 1. 挪时间（会重排数组）：→ ×8 再 ← ×8 ==="
for ($i = 0; $i -lt 8; $i++) { 键 '{RIGHT}' }
$R1 = 读一套
"  →×8 之后：$(串 $R1)"
断言真 '位置动了' ($R1['拍位'] -ne $R0['拍位'] -or $R1['小节'] -ne $R0['小节']) "$($R0['小节']) / $($R0['拍位']) → $($R1['小节']) / $($R1['拍位'])"
断言 '音高一个字节没动' $R1['音高'] $R0['音高']
断言 '时值一个字节没动' $R1['时值'] $R0['时值']
断言 '轨没动' $R1['轨'] $R0['轨']

for ($i = 0; $i -lt 8; $i++) { 键 '{LEFT}' }
$R1b = 读一套
断言真 '再 ←×8 回到原处 —— 五格逐字回到 R0' ((串 $R1b) -eq $R0串) "$R0串 → $(串 $R1b)"

# ---------- 2. 挪音高：这条**不重排**，但走的是同一条「改完重新解析选中」的路 ----------
"`n=== 2. 挪音高：↑ ×5 再 ↓ ×5 ==="
for ($i = 0; $i -lt 5; $i++) { 键 '{UP}' }
$R2 = 读一套
"  ↑×5 之后：$(串 $R2)"
断言真 '音高变了' ($R2['音高'] -ne $R0['音高']) "$($R0['音高']) → $($R2['音高'])"
断言 '位置一点没动（小节）' $R2['小节'] $R0['小节']
断言 '位置一点没动（拍位）' $R2['拍位'] $R0['拍位']
断言 '时值没动' $R2['时值'] $R0['时值']

for ($i = 0; $i -lt 5; $i++) { 键 '{DOWN}' }
$R2b = 读一套
断言真 '再 ↓×5 回到原处 —— 逐字回到 R0' ((串 $R2b) -eq $R0串) "$R0串 → $(串 $R2b)"

# ---------- 3. 改时值：Shift + ←/→ ----------
"`n=== 3. 改时值：Shift+→ 再 Shift+← ==="
键 '+{RIGHT}'
$R3 = 读一套
"  Shift+→ 之后：$(串 $R3)"
断言真 '时值变了' ($R3['时值'] -ne $R0['时值']) "$($R0['时值']) → $($R3['时值'])"
断言 '起点没动' $R3['拍位'] $R0['拍位']
断言 '音高没动' $R3['音高'] $R0['音高']

键 '+{LEFT}'
$R3b = 读一套
断言真 '再 Shift+← 回到原处 —— 逐字回到 R0' ((串 $R3b) -eq $R0串) "$R0串 → $(串 $R3b)"

# ---------- 4. 撤销 / 重做 ----------
#
# 撤销是**反向**改谱面：镜像得把值往回算。算错就丢选中 —— 而丢了之后
# 用户看到的不是报错，是「撤销了一下，选中的音跑到别处去了」。
"`n=== 4. 撤销 / 重做 ==="
键 '{UP}'
$改后 = 串 (读一套)
断言真 '↑ 之后确实改了' ($改后 -ne $R0串) "$R0串 → $改后"

键 '^z' 700
$撤 = 读一套
断言真 'Ctrl+Z 之后逐字回到 R0' ((串 $撤) -eq $R0串) "$R0串 → $(串 $撤)"

键 '^y' 700
$重 = 读一套
断言真 'Ctrl+Y 之后又回到改后那一份' ((串 $重) -eq $改后) "$改后 → $(串 $重)"

键 '^z' 700
$撤2 = 读一套
断言真 '再撤一次，又逐字回到 R0' ((串 $撤2) -eq $R0串) "$R0串 → $(串 $撤2)"

# ---------- 5. 连续操作：挪 → Ctrl+Z → 再挪 ----------
"`n=== 5. 挪一个音 → 立刻 Ctrl+Z → 再挪 ==="
键 '{RIGHT}'
$挪1 = 读一套
键 '^z' 700
$回 = 读一套
断言真 '第一次挪完撤销，逐字回到 R0' ((串 $回) -eq $R0串) "$R0串 → $(串 $回)"

键 '{RIGHT}'
$挪2 = 读一套
断言真 '再挪一次，落点和第一次**逐字相同**' ((串 $挪2) -eq (串 $挪1)) "$(串 $挪1) → $(串 $挪2)"

键 '^z' 700
断言真 '收尾撤销，回到 R0' ((串 (读一套)) -eq $R0串) ''

# ---------- 6. 删除 ----------
#
# 删完那个音**没有内容可找了** —— 这正是按值镜像的死角。19 号在同一个位置加了
# 「选中回落到时间上最近的邻居」，那条路也得重新认音。
"`n=== 6. 删掉选中的音 ==="
键 '{DEL}' 700
$删 = 读一套
"  删完之后：$(串 $删)"
断言真 '删完之后**不再**是原来那个音' ((串 $删) -ne $R0串) '要么回落到了邻居，要么整块藏起来'
断言真 '删完之后没有指着原来那个位置不放' `
  (($删.可见 -eq $false) -or ($删['拍位'] -ne $R0['拍位']) -or ($删['音高'] -ne $R0['音高'])) `
  "可见=$($删.可见)"

键 '^z' 700
[V31]::Move($空白X, $空白Y)
$撤选中 = 读一套
"  撤销之后「选中」落在：$(串 $撤选中)"
# 撤销**不动选中集** —— 选中是界面概念，不进撤销栈（撤销恢复的是文档）。
# 所以这里不能断言「撤销之后选中回到那个音」：它留在刚刚回落到的邻居上。
# 该量的是「选中还指着一个**存在**的音」，不是几个悬空坐标。
断言真 '撤销之后选中仍指着一个存在的音（不是空、不是悬空坐标）' $撤选中.可见 "可见=$($撤选中.可见)"

# 文档回来没有，靠**悬停原坐标**量：那个音应当原样躺回原处，读数逐字是 R0
[V31]::Move($甲.X, $甲.Y)
$删回 = 读一套
断言真 'Ctrl+Z 把那个音原样放回了原处（悬停原坐标 = R0）' ((串 $删回) -eq $R0串) "$R0串 → $(串 $删回)"
[V31]::Move($空白X, $空白Y)

# ---------- 7. 删掉一整条轨：选中集必须**清空** ----------
#
# 这是本单剩下的**唯一一处位置语义**：`NoteRef` 的轨那一半还是**下标**
#（删掉一条轨，后面的整体前移）。身份号是按轨连号发的，换一条轨照样能撞上一个号，
# 于是「认不出就丢掉」那道闸拦不住它 —— 窗口只能**显式交一份空的选中集**
#（OnTrackDeleteRequested 就是这么写的）。这条量的是那个显式动作真的落地了：
# 万一漏了，用户会看到选中莫名其妙落在**别条轨**的某个音上。
"`n=== 7. 删掉一整条轨（01）：选中集必须清空 ==="
function 点元素($e) {
  $r = $e.Current.BoundingRectangle
  [V31]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
}

# 先把选中重新放回甲身上（上一节撤销之后选中停在邻居上）
[V31]::Move($甲.X, $甲.Y)
[V31]::Click($甲.X, $甲.Y)
[V31]::Move($空白X, $空白Y)
$选甲 = 读一套
断言真 '删轨之前：甲 是选中的那一个' ((串 $选甲) -eq $R0串) "$R0串 → $(串 $选甲)"
断言 '而且它在 01 轨上' $选甲['轨'] '01'

$删轨 = 全部 $CT::Button | Where-Object { $_.Current.AutomationId -eq 'DeleteButton' } | Select-Object -First 1
if (-not $删轨) { throw '找不到 01 轨头上的「删除」按钮' }
"  01 轨头的「删除」@ $([int]$删轨.Current.BoundingRectangle.X),$([int]$删轨.Current.BoundingRectangle.Y)"
点元素 $删轨
Start-Sleep -Milliseconds 500

$确认 = 全部 $CT::Button | Where-Object { $_.Current.AutomationId -eq 'DeleteYesButton' } | Select-Object -First 1
断言真 '按下之后换出「删掉这条轨？」那一问' ([bool]$确认) '两下：先问一句，再删'
if (-not $确认) { throw '没换出确认那一问，删轨这条走不下去' }
点元素 $确认
Start-Sleep -Milliseconds 900

[V31]::Move($空白X, $空白Y)
$删轨后 = 读一套
"  删完之后：$(串 $删轨后)"
断言真 '删掉一整条轨之后，选中集**清空**（读数整块藏起来）' (-not $删轨后.可见) `
  '不能冒出一个别条轨上的音 —— 这是轨那一半还是下标留下的代价'
断言 '轨头 01 那块没了（旧 02 顶上来了）' (@(取文本 | Where-Object { $_.Current.Name -eq '01' }).Count) 1

# 撤销把轨放回来。**选中不会跟着回来**（选中集不进撤销栈），所以这里只量文档：
键 '^z' 900
[V31]::Move($甲.X, $甲.Y)
$轨回 = 读一套
断言真 'Ctrl+Z 把整条轨放回来了（悬停原坐标 = R0）' ((串 $轨回) -eq $R0串) "$R0串 → $(串 $轨回)"
[V31]::Move($空白X, $空白Y)

"`n$(if ($fail -eq 0) { '全过' } else { "$fail 条不过" })"
exit $fail
