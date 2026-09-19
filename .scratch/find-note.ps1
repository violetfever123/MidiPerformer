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
# 用法: pwsh -File find-note.ps1 -Track 2 [-Blank] [-Out scan.png]
param([int]$Track = 2, [switch]$Blank, [string]$Out = '.scratch/shots/scan.png')

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
$tc = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Text)
$heads = @($root.FindAll($TS::Descendants, $tc)) | Where-Object { $r2 = $_.Current.BoundingRectangle
    $_.Current.Name -match '^\d{2}$' -and $r2.X -gt 440 -and $r2.X -lt 490 } |
  Sort-Object { $_.Current.BoundingRectangle.Y }
if ($Track -gt $heads.Count) { throw "只有 $($heads.Count) 条轨" }
$top = [int]$heads[$Track - 1].Current.BoundingRectangle.Y
$bot = if ($Track -lt $heads.Count) { [int]$heads[$Track].Current.BoundingRectangle.Y } else { $hh }

# 轨头那一行自己占 ~90px（轨名、音色、移调那些控件），跳过去再找。
#
# **要夹到窗口里**：轨多的时候列表会滚动，滚出去的轨头 Y 是负的，
# 拿负数去 GetPixel 会一路抛越界，脚本卡死在那儿看不出所以然。
$X0 = 460; $X1 = [Math]::Min(2500, $w - 20)
$Y0 = [Math]::Max($top + 60, 0)
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
