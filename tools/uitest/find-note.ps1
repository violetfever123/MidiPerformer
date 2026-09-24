# 在第 N 轨里找个音符，报出它的屏幕坐标。
#
# 为什么不去盲扫：卷帘里音符是**一条条细横杠**（实测高 8px），
# 固定一个 y 横扫过去多半从两条杠中间的缝里穿过去，看着就像「这一轨没有音符」。
# 而按 y 加密扫要几千下，每下都要等 UI 反应，跑一趟几分钟。
#
# 改成看像素：PrintWindow 抓一张，音符那条颜色的横杠很长、很好认。
# 抓图坐标 == 屏幕坐标（窗口正好在 (0,0)，实测见 verify-18.ps1 的注释）。
#
# 音符色是实测出来的 #5E6B7A（轨 1、轨 2 都一样），上下各有一圈抗锯齿的过渡色，
# 所以判据是「核心色 ± 容差」，不是「亮度高」—— 用亮度会把轨头那一行白字也算进来。
#
# 加 -Blank 则反过来：报这一轨卷帘里**没有音符**的一个点（用来试「点空白」那条分支）。
# 🔴 92 号：这个点在**吐出来之前要过一道 FromPoint 核对**（见下面 -Blank 那一段）——
# 它以前结构性地落在**下一条轨**里：带的算式把下沿算到下一轨去了（实测见下面「车道」那一段）。
#
# 加 -Near "x,y" 则报**离这个点最近的、那个像素不是音符**的位置（x 不动，只上下挪）。
# 拖框那一版要用这个、不要用 -Blank：`-Blank` 是从扫查带的最下沿往上找的，
# 找出来的往往正好是带的**边缘那一行**，离轨的底边只有几像素 —— 而框选只要求
# **按下那一点**是空白，不要求整行都空，所以就近找一个空白像素才是对的要求。
#（这一段从前写的是「那一行常常已经在卷帘外面了 —— 按下去什么也没发生，
#  看着却像『框选没生效』」—— 92 号量出来的比这更重：那一行当时**已经在轨外面了**。）
#
# 用法: pwsh -File find-note.ps1 -Track 2 [-Blank | -Near "x,y"] [-Out scan.png] [-Drop px]
#
# 加 -Drop <px> 则把扫查带整体往下推这么多像素。给「轨道头上多摆了一行」的状态用
#（38 号：装备上「抽掉一段」之后，轨道头长出第二行，卷帘整条被推下去）——
# 那种时候按默认的那把尺子扫，扫到的是另一个音高行，量出来的东西全不对。
param([int]$Track = 2, [switch]$Blank, [string]$Near = '', [string]$Out = '', [int]$Drop = 0)
if (-not $Out) { $Out = Join-Path $PSScriptRoot 'shots/scan.png' }

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices;
public class FN {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}
"@

$音符色 = @(0x5E, 0x6B, 0x7A)
$容差 = 14
$最短杠 = 40        # 短于这个长度的亮横条不算音符（轨头上的白字、按钮边框都够不着）

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$p = Get-Process -Name dotnet, MidiPerformer -EA SilentlyContinue |
  Where-Object { $_.MainWindowTitle -eq 'MIDI 演奏器' } | Select-Object -First 1
if (-not $p) { throw 'MidiPerformer 没在跑' }
$h = $p.MainWindowHandle
$root = $AE::FromHandle($h)

# ---------- 抓图 ----------
$r = New-Object FN+RECT
[void][FN]::GetWindowRect($h, [ref]$r)
$w = $r.R - $r.L; $hh = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap($w, $hh)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$dc = $g.GetHdc()
[void][FN]::PrintWindow($h, $dc, 2)
$g.ReleaseHdc($dc); $g.Dispose()
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Out) | Out-Null
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)

