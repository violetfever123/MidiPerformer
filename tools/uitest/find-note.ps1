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
#
# 加 -Near "x,y" 则报**离这个点最近的、那个像素不是音符**的位置（x 不动，只上下挪）。
# 拖框那一版必须用这个，不能用 -Blank：`-Blank` 是从扫查带的最下沿往上找的，
# 找出来的往往正好是带的**边缘那一行**，那一行常常已经在卷帘外面了 —— 按下去什么也没发生，
# 看着却像「框选没生效」。而框选只要求**按下那一点**是空白，不要求整行都空，
# 所以就近找一个空白像素才是对的要求。
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
$Y1 = [Math]::Min($bot - 20, $hh - 10)
if ($Y1 -le $Y0) { "轨 $Track 整条都在窗口外（轨头 Y=$top，窗口高 $hh）—— 先把它滚进视野"; exit 1 }
"抓图 $w x $hh -> $Out"
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
  for ($y = $Y1 - 1; $y -ge $Y0; $y--) {
    if (这一行的杠 $y) { continue }
    if (是音符色 $bmp.GetPixel($X0 + 40, $y)) { continue }
    $bmp.Dispose()
    "轨 $Track 的空白点在 ($($X0 + 40), $y)"
    "$($X0 + 40),$y"
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
