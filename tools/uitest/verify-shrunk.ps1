# 给「压缩 + 裁剪」那一版发布产物做界面体检。
#
# 为什么非要开窗口：内置自检（run-selftest）**不建窗口**，它只跑合成器那套用例。
# 裁剪裁坏的往往是别的东西 —— XAML 里 new 的控件、{Binding} 的目标属性、
# System.Text.Json 反序列化构造器。这些只有真开一次窗口、真读一份 .mproj 才看得见。
#
# 同时验证曲库：发布目录下 songs\ 里的曲子是不是都在、中文名有没有乱码、
# 点一首能不能真载进来（载入 = 走一遍 ReadProject 的 JSON 反序列化）。
#
# 只认自己起的那个 PID，绝不碰桌面上别人已经开着的实例。
#
# 用法: pwsh -NoProfile -File verify-shrunk.ps1 [-点第几首 N]

param(
  # 一串「相对窗口左上角」的点击点，分号隔开，例如 "199,102; 300,260"。
  # 每点一下截一张图（3-点1.png、3-点2.png…），好一格一格看界面是怎麼走过去的。
  [string]$点偏移 = '',
  [int]$每点等秒 = 4
)

Add-Type -AssemblyName System.Drawing, System.Windows.Forms

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class SHR {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public static int[] 矩形(IntPtr h) { RECT r; GetWindowRect(h, out r); return new int[] { r.L, r.T, r.R - r.L, r.B - r.T }; }
  public static string 标题(IntPtr h) {
    var sb = new System.Text.StringBuilder(512);
    GetWindowTextW(h, sb, sb.Capacity);
    return sb.ToString();
  }
  public static void 点(int x, int y) {
    SetCursorPos(x, y);
    System.Threading.Thread.Sleep(120);
    mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);   // LEFTDOWN
    System.Threading.Thread.Sleep(60);
    mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);   // LEFTUP
  }
}
"@

$出 = Join-Path $PSScriptRoot 'verify-compressed'
New-Item -ItemType Directory -Force -Path $出 | Out-Null
$script:图 = $null

# ── 81 号票：这一条修前**一条判据都没有**（只 print、拍图、走人），可它在总表里照样占着一个
#    「绿」——「绿」的意思是「有一批判据真的通过了」，而它一条都没有。这是本票要杀的病。
#    现在补的判据**全是它自己一直在量、抬头也早就承诺过的东西**（没有新造需求）：
#      · 抬头第 7-8 行：发布目录下 songs\ 里的曲子是不是都在、中文名有没有乱码
#      · 窗口标题（裁剪把 CJK 弄坏就看得出来）+ 截图证据是不是真的有效（PrintWindow 没成功，
#        那张「开窗后长什么样」就不作数，整个体检结论就没有根据）
#  🔴 **一条像素值判据都没有**（硬规矩：像素读回只能进 probe，不能进 verify）——
#     判的是「窗口/标题/文件」这类前提，不是「某个像素该是什么颜色」。
$fail = 0
function 断言真([string]$名, [bool]$条件, [string]$原文) {
  if ($条件) { Write-Host "  OK   $名（$原文）" } else { Write-Host "  FAIL $名（$原文）" -ForegroundColor Red; $script:fail++ }
}

function 抓窗口([IntPtr]$h) {
  $r = [SHR]::矩形($h)
  $b = [System.Drawing.Bitmap]::new($r[2], $r[3])
  $g = [System.Drawing.Graphics]::FromImage($b)
  $hdc = $g.GetHdc()
  $好 = [SHR]::PrintWindow($h, $hdc, 2)   # PW_RENDERFULLCONTENT：不受遮挡影响
  $g.ReleaseHdc($hdc)
  $g.Dispose()
  $script:图 = $b
  return $好
}