# ---------- 轨头的 Y ----------
# ⚠️ 76 号：这里以前是「Text 名匹配 ^\d{2}$ 且 **x 落在 440..490**」—— 那是**旧版式**里轨号那格的位置。
# 40 号把侧栏撤掉之后轨号是**第 1 列**（MidiPerformer.App/Views/TrackLaneView.axaml:193 是列序的权威），
# x≈30，旧判据于是**一条轨都找不到、而且不响** —— 返回 0 条，调用方继续往下跑出「像结论的垃圾」。
# 现在照 tools/uitest/verify-36.ps1:255 的 轨号文字()：
#   先拿**折叠按钮**当锚定行（每条轨的轨头上都有一颗，按 Y 排就是轨序），
#   再在那一行的 Y 附近按**名字全等**（'02'）认轨号 —— 完全不依赖 x 区间，版式再挪也不瞎。
# 为什么不横扫 `^\d{2}$`：读数条上「轨」那一格**也是 '02' 这种两位数**，会撞名 —— verify-36.ps1:246 记着这笔账。
$tc = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Text)
$bc = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Button)
$锚 = @($root.FindAll($TS::Descendants, $bc) | Where-Object { $_.Current.Name -eq '折叠' } |
  Sort-Object { $_.Current.BoundingRectangle.Y })
if ($锚.Count -eq 0) {
  throw '一条轨都定位不到：UIA 树里连一颗「折叠」按钮都没有 —— 曲子没载进来，或者版式又变了。' +
        '（锚是「折叠」按钮、不是轨号文字，见 TrackLaneView.axaml:193；这里以前返回 0 条了事，于是坏了好几天没人知道 —— 76 号）'
}
function 轨号文字([int]$序) {
  if ($序 -lt 1 -or $序 -gt $锚.Count) { throw "第 $序 条轨不在场（共 $($锚.Count) 条轨）" }
  $y = $锚[$序 - 1].Current.BoundingRectangle.Y
  $c = @($root.FindAll($TS::Descendants, $tc) | Where-Object {
      $_.Current.Name -ceq ('{0:D2}' -f $序) -and
      [Math]::Abs($_.Current.BoundingRectangle.Y - $y) -lt 40 })
  if ($c.Count -ne 1) { throw "第 $序 条轨的轨号文字不唯一（$($c.Count) 个）—— 定位不到就是定位不到，不返回 0（76 号）" }
  $c[0]
}
$当前轨号 = 轨号文字 $Track
$top = [int]$当前轨号.Current.BoundingRectangle.Y
$bot = if ($Track -lt $锚.Count) { [int](轨号文字 ($Track + 1)).Current.BoundingRectangle.Y } else { $hh }

# ---------- 车道矩形（直接问 UIA，不再靠地标内缩猜） ----------
# 🔴 92 号：以前扫查带的下沿是拿**下一条轨的轨号文字**减出来的：`$Y1 = $bot - 20`。
# 实测（82 号探针2 第 1 趟）轨号文字(N).Y = 轨顶(N) + 22（160→182、784→806、1240→1262、1878→1900），
# 于是轨 N 的第一条候选行 = 轨顶(N+1) + 1 —— **1px 扎进下一条轨里**，扎在它的轨头上（不在卷帘上）。
# 那时报出来的空白点 (77,785)：FromPoint 上去是 `类=「TrackLaneView」 矩形=13,784 2374x456` 那条轨，
# 也就是**轨 2**（车道首尾相接：160+624=784、784+456=1240、1240+638=1878 ⇒ 边界那一行归下面那条轨）。
# 按下去落在轨 2 的轨头里（TrackLaneView 自己没有任何指针处理）⇒ 什么都没发生，
# 看着像「点空白这条分支没生效」，其实是**仪器悄悄指错了地方**。
# 现在直接要每一条 TrackLaneView 的矩形（类名是实测的，见上面那条探针），下沿从**本轨**的底边推。
$车道条件 = New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, 'TrackLaneView')
$车道 = @($root.FindAll($TS::Descendants, $车道条件) |
  Sort-Object { $_.Current.BoundingRectangle.Y })
