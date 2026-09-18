# 把正在跑的那个窗口按「窗口自己画一遍」截下来（PrintWindow），
# 再裁两条出来：一条轨头、一条「已折叠」。
#
# 为什么不用 drive-ui.ps1 里的 Shot：那条走 Windows.Forms 的 Screen.Bounds +
# CopyFromScreen，在 200% 缩放下量到的是虚拟坐标，只抠到屏幕左上角一块 ——
# 截出来看着像窗口被切了，其实窗口好好的（UIA 量出来 3014x1729）。
# PrintWindow 让窗口自己画，尺寸取 GetWindowRect，不受这件事影响。

param([string]$OutDir = "$PSScriptRoot\shots")

Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Cap {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

function Get-Root {
  $p = Get-Process -Name dotnet -EA SilentlyContinue |
    Where-Object { $_.MainWindowTitle -eq 'MIDI 演奏器' } | Select-Object -First 1
  if (-not $p) { throw 'MidiPerformer 没在跑' }
  return @{ Proc = $p; Root = $AE::FromHandle($p.MainWindowHandle) }
}
function Find-All($root, $type) {
  $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $type)
  return $root.FindAll($TS::Descendants, $cond)
}
function Invoke($e) { $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# 先把第二条轨收起来，这样一张图里「正常的一条」和「收起来的一条」都有
$app = Get-Root
$folds = @(Find-All $app.Root $CT::Button | Where-Object { $_.Current.Name -eq '折叠' })
"折叠按钮 $($folds.Count) 个"
if ($folds.Count -ge 2) {
  Invoke $folds[1]
  Start-Sleep -Milliseconds 900
  "第二条轨收起来了"
}

$app = Get-Root
$h = $app.Proc.MainWindowHandle
$r = New-Object Cap+RECT
[void][Cap]::GetWindowRect($h, [ref]$r)
$w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
"窗口 $w x $ht @ $($r.Left),$($r.Top)"

$bmp = New-Object System.Drawing.Bitmap $w, $ht
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[void][Cap]::PrintWindow($h, $hdc, 2)   # PW_RENDERFULLCONTENT
$g.ReleaseHdc($hdc); $g.Dispose()
$full = Join-Path $OutDir '10-full.png'
$bmp.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
"saved $full"

function Crop($src, $x, $y, $cw, $ch, $name) {
  $rect = New-Object System.Drawing.Rectangle $x, $y, $cw, $ch
  $c = $src.Clone($rect, $src.PixelFormat)
  $p = Join-Path $OutDir $name
  $c.Save($p, [System.Drawing.Imaging.ImageFormat]::Png)
  $c.Dispose()
  "saved $p"
}

# 裁哪一条不靠猜偏移 —— 猜错过一次（按上次量到的 y 裁，结果裁到卷帘中间去了，
# 因为窗口尺寸在那两次之间变过）。改成当场问 UIA：那颗「展开」按钮和那行「已折叠」在哪。
$foldBtn = Find-All $app.Root $CT::Button | Where-Object { $_.Current.Name -eq '展开' } | Select-Object -First 1
$stripTxt = Find-All $app.Root $CT::Text | Where-Object { $_.Current.Name -eq '已折叠' } | Select-Object -First 1
if ($foldBtn) {
  $fr = $foldBtn.Current.BoundingRectangle
  "$('展开') 按钮 @ $($fr.Left),$($fr.Top)  $($fr.Width)x$($fr.Height)"
  # 那条轨的头 + 它下面的「已折叠」
  $top = [int]$fr.Top - 50
  Crop $bmp 430 $top 2100 260 '11-collapsed-lane.png'
}
if ($stripTxt) {
  $sr = $stripTxt.Current.BoundingRectangle
  "'已折叠' @ $($sr.Left),$($sr.Top)  $($sr.Width)x$($sr.Height)"
  Crop $bmp ([int]$sr.Left - 30) ([int]$sr.Top - 30) 900 120 '12-strip.png'
}
# 最上面那条正常轨的轨头，用来看这一行挤不挤
$n = Find-All $app.Root $CT::Button | Where-Object { $_.Current.Name -eq '折叠' } | Select-Object -First 1
if ($n) {
  $nr = $n.Current.BoundingRectangle
  Crop $bmp 430 ([int]$nr.Top - 45) 2100 110 '13-head.png'
}
$bmp.Dispose()