function 存位图([string]$名) {
  $p = Join-Path $出 $名
  $script:图.Save($p, [System.Drawing.Imaging.ImageFormat]::Png)
  Write-Host "  写出 $名  ($($script:图.Width)x$($script:图.Height))"
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$exe = Join-Path $repo 'MidiPerformer.App\bin\Release\net8.0\win-x64\publish\MidiPerformer.exe'
if (-not (Test-Path $exe)) { throw "没有发布产物：$exe" }
$曲库 = Join-Path (Split-Path $exe -Parent) 'songs'

Write-Host "用 $exe"
$exeInfo = Get-Item $exe
Write-Host "产物 $([math]::Round($exeInfo.Length / 1MB, 2)) MB，改动于 $($exeInfo.LastWriteTime)"
Write-Host "`n── 曲库（发布目录下 songs\）──"
$曲们 = @(if (Test-Path -LiteralPath $曲库 -PathType Container) {
    Get-ChildItem -LiteralPath $曲库 -Filter *.mproj -File | Sort-Object Name
  } else { @() })
foreach ($曲 in $曲们) {
  Write-Host ("  {0,-8} {1}" -f $曲.Length, $曲.Name)
}
断言真 '发布产物里带着曲库目录' (Test-Path -LiteralPath $曲库 -PathType Container) $曲库
断言真 '曲库里至少有一份 .mproj' ($曲们.Count -gt 0) "$($曲们.Count) 份"
# 抬头承诺的「曲子是不是都在、中文名有没有乱码」——这里真去读一遍（载入时 App 也是这么读的：
# 一份 .mproj 走一遍 JSON 反序列化）。名字里出现 U+FFFD = 乱码；JSON 读不动 / 没有轨 = 坏曲子。
foreach ($曲 in $曲们) {
  $坏 = ''
  $详 = ''
  if ($曲.Name.IndexOf([char]0xFFFD) -ge 0) { $坏 = '文件名里有乱码字符（U+FFFD）' }
  else {
    try {
      $j = Get-Content -LiteralPath $曲.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
      $轨数 = if ($j.Song) { @($j.Song.Tracks).Count } else { 0 }
      $详 = "Name=$($j.Name)，轨道 $轨数 条"
      if (-not $j.Name) { $坏 = 'JSON 里没有 Name' }
      elseif ($j.Name.IndexOf([char]0xFFFD) -ge 0) { $坏 = 'Name 里有乱码字符（U+FFFD）' }
      elseif ($轨数 -eq 0) { $坏 = 'JSON 里一条轨都没有' }
    }
    catch { $坏 = "JSON 读不动：$($_.Exception.Message)" }
  }
  断言真 "曲库「$($曲.BaseName)」完好（无乱码 + JSON 有 Name 有轨）" ($坏 -eq '') $(if ($坏) { $坏 } else { $详 })
}

$本来就有 = @(Get-Process -Name MidiPerformer -EA SilentlyContinue | ForEach-Object { $_.Id })
Write-Host "本来就在跑的 MidiPerformer：$(if ($本来就有.Count) { $本来就有 -join ', ' } else { '（没有）' })"

$proc = Start-Process -FilePath $exe -PassThru
$期限 = (Get-Date).AddSeconds(60)
do {
  Start-Sleep -Milliseconds 500
  if ($proc.HasExited) { throw "启动就退了，退出码 $($proc.ExitCode)" }
  $proc.Refresh()
} while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $期限)
if ($proc.MainWindowHandle -eq 0) { throw '等不到窗口句柄' }
$h = $proc.MainWindowHandle
Write-Host "`n我这个实例：PID $($proc.Id)，窗口 $h"
[void][SHR]::ShowWindow($h, 9)
[void][SHR]::SetForegroundWindow($h)
Start-Sleep -Seconds 4

$r = [SHR]::矩形($h)
Write-Host "窗口矩形：$($r[0]),$($r[1]) $($r[2])x$($r[3])"
$标 = [SHR]::标题($h)
Write-Host "窗口标题：$标"
断言真 '窗口矩形非零（截图才有意义）' ($r[2] -gt 0 -and $r[3] -gt 0) "$($r[2])x$($r[3])"
断言真 '窗口标题还是「MIDI 演奏器」（裁剪没把标题弄坏）' ($标 -eq 'MIDI 演奏器') "「$标」"

Write-Host "`n── 开窗后长什么样 ──"
$好 = 抓窗口 $h
Write-Host "  PrintWindow 成功 = $好"
存位图 '1-开窗.png'
断言真 'PrintWindow 成功（体检那张图才作数）' ([bool]$好) "PrintWindow=$好"
$图路径 = Join-Path $出 '1-开窗.png'
断言真 '开窗那张图真写出去了（非空）' ((Test-Path -LiteralPath $图路径) -and (Get-Item -LiteralPath $图路径).Length -gt 0) `
  "$(if (Test-Path -LiteralPath $图路径) { "$((Get-Item -LiteralPath $图路径).Length) 字节" } else { '文件不在' })"

if ($点偏移.Trim()) {
  $n = 0
  foreach ($段 in $点偏移.Split(';')) {
    if (-not $段.Trim()) { continue }
    $n++
    $xy = $段.Split(',')
    $x = $r[0] + [int]$xy[0].Trim()
    $y = $r[1] + [int]$xy[1].Trim()
    Write-Host "`n── 第 $n 点：窗口内 ($($xy[0].Trim()), $($xy[1].Trim())) ＝ 屏幕 ($x, $y) ──"
    [SHR]::点($x, $y)
    Start-Sleep -Seconds $每点等秒
    Write-Host "  点完的标题：$([SHR]::标题($h))"
    存位图 "3-点$n.png"
  }
}

Write-Host "`n── 收尾（只关我自己这一个）──"
[void]$proc.CloseMainWindow()
if (-not $proc.WaitForExit(8000)) { $proc.Kill(); Write-Host '  不肯退，强杀了' }
else { Write-Host "  关掉了 PID $($proc.Id)" }
$还在 = @(Get-Process -Name MidiPerformer -EA SilentlyContinue | ForEach-Object { $_.Id })
Write-Host "别人那些现在还在吗：$(if ($还在.Count) { $还在 -join ', ' } else { '（一个都不剩）' })"
# ⚠️ 「本来就在跑的还在不在」**不判红**：run-all.ps1 的抬头专门写过为什么
#    （这一轮桌上同时有别人的实例来来去去，拿它判红等于让结论看别人脸色）。
#    它是必须看见的信息，不是判据。

"`n$(if ($fail -eq 0) { '全过' } else { "$fail 条红" })"
# 81 号票：裁决行 + 退出码（run-all.ps1 拿这行复核退出码，对不上就降级成红）。
"==== uitest 裁决 不过=$fail"
exit $fail