if ($车道.Count -eq 0) {
  throw '一条车道都定位不到：UIA 树里没有 类名=TrackLaneView 的元素。' +
        '（这个脚本里别的定位函数定位不到都是抛，这里也不返回 0 —— 76 号那条教训）'
}
if ($车道.Count -ne $锚.Count) {
  throw "车道数和轨数对不上（车道 $($车道.Count) 条，轨 $($锚.Count) 条）—— 定位不可信，不拿它算扫查带"
}

function 本轨矩形 {
  if ($Track -lt 1 -or $Track -gt $车道.Count) { throw "第 $Track 条车道不在场（共 $($车道.Count) 条）" }
  $车道[$Track - 1].Current.BoundingRectangle
}

# ---------- 下面两把是**纯尺子**（不碰窗口、不看像素，给定数字就能算） ----------
# 分开写是为了能离线复算：92 号拿 82 号那两趟的地标读数重放过它们，逐行打出「老算式落在哪条轨、新算式落在哪条轨」。
#
# 卷帘带的下沿：本轨矩形的底边往上缩 2px（缩是为了不压在下一条轨的边界那一行上），再夹到图里。
function 卷帘带下沿([int]$本轨底, [int]$图高) {
  [Math]::Min($本轨底 - 2, $图高 - 10)
}
# 核对「这一点到底落在哪条轨上」：核过就是本轨 ⇒ 返回空串；核不过 ⇒ 返回**为什么不算过**（一句话，给人看着办）。
# 命中类 != 'TrackLaneView' 时**不看矩形**（那种时候矩形可能是 $null）。
function 轨核对([string]$命中类, $命中矩形, $期望矩形) {
  if ($命中类 -ne 'TrackLaneView') {
    if ($命中类) { return "那一点（连它往上找 12 层）都不是 TrackLaneView —— 点上的类名是「$命中类」" }
    return '那一点（连它往上找 12 层）都不是 TrackLaneView —— 连点上的类名都问不到'
  }
  if ([Math]::Abs($命中矩形.Top - $期望矩形.Top) -gt 1 -or
      [Math]::Abs($命中矩形.Bottom - $期望矩形.Bottom) -gt 1) {
    return "那一点落在**另一条轨**上（命中 $([int]$命中矩形.X),$([int]$命中矩形.Y) " +
           "$([int]$命中矩形.Width)x$([int]$命中矩形.Height) / 本轨 $([int]$期望矩形.X),$([int]$期望矩形.Y) " +
           "$([int]$期望矩形.Width)x$([int]$期望矩形.Height)）"
  }
  return ''
}

# 轨头那一行自己占 ~90px（轨名、音色、移调那些控件），跳过去再找。
#
# **要夹到窗口里**：轨多的时候列表会滚动，滚出去的轨头 Y 是负的，
# 拿负数去 GetPixel 会一路抛越界，脚本卡死在那儿看不出所以然。
#
# ⚠️ 76 号：左界以前写死 `$X0 = 460` —— 那是「从轨头往右开始扫」的意思，
# 460 在旧版式里正落在轨号那格（440..490）上。新版式轨号在 x≈30，
# 写死 460 会把**卷帘最左边 424px 整个跳过**：早段的音扫不到，
# `-Near` / `-Blank` 更会报出「根本不是空白」的点（verify-19 取空白点那一步就吃这个）。
# 现在左界从**上面定位到的轨号文字**的 X 推出来（同 verify-36.ps1:305 的 $left），版式怎么挪都对。
$X0 = [Math]::Max([int]($当前轨号.Current.BoundingRectangle.X - $r.L) + 4, 0)
$X1 = [Math]::Min(2500, $w - 20)
$Y0 = [Math]::Max($top + 60 + $Drop, 0)
$本轨 = 本轨矩形
$本轨底 = [int]$本轨.Bottom
# 🔴 92 号：`-Blank` / `-Near` 的上界改成**本轨矩形**推出来的（以前是 `$bot - 20`，即下一条轨的地标，见上）。
# 找音符那一条分支不改（它的 `$bot - 20` 只当下界用，多点几像素无所谓，少动少错）。
$Y1 = if ($Blank -or $Near) { 卷帘带下沿 $本轨底 $hh } else { [Math]::Min($bot - 20, $hh - 10) }
if ($Y1 -le $Y0) { "轨 $Track 整条都在窗口外（轨头 Y=$top，窗口高 $hh）—— 先把它滚进视野"; exit 1 }
"抓图 $w x $hh -> $Out"
"轨 $Track 的车道: $([int]$本轨.X),$([int]$本轨.Y) $([int]$本轨.Width)x$([int]$本轨.Height)（UIA 的答案）"
"轨 $Track 的卷帘: y $Y0..$Y1, x $X0..$X1"

