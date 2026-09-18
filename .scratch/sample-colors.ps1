# 数一数截图里都有哪些颜色。
# 用来抓"某个控件没吃到令牌、露出了 Fluent 或系统的颜色"这类只能看出来的事 ——
# 眼睛分不清 #6E7987 和 #787879，数出来就一目了然。
param(
    [Parameter(Mandatory = $true)][string]$Image,
    [int]$SkipTop = 120,   # 跳过系统标题栏：它是 Windows 按强调色画的，不归我们管
    [int]$Top = 12,
    # 只数某一竖条（给滚动条那种细长控件用）—— 给了就只输出这一条的色块
    [int]$Column = -1,
    [int]$ColumnWidth = 6
)

Add-Type -AssemblyName System.Drawing

$bmp = [System.Drawing.Bitmap]::FromFile($Image)

if ($Column -ge 0) {
    # 沿这一竖条自上而下，把连续同色的像素并成段 —— 眼睛分不清的两个灰，这里分得清
    $runStart = $SkipTop; $runColor = $null
    for ($y = $SkipTop; $y -lt $bmp.Height; $y++) {
        $c = $bmp.GetPixel($Column, $y)
        $key = "#{0:X2}{1:X2}{2:X2}" -f $c.R, $c.G, $c.B
        if ($key -ne $runColor) {
            if ($null -ne $runColor -and ($y - $runStart) -ge 3) {
                Write-Output ("  y {0,4}-{1,4}  {2}" -f $runStart, ($y - 1), $runColor)
            }
            $runColor = $key; $runStart = $y
        }
    }
    # 收尾那一段：最后一段一直画到图片底边，循环里没有"下一次变色"来触发输出。
    # 不补这一下，最底下那条色带会被静默吞掉 —— 而"底下有没有漏出系统色"正是要查的事。
    if ($null -ne $runColor -and ($bmp.Height - $runStart) -ge 3) {
        Write-Output ("  y {0,4}-{1,4}  {2}" -f $runStart, ($bmp.Height - 1), $runColor)
    }
    $bmp.Dispose()
    exit 0
}

$counts = @{}

for ($y = $SkipTop; $y -lt $bmp.Height; $y++) {
    for ($x = 0; $x -lt $bmp.Width; $x++) {
        $c = $bmp.GetPixel($x, $y)
        $key = "#{0:X2}{1:X2}{2:X2}" -f $c.R, $c.G, $c.B
        $counts[$key] = 1 + ($counts[$key] ?? 0)
    }
}
$bmp.Dispose()

Write-Output "共 $($counts.Count) 种颜色，最常见的 $Top 种："
$counts.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First $Top |
    ForEach-Object { Write-Output ("  {0}  {1}" -f $_.Key, $_.Value) }

# 系统强调色（洋红 #BF0077）—— 我们的界面里不该有它
$accent = @{ R = 0xBF; G = 0x00; B = 0x77 }
$hits = 0
foreach ($k in $counts.Keys) {
    $r = [Convert]::ToInt32($k.Substring(1, 2), 16)
    $g = [Convert]::ToInt32($k.Substring(3, 2), 16)
    $b = [Convert]::ToInt32($k.Substring(5, 2), 16)
    if ([Math]::Abs($r - $accent.R) -le 6 -and [Math]::Abs($g - $accent.G) -le 6 -and
        [Math]::Abs($b - $accent.B) -le 6) { $hits += $counts[$k] }
}
Write-Output "系统强调色 #BF0077 附近的像素：$hits"