function 是音符色($c) {
  [Math]::Abs($c.R - $音符色[0]) -le $容差 -and
  [Math]::Abs($c.G - $音符色[1]) -le $容差 -and
  [Math]::Abs($c.B - $音符色[2]) -le $容差
}
# 这一行里最长的一条音符色横杠；返回 @(起, 止) 或 $null
function 这一行的杠([int]$y) {
  $起 = -1; $最起 = -1; $最长 = 0
  for ($x = $X0; $x -lt $X1; $x++) {
    if (是音符色 $bmp.GetPixel($x, $y)) {
      if ($起 -lt 0) { $起 = $x }
    } elseif ($起 -ge 0) {
      if ($x - $起 -gt $最长) { $最长 = $x - $起; $最起 = $起 }
      $起 = -1
    }
  }
  if ($起 -ge 0 -and ($X1 - $起) -gt $最长) { $最长 = $X1 - $起; $最起 = $起 }
  # 括号不能省：PS 里逗号比 `+` 结合得紧，`@($a, $b + $c - 1)` 会算成 `($a,$b) + $c - 1`
  if ($最长 -ge $最短杠) { return @($最起, ($最起 + $最长 - 1)) } else { return $null }
}

if ($Near) {
  # 上下各探一段，报第一个「自己跟上下各 2px 都不是音符色」的像素 ——
  # 留 2px 是躲开横杠边缘那圈抗锯齿的过渡色（它在容差之外，只判自己会误判成空白）
  $parts = $Near -split ','
  $nx = [int]$parts[0]; $ny = [int]$parts[1]
  $空 = {
    param($y)
    if ($y -lt $Y0 -or $y -ge $Y1) { return $false }
    foreach ($d in -2, 0, 2) {
      $yy = $y + $d
      if ($yy -lt 0 -or $yy -ge $hh) { return $false }
      if (是音符色 $bmp.GetPixel($nx, $yy)) { return $false }
    }
    return $true
  }
  for ($k = 0; $k -le 120; $k++) {
    foreach ($y in @(($ny - $k), ($ny + $k))) {
      if (-not (& $空 $y)) { continue }
      $bmp.Dispose()
      "离 ($nx,$ny) 最近的空白像素：($nx, $y)   上下挪了 $($y - $ny)px"
      "$nx,$y"
      exit 0
    }
  }
  $bmp.Dispose()
  "($nx,$ny) 上下 120px 以内找不到空白像素"
  exit 1
}

if ($Blank) {
  # 找空白：从卷帘底下往上找一行，整行没有长横杠，且那个点自己也不是音符色。
  # 两条都要 —— 只判「这一行没有长杠」的话，可能正好戳在一个短音符上。
  #
  # 🔴 92 号：**报点之前自己 FromPoint 核一次** —— 拿算出来的屏幕坐标去问 UIA「这一点是谁」，
  # 一路往上走到 TrackLaneView，看它的矩形是不是**本轨**的。核不上就 exit 1（大声失败），
  # **绝不返回一个坏点**：这一族最贵的缺陷不是「找不到点」，是**仪器悄悄指错了地方** ——
  # 调用方拿到一个看着完全正常的读数，按下去却打在另一条轨上（见上面「车道」「下沿」那两段注释）。
  # 顺带说清楚这一核对能管到哪儿：UIA 在这一层能认到的最小单位是**整条 TrackLaneView**
  #（轨头里那些控件自己认得出来，空着的 padding 认不出来），所以它管的是「落对了轨」；
  # 「落在这一轨的卷帘上而不是轨头上」是**扫查方向**保的：从本轨底边往上找，头几行必然是卷帘。
  $期望 = 本轨
  for ($y = $Y1 - 1; $y -ge $Y0; $y--) {
    if (这一行的杠 $y) { continue }
    if (是音符色 $bmp.GetPixel($X0 + 40, $y)) { continue }
    $sx = $X0 + 40 + $r.L
    $sy = $y + $r.T
    # 抓图坐标 == 屏幕坐标是**实测**的（窗口在 (0,0)），但这里还是把窗口左上角加回去：不靠这条实测。
    $点 = $AE::FromPoint((New-Object System.Windows.Point($sx, $sy)))
    $命中 = $null
    $e = $点
    for ($i = 0; $i -le 12 -and $null -ne $e; $i++) {
      if ($e.Current.ClassName -eq 'TrackLaneView') { $命中 = $e; break }
      $e = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($e)
    }
    $为什么 = if ($命中) {
      轨核对 'TrackLaneView' $命中.Current.BoundingRectangle $期望
    } else {
      轨核对 $(if ($点) { $点.Current.ClassName } else { '' }) $null $期望
    }
    if ($为什么) {
      $bmp.Dispose()
      "⚠ 算出来的空白点 ($sx, $sy) 核不上：$为什么"
      "   本轨：轨 $Track，车道矩形 $([int]$期望.X),$([int]$期望.Y) $([int]$期望.Width)x$([int]$期望.Height)"
      "   车道们（UIA 的答案，按 Y 排）："
      for ($i = 0; $i -lt $车道.Count; $i++) {
        $b = $车道[$i].Current.BoundingRectangle
        $标 = if (($i + 1) -eq $Track) { '  <= 本轨' } else { '' }
        "     [$($i + 1)] $([int]$b.X),$([int]$b.Y) $([int]$b.Width)x$([int]$b.Height)$标"
      }
      "   这一次的点做废 —— 不返回坏点，先把定位修对（92 号）"
      exit 1
    }
    $bmp.Dispose()
    "轨 $Track 的空白点在 ($sx, $sy)（FromPoint 核过：这一点上是本轨的 TrackLaneView）"
    "$sx,$sy"
    exit 0
  }
  $bmp.Dispose()
  "轨 $Track 里找不到空白点（y $Y0..$Y1 每一行都有音符）"
  exit 1
}

# 从上往下找出**第一条横杠**，取它最厚那几行的中点 —— 落在杠身上，不落在边缘
$行 = @()
for ($y = $Y0; $y -lt $Y1; $y++) {
  $杠 = 这一行的杠 $y
  if ($杠) { $行 += ,@{ Y = $y; 起 = $杠[0]; 止 = $杠[1] } }
  elseif ($行.Count -gt 0) { break }        # 已经量完一条杠（中间断了就是杠到头了）
}
$bmp.Dispose()

if ($行.Count -eq 0) { "轨 $Track 里没找到音符（y $Y0..$Y1）"; exit 1 }
$中行 = $行[[int]($行.Count / 2)]           # 杠的中间那行，最稳
$cx = [int](($中行.起 + $中行.止) / 2)
$cy = $中行.Y
"找到音符：屏幕 ($cx, $cy)   杠厚 $($行.Count)px，横跨 x $($中行.起)..$($中行.止)"
"$cx,$cy"
