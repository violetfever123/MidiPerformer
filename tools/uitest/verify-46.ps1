#Requires -Version 5.1
<#
  46 号工单的实机验证：**曲库面板底下那层毛玻璃（F5）**。

  用法（一般由 .scratch 的两主题驱动脚本调；单独跑也行）：

      pwsh -NoProfile -File verify-46.ps1 -标签 深色
      pwsh -NoProfile -File verify-46.ps1 -标签 浅色

  这个脚本**只负责截图与取数**，它自己不改任何源文件、也不切主题 —— 主题是它在运行时
  用的那份构建决定的（App.axaml 的 RequestedThemeVariant），切主题要重新编译。

  两条判据都是**看**出来的（票里写明归上机截图），所以脚本干的是两件事：
    ① 保证「看到的是该看的那块」——结构断言：曲库窗开着、面板找得到、图存下来了；
    ② 把几个不靠眼睛的数打出来，给看图的人一个参照。

  图存到 `.scratch/shots/46-<标签>/`：
    曲库窗.png      —— 曲库窗自己（毛玻璃在这儿）
    主窗.png        —— 主窗自己（PrintWindow 不含上面那扇模态窗，所以这是「糊之前」）
    对照-糊前.png   —— 主窗那张里、**面板占的那块屏幕矩形**，裁下来
    对照-糊后.png   —— 曲库窗那张里、同一块屏幕矩形
    整屏.png        —— 屏幕上那一坨（含窗口外的桌面），看整体的
  前两张的对照是最要紧的：同一块屏幕区域、同一套几何，一张实、一张糊。

  ⚠️ 这一票的落点跟票面不一样：票里说糊的是「窗内浮在卷帘上的那个选取器」，
     那个东西 40 号票已经撤了（曲库搬进独立窗口）。面板今天住在一个模态窗里，
     它自己底下只有窗口底色，**真正有东西可透的是身后主窗那份卷帘** ——
     所以曲库窗在 OnOpened 里把 Owner.Content 交上来当底子。截图验的就是这件事。

  ⚠️ 截图之前先把 Carulli 装上：空卷帘是一片均匀的底，糊不糊**看不出来**。
     装曲子是只读的（不碰工程文件），跑完还要逐文件比对 md5。

  ⚠️ 不用 uitest-lib 的 `起窗口`：60 号票刚给启动加了一颗**模态**的提权框，
     它开着的时候主窗是禁用的，`Take` 抢不到前台就抛。这里的 `起窗口带清障` 先把那颗框按掉。
#>
param([string]$标签 = '深色')

# ⚠️ 必须在点源 uitest-lib **之前**声明 DPI 感知，否则它的分辨率前提会误判。
#
# 这台机器 15:12 前后显示缩放从 100% 变成了 200%（面板物理一直是 3072x1920）：
# 一个 DPI 不感知的进程（pwsh 默认）读 Screen.Bounds 拿到的是**虚拟化后的一半** 1536x960，
# 于是 uitest-lib 那句「主屏只有 1536x960 —— 这套脚本要 2360x1520 才摆得下」把两轮全都挡在门外，
# 而我这边量到的实际屏幕明明是 3072x1920。
#
# 只影响本进程怎么读「屏幕有多大」：UIA 矩形、GetWindowRect、PrintWindow 本来就是物理像素，
# 所以脚本里所有的量法一个都不用改（$缩放 也是从窗口宽度反推的，不写死）。
Add-Type -Namespace Dpi -Name Ctx -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetProcessDpiAwarenessContext(System.IntPtr c);
'@
[void][Dpi.Ctx]::SetProcessDpiAwarenessContext([IntPtr](-4))

. (Join-Path $PSScriptRoot 'uitest-lib.ps1')

$深色 = ($标签 -ne '浅色')
$fail = 0
function 断言真([string]$名字, [bool]$条件, [string]$原文) {
  if ($条件) { "  OK   $名字（$原文）" } else { "  FAIL $名字（$原文）"; $script:fail++ }
}

$图 = Join-Path (Join-Path $PSScriptRoot '..\..\.scratch') "shots\46-$标签"
New-Item -ItemType Directory -Force -Path $图 | Out-Null

# 这几个名字 `别窗` / `曲库窗` 之流要在**脚本作用域**里存在（StrictMode 下没定义就抛）
$pid脚本 = 0
$h = [IntPtr]::Zero
$proc = $null
$root = $null
$跑完了 = $false

# =====================================================================
# UIA 取数（照抄 verify-40 的那一套，改都没改）
# =====================================================================
function 找类型($根, $类型) { if ($null -eq $根) { return @() } @($根.FindAll($TS::Descendants, (& $条件 $类型))) }
function 按id($根, [string]$id) {
  foreach ($t in @($CT::Text, $CT::Button, $CT::Edit, $CT::List, $CT::ListItem, $CT::Pane, $CT::Custom)) {
    $h2 = @(找类型 $根 $t | Where-Object { $_.Current.AutomationId -eq $id })
    if ($h2.Count) { return $h2[0] }
  }
  return $null
}
function 按名字($根, [string]$名) {
  foreach ($t in @($CT::MenuItem, $CT::Button, $CT::Text)) {
    $h2 = @(找类型 $根 $t | Where-Object { $_.Current.Name -eq $名 })
    if ($h2.Count) { return $h2 }
  }
  return @()
}
function 按种类([string]$名) {
  $出 = @()
  foreach ($t in @($CT::MenuItem, $CT::Button)) {
    foreach ($e in @(找类型 $root $t)) { if ($e.Current.Name -eq $名) { $出 += $e } }
  }
  $出
}
function 矩形($e) { $e.Current.BoundingRectangle }
# ⚠️ 数窗口个数**必须写 `@(别窗).Count`**，`@` 一个都不能省。
#    uitest-lib 往调用者作用域里漏了一个 Set-StrictMode -Version Latest；在它底下，
#    PowerShell 会把**单元素数组**从函数返回值里拆包成标量，而标量上没有 Count 这个属性 ——
#    「窗外正好只有一个窗口」是最常见的情形（曲库窗开着就是这样），于是
#    `(别窗).Count` 平时好好的、一旦只剩一个窗口就抛「找不到属性 Count」，而且是**脚本半路死掉**
#    那种失败（跑出来的图、验过的条目全作废）。46 号第一趟两轮就是这么一起断在 293 行的。
#    写成 @(别窗).Count 之后 0 个 / 1 个 / n 个都是对的。（`(别窗)[0]` 索引反而没事：
#    标量取 [0] 就是它自己。）
function 别窗 { @([P40]::Others($pid脚本, $h)) }
function 曲库窗 {
  $别 = @(别窗 | Where-Object { [P40]::Title($_) -eq '歌曲库' })
  if ($别.Count -eq 0) { return $null }
  return $别[0]
}
# UIA 的 FromHandle 偶尔会回一句「无法识别的错误」（E_FAIL）：窗口刚被重建（比如刚装完一首曲子，
# 主窗控件整套换过）、或者 provider 正忙，都会这样。**一次就抛会把整轮验废掉** ——
# 46 号第四轮就是这么断在 `$root = $AE::FromHandle($h)` 的（前面十一条全 OK，后面的全没验）。
# 所以重试几次；要是进程本身已经没了，那是另一回事，直接把退出码报出来（好认是不是崩了）。
function 取根($句柄) {
  $最后 = $null
  for ($i = 0; $i -lt 6; $i++) {
    try { return $AE::FromHandle($句柄) }
    catch {
      $最后 = $_
      if ($script:proc -and $script:proc.HasExited) {
        throw "主窗所在的进程已经退出（退出码 $($script:proc.ExitCode)）—— 这不是 UIA 的锅"
      }
      Start-Sleep -Milliseconds 400
    }
  }
  throw "FromHandle 连试 6 次都没成：$最后"
}
function 曲库根 { $w = 曲库窗; if ($w) { 取根 $w } else { $null } }

# 曲库窗里某一首那一行。**只认已经摆好的行**：刷新之后旧容器会赖在树里，矩形全是 NaN。
function 某行($根, [string]$曲名) {
  foreach ($it in @(找类型 $根 $CT::ListItem)) {
    $r = $it.Current.BoundingRectangle
    if ([double]::IsNaN($r.X) -or $r.Width -le 0 -or $r.Height -le 0) { continue }
    if (@(找类型 $it $CT::Text | Where-Object { $_.Current.Name -eq $曲名 }).Count -gt 0) { return $it }
  }
  return $null
}

# =====================================================================
# 点击（照抄 verify-40：绝对坐标 + 落点前抖一下；判据放在「点出来了吗」而不是「点过了」）
# =====================================================================
function 点元素($e, [IntPtr]$谁的窗, [string]$谁) {
  $r = 矩形 $e
  if ([double]::IsNaN($r.X) -or $r.Width -le 0) { throw "「$谁」在屏幕上没有位置（矩形是 $r）" }
  [void][P40]::Take($谁的窗)
  Start-Sleep -Milliseconds 250
  $cx = [int]($r.X + $r.Width / 2); $cy = [int]($r.Y + $r.Height / 2)
  if ([P40]::PidAt($cx, $cy) -ne $pid脚本) {
    throw "点「$谁」之前 $cx,$cy 上压着的不是本进程（是 PID $([P40]::PidAt($cx, $cy))）"
  }
  [P40]::ClickAt($cx, $cy)
}
function 点后等([scriptblock]$找, [IntPtr]$谁的窗, [string]$谁, [scriptblock]$期望, [int]$秒 = 8, [int]$最多点 = 3, [int]$最少等 = 0) {
  $点了 = 0
  $起 = Get-Date
  $期限 = $起.AddSeconds($秒)
  while ($true) {
    if (& $期望) { return $true }
    $现在 = Get-Date
    if ($现在 -ge $期限 -or $点了 -ge $最多点) { return $false }
    if (($现在 - $起).TotalSeconds -lt $最少等) { Start-Sleep -Milliseconds 400; continue }
    $e = & $找
    if ($null -eq $e) { Start-Sleep -Milliseconds 400; continue }
    点元素 $e $谁的窗 $谁
    $点了++
    Start-Sleep -Milliseconds 500
  }
}
function 双击元素($e, [IntPtr]$谁的窗, [string]$谁) {
  $r = 矩形 $e
  if ([double]::IsNaN($r.X) -or $r.Width -le 0) { throw "「$谁」在屏幕上没有位置（矩形是 $r）" }
  [void][P40]::Take($谁的窗)
  Start-Sleep -Milliseconds 250
  $cx = [int]($r.X + $r.Width / 2); $cy = [int]($r.Y + $r.Height / 2)
  if ([P40]::PidAt($cx, $cy) -ne $pid脚本) { throw "双击「$谁」之前那一点上压着的不是本进程" }
  [P40]::DoubleClickAt($cx, $cy)
}
function 双击后等([scriptblock]$找, [IntPtr]$谁的窗, [string]$谁, [scriptblock]$期望, [int]$秒 = 25, [int]$最多点 = 2, [int]$最少等 = 6) {
  $点了 = 0
  $起 = Get-Date
  $期限 = $起.AddSeconds($秒)
  while ($true) {
    if (& $期望) { return $true }
    $现在 = Get-Date
    if ($现在 -ge $期限 -or $点了 -ge $最多点) { return $false }
    if (($现在 - $起).TotalSeconds -lt $最少等) { Start-Sleep -Milliseconds 400; continue }
    $e = & $找
    if ($null -eq $e) { Start-Sleep -Milliseconds 400; continue }
    双击元素 $e $谁的窗 $谁
    $点了++
    Start-Sleep -Milliseconds 500
  }
}

# =====================================================================
# 起窗口：清掉启动那颗提权框（60 号票新加的，模态，不点掉主窗就是死的）
# =====================================================================
function 关提权框 {
  for ($i = 0; $i -lt 30; $i++) {
    $提示 = @(别窗 | Where-Object { [P40]::Title($_) -eq '要以管理员身份重启吗？' })
    if ($提示.Count -eq 0) { break }
    $根 = 取根 $提示[0]
    $取消 = @(按名字 $根 '取消')
    if ($取消.Count -eq 0) { Start-Sleep -Milliseconds 400; continue }
    if ($i -eq 0) { Write-Host '  启动那颗提权框在 —— 按「取消」（= 先不提权，照常往里走）' }
    点元素 $取消[0] $提示[0] '取消'
    Start-Sleep -Milliseconds 500
  }
  $script:proc.Refresh()
  $script:h = $script:proc.MainWindowHandle
  if ([P40]::Title($script:h) -eq '要以管理员身份重启吗？') { throw '提权框没点掉' }
  if (-not [P40]::Enabled($script:h)) { throw '主窗口还是禁用的 —— 提权框没真收掉' }
}
function 起窗口带清障 {
  $exe = Join-Path $PSScriptRoot '..\..\MidiPerformer.App\bin\Debug\net8.0\MidiPerformer.exe'
  if (-not (Test-Path $exe)) { throw "没找到 $exe —— 先编译" }
  $在跑的 = @(Get-Process -Name MidiPerformer -EA SilentlyContinue)
  if ($在跑的.Count) { throw "已经有 MidiPerformer 在跑（PID $(($在跑的 | ForEach-Object { $_.Id }) -join ', ')）—— 先关掉再跑" }
  $proc = Start-Process -FilePath $exe -PassThru
  $期限 = (Get-Date).AddSeconds(30)
  do {
    Start-Sleep -Milliseconds 500
    if ($proc.HasExited) { throw "窗口没起来，退出码 $($proc.ExitCode)" }
    $proc.Refresh()
  } while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $期限)
  if ($proc.MainWindowHandle -eq 0) { throw '等不到窗口句柄' }
  Start-Sleep -Seconds 3
  $script:proc = $proc
  $script:pid脚本 = [uint32]$proc.Id
  $script:h = $proc.MainWindowHandle
  关提权框
  if (-not [P40]::Take($script:h)) { throw '拽不到前台' }
  $wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
  [void][P40]::SetWindowPos($script:h, [IntPtr]::Zero, $wa.X, $wa.Y, $wa.Width, $wa.Height, 0x0004 -bor 0x0010)
  Start-Sleep -Milliseconds 800
  Write-Host "  起了一个干净实例：PID $($proc.Id)，窗口 $([P40]::Rect($script:h))"
  return $script:h
}

# =====================================================================
# 像素取数的小工具
# =====================================================================
# 一行里 [x1..x2] 那一段的平均亮度（0..255）。隔 2 取一点就够，噪声会被平均掉。
function 行亮([byte[]]$b, [int]$bw, [int]$x1, [int]$x2, [int]$y) {
  $s = 0.0; $n = 0
  for ($x = $x1; $x -le $x2; $x += 2) {
    $c = [P40]::Rgb($b, $bw, $x, $y)
    $s += (($c -shr 16) -band 255) + (($c -shr 8) -band 255) + ($c -band 255)
    $n += 3
  }
  if ($n -eq 0) { return 0.0 }
  $s / $n
}
# 竖着那一列 [x1..x2]、[y1..y2] 那一块的平均亮度
function 块亮([byte[]]$b, [int]$bw, [int]$x1, [int]$x2, [int]$y1, [int]$y2) {
  $s = 0.0; $n = 0
  for ($y = $y1; $y -le $y2; $y += 2) {
    for ($x = $x1; $x -le $x2; $x += 2) {
      $c = [P40]::Rgb($b, $bw, $x, $y)
      $s += (($c -shr 16) -band 255) + (($c -shr 8) -band 255) + ($c -band 255)
      $n += 3
    }
  }
  if ($n -eq 0) { return 0.0 }
  $s / $n
}
# 相邻两像素的平均亮度差 —— 颗粒越大这个数越大（它是「高频有多少」的粗糙度量）
function 颗粒([byte[]]$b, [int]$bw, [int]$x1, [int]$x2, [int]$y1, [int]$y2) {
  $s = 0.0; $n = 0
  for ($y = $y1; $y -le $y2; $y++) {
    for ($x = $x1; $x -lt $x2; $x++) {
      $a = [P40]::Rgb($b, $bw, $x, $y); $c = [P40]::Rgb($b, $bw, $x + 1, $y)
      $s += [Math]::Abs((($a -shr 16) -band 255) - (($c -shr 16) -band 255))
      $n++
    }
  }
  if ($n -eq 0) { return 0.0 }
  $s / $n
}
# 把一块裁出来（存成对照图用）。`,$出` 那一下是为了别让 PowerShell 把 byte[] 拆进管道。
function 裁([byte[]]$b, [int]$bw, [int]$x, [int]$y, [int]$w, [int]$ht) {
  $出 = New-Object byte[] ($w * $ht * 4)
  for ($j = 0; $j -lt $ht; $j++) {
    for ($i = 0; $i -lt $w; $i++) {
      $s = (($y + $j) * $bw + ($x + $i)) * 4
      $d = ($j * $w + $i) * 4
      $出[$d] = $b[$s]; $出[$d + 1] = $b[$s + 1]; $出[$d + 2] = $b[$s + 2]; $出[$d + 3] = 255
    }
  }
  , $出
}
# 一块区域按 $格 像素一格取平均，返回一维数组 —— 用来看「低频结构还在不在」。
function 粗格([byte[]]$b, [int]$bw, [int]$x1, [int]$y1, [int]$w, [int]$ht, [int]$格) {
  $列 = [int]($w / $格); $行 = [int]($ht / $格)
  $出 = New-Object 'double[]' ($列 * $行)
  for ($j = 0; $j -lt $行; $j++) {
    for ($i = 0; $i -lt $列; $i++) {
      $出[$j * $列 + $i] = 块亮 $b $bw ($x1 + $i * $格) ($x1 + $i * $格 + $格 - 1) ($y1 + $j * $格) ($y1 + $j * $格 + $格 - 1)
    }
  }
  @{ 值 = $出; 列 = $列 }
}

# =====================================================================
# 开跑
# =====================================================================
"===== 46 号：曲库面板毛玻璃 F5（$标签 主题）====="

$曲库 = (Resolve-Path (Join-Path $PSScriptRoot '..\..\MidiPerformer.App\bin\Debug\net8.0\songs')).Path
function 目录指纹([string]$目录) {
  @(Get-ChildItem -File $目录 | Sort-Object Name | ForEach-Object {
      "{0} {1} {2}" -f $_.Name, $_.Length, (Get-FileHash $_.FullName -Algorithm MD5).Hash })
}
$曲库开头 = 目录指纹 $曲库
Write-Host "  曲库 $曲库（$($曲库开头.Count) 份，跑完逐文件比对）"

try {
  $h = 起窗口带清障
  $root = 取根 $h
  $win = $root.Current.BoundingRectangle
  Write-Host "  主窗 $([int]$win.Width)x$([int]$win.Height) @ $([int]$win.X),$([int]$win.Y)"

  # ---------- 0. 先装一首：空卷帘是一块均匀的底，糊不糊看不出来 ----------
  "`n=== 0. 装一首 Carulli —— 卷帘上得有东西，糊不糊才看得出来 ==="
  点后等 { (按种类 '歌曲库')[0] } $h '歌曲库' { $null -ne (曲库窗) } | Out-Null
  $dw = 曲库窗
  if (-not $dw) { throw '曲库窗没开出来' }
  $装上了 = 双击后等 {
    $w = 曲库窗
    if (-not $w) { return $null }          # 窗口没了就是「这一行没得点」
    某行 (曲库根) 'Carulli_Duetto_No2_Op4'
  } $dw 'Carulli 那一行' { @(别窗).Count -eq 0 } 30 2 6
  断言真 '双击 Carulli 把它装上了（曲库窗自己关掉）' $装上了 "别窗 $(@(别窗).Count) 个"
  if (-not $装上了) {
    点后等 { 按id (曲库根) 'CloseButton' } (曲库窗) '关闭' { @(别窗).Count -eq 0 } | Out-Null
  }
  Start-Sleep -Seconds 2
  # 装曲子会把主窗控件整套重建 —— 开场抓的那个根从这一刻起作废
  $root = 取根 $h
  $win = $root.Current.BoundingRectangle

  # ---------- 1. 再开曲库窗：这就是要截的那一扇 ----------
  "`n=== 1. 再开曲库窗 ==="
  点后等 { (按种类 '歌曲库')[0] } $h '歌曲库' { $null -ne (曲库窗) } | Out-Null
  $dw = 曲库窗
  if (-not $dw) { throw '曲库窗没开出来' }
  $droot = 取根 $dw
  [void][P40]::Take($dw)
  Start-Sleep -Milliseconds 800
  $dr = New-Object P40+RECT
  [void][P40]::GetWindowRect($dw, [ref]$dr)
  $d宽px = $dr.R - $dr.L
  Write-Host "  曲库窗 $([P40]::Rect($dw))"

  # ---------- 2. 面板占的那块屏幕矩形 ----------
  # 面板本体的左右下三边 = 那条列表的框（列表铺满面板的其余部分，页脚在它下面）：
  # 都从 UIA 拿，不猜。上边从「N 首」那一格往上推 9 DIP（标题那条 Border 的 Padding 上边）。
  $列表 = @(找类型 $droot $CT::List)
  $计数格 = 按id $droot 'CountText'
  if ($列表.Count -eq 0 -or $null -eq $计数格) { throw '面板里找不到列表或「N 首」—— 曲库窗还没摆好' }
  # 这台机器上窗口 560 DIP 宽 → 客户区 1120 物理像素。缩放比从窗口矩形反推，不写死 2。
  $缩放 = [Math]::Max(1, [int][Math]::Round($d宽px / 560.0))
  $lr = 矩形 $列表
  $面板左 = [int]$lr.X - $dr.L
  $面板右 = [int]($lr.X + $lr.Width) - $dr.L - 1
  $面板下 = [int]($lr.Y + $lr.Height) - $dr.T - 1
  $面板上 = [int]($计数格.Current.BoundingRectangle.Y) - $dr.T - 9 * $缩放
  Write-Host "  缩放 ${缩放}x；面板（客户区）$([int]($面板右 - $面板左 + 1))x$([int]($面板下 - $面板上 + 1)) px @ $面板左,$面板上（窗口内坐标）"
  断言真 '面板的框量得出来（左右下三边来自列表，上边从「N 首」推）' `
    ($面板右 - $面板左 -gt 400 -and $面板下 - $面板上 -gt 200) "宽 $($面板右 - $面板左 + 1)，高 $($面板下 - $面板上 + 1)"

  $b = [P40]::Shot($dw)
  $bw = [P40]::LastW
  $bh = [P40]::LastH
  存图 $b $bw $bh (Join-Path $图 '曲库窗.png')
  断言真 '曲库窗的图抓下来了' ($bw -gt 0 -and $bh -gt 0) "$bw x $bh px"

  $b主 = [P40]::Shot($h)
  $b主w = [P40]::LastW
  $b主h = [P40]::LastH
  存图 $b主 $b主w $b主h (Join-Path $图 '主窗.png')

  # 同一块**屏幕**矩形，两张图里各裁一份：一张是糊之前、一张是糊之后
  $裁宽 = $面板右 - $面板左 + 1
  $裁高 = $面板下 - $面板上 + 1
  $主左 = $面板左 + $dr.L - [int]$win.X
  $主上 = $面板上 + $dr.T - [int]$win.Y
  if ($主左 -ge 0 -and $主上 -ge 0 -and ($主左 + $裁宽) -le $b主w -and ($主上 + $裁高) -le $b主h) {
    存图 (裁 $b主 $b主w $主左 $主上 $裁宽 $裁高) $裁宽 $裁高 (Join-Path $图 '对照-糊前.png')
    存图 (裁 $b $bw $面板左 $面板上 $裁宽 $裁高) $裁宽 $裁高 (Join-Path $图 '对照-糊后.png')
    Write-Host "  对照图裁好了：主窗里那块在 $主左,$主上，曲库窗里那块在 $面板左,$面板上（同屏幕位置）"
  } else {
    断言真 '面板那块在主窗的图里也落得下（对照图裁得出）' $false "主窗图 $b主w x $b主h，要裁 $主左,$主上 $裁宽 x $裁高"
  }

  # 整屏一张：看那一坨摆在一起是什么样
  $外 = 260
  $屏 = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $x1 = [Math]::Max(0, $dr.L - $外); $y1 = [Math]::Max(0, $dr.T - $外)
  $x2 = [Math]::Min($屏.Width, $dr.R + $外); $y2 = [Math]::Min($屏.Height, $dr.B + $外)
  $bmp = New-Object System.Drawing.Bitmap(($x2 - $x1), ($y2 - $y1))
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($x1, $y1, 0, 0, (New-Object System.Drawing.Size(($x2 - $x1), ($y2 - $y1))))
  $g.Dispose()
  $bmp.Save((Join-Path $图 '整屏.png'), [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()

  # ---------- 3. 顶边那一道 1px 高光 ----------
  # 不猜「哪一行是顶边」（推出来的上沿有 ±2px 的误差）：在上沿附近扫一圈，找最亮的那一行，
  # 再看它比上下各 3px 亮多少。高光只有 1px 宽，所以峰值必须又尖又独立。
  "`n=== 2. 顶边那一道 1px 高光 ==="
  $带左 = $面板左 + 120; $带右 = $面板右 - 120
  $扫 = @()
  for ($dy = -12; $dy -le 14; $dy++) {
    $y = $面板上 + $dy
    if ($y -lt 0 -or $y -ge $bh) { continue }
    $扫 += [pscustomobject]@{ 偏移 = $dy; 亮 = (行亮 $b $bw $带左 $带右 $y) }
  }
  $峰 = $扫 | Sort-Object -Property 亮 -Descending | Select-Object -First 1
  if ($null -eq $峰) { throw '面板上沿那一带扫不到行 —— 面板的框量歪了' }
  foreach ($点 in $扫) {
    Write-Host ("    {0,4} px  {1,6:N1}  {2}" -f $点.偏移, $点.亮, ('#' * [int][Math]::Max(0, ($点.亮 - 20) / 3)))
  }
  # ⚠️ 别写成 `(... | Select-Object -First 1).亮`：空的时候那个 `.亮` 落在 $null 上，
  #    StrictMode 下报的是一句「找不到属性」，看不出是「这一行不在扫描范围里」。
  $上3 = 0.0; $下3 = 0.0
  $命中 = @($扫 | Where-Object { $_.偏移 -eq $峰.偏移 - 3 })
  if ($命中.Count) { $上3 = $命中[0].亮 }
  $命中 = @($扫 | Where-Object { $_.偏移 -eq $峰.偏移 + 3 })
  if ($命中.Count) { $下3 = $命中[0].亮 }
  $峰值 = $峰.亮
  Write-Host ("  最亮的一行在偏移 {0}（亮度 {1:N1}；往上 3px {2:N1}，往下 3px {3:N1}）" -f $峰.偏移, $峰值, $上3, $下3)
  if ($深色) {
    断言真 '顶边有一道高光（比上边 3px 都亮）' `
      ($峰.偏移 -ge -8 -and $峰.偏移 -le 10 -and ($峰值 - $上3) -ge 6 -and ($峰值 - $下3) -ge 4) `
      "峰 $峰值，上 $上3，下 $下3"

    # 高光在**代码里是 1 DIP**（原型那句 `inset 0 1px 0` 就是 1 CSS px），落到屏幕上占 $缩放 个物理像素。
    # ⚠️ 别按「1 px」判：这台机器今天的显示缩放是 200%，1 DIP = 2 px —— 按 1px 判会假红
    #    （46 号第一次跑就是这么红的：第 2 px 亮度和峰值只差 0.3，因为它本来就在高光带里）。
    $带内 = 0.0
    for ($i = 0; $i -lt $缩放; $i++) {
      $带内 = [Math]::Max($带内, (行亮 $b $bw $带左 $带右 ($面板上 + $峰.偏移 + $i)))
    }
    $带外 = 行亮 $b $bw $带左 $带右 ($面板上 + $峰.偏移 + $缩放)
    断言真 "那道高光只有 1 DIP（$缩放 个物理像素，过了就掉回背景）" `
      ((($带内 - $带外) -ge 20) -and ([Math]::Abs($带外 - $下3) -lt 8)) `
      "带内 $带内，带外 $带外（再往下 3px 的背景是 $下3）"
  } else {
    Write-Host '  （浅色下这道高光是白线压在近白的底上，差得小是物理性质，不作断言）'
  }

  # ---------- 4. 四边没有泛白圈 ----------
  # 「泛白圈」是**忘了外扩那 90px** 的症状：模糊在面板边上吸不到料，四边糊出来是白的。
  # 判法：四边往里 1..3px 那一圈，不许比再往里 40..60px 那一圈明显亮。
  "`n=== 3. 四边有没有泛白圈（位图忘了外扩就是这个症状）==="
  $顶 = $面板上 + 40; $底 = $面板下 - 40
  $左沿 = 块亮 $b $bw ($面板左 + 1) ($面板左 + 3) $顶 $底
  $左内 = 块亮 $b $bw ($面板左 + 40) ($面板左 + 60) $顶 $底
  $右沿 = 块亮 $b $bw ($面板右 - 3) ($面板右 - 1) $顶 $底
  $右内 = 块亮 $b $bw ($面板右 - 60) ($面板右 - 40) $顶 $底
  $下沿 = 块亮 $b $bw $带左 $带右 ($面板下 - 2) ($面板下 - 1)
  $下内 = 块亮 $b $bw $带左 $带右 ($面板下 - 60) ($面板下 - 40)
  Write-Host ("    左沿 {0:N1} / 左内 {1:N1} ＝ {2:N1}；右沿 {3:N1} / 右内 {4:N1} ＝ {5:N1}；下沿 {6:N1} / 下内 {7:N1} ＝ {8:N1}" -f `
      $左沿, $左内, ($左沿 - $左内), $右沿, $右内, ($右沿 - $右内), $下沿, $下内, ($下沿 - $下内))
  if ($深色) {
    断言真 '左、右、下三边都不比往里 40px 更亮（没有泛白圈）' `
      (($左沿 - $左内) -lt 18 -and ($右沿 - $右内) -lt 18 -and ($下沿 - $下内) -lt 18) `
      "左 $([int]($左沿 - $左内))、右 $([int]($右沿 - $右内))、下 $([int]($下沿 - $下内))（阈值 18）"
  }

  # ---------- 5. 颗粒 ----------
  # 标题那一行中间是空的（「歌曲库」在最左、「N 首」在最右），在那儿量相邻像素差：
  # 有颗粒就不是 0。浅色下颗粒基本看不见 —— 那是物理性质，只报数不判。
  "`n=== 4. 颗粒 ==="
  $gy = $面板上 + 30
  $颗粒值 = 颗粒 $b $bw ($面板左 + 400) ($面板左 + 900) ($gy - 3) ($gy + 3)
  Write-Host ("  空白带上的相邻像素平均差 {0:N2}（0 = 一片平，越大越毛）" -f $颗粒值)
  if ($深色) {
    断言真 '深色主题下颗粒看得见（空白处不是一片平）' ($颗粒值 -ge 1.0) "$([Math]::Round($颗粒值, 2)) ≥ 1.0"
  } else {
    Write-Host '  （浅色下颗粒基本看不见 —— 近白的底上叠中灰噪点，差值本来就小，不作断言）'
  }

  # ---------- 6. 玻璃底下确实是那块卷帘（而且被糊过） ----------
  # 低频结构：把「面板那块」和「主窗里同一块屏幕区域」各按 24px 一格取平均。
  # 玻璃要是真透出了身后的卷帘，两边的格子图案该长得像；底子要是没接上（Backdrop 是 null），
  # 面板那边就只剩一层纯底色，格子几乎是平的。
  "`n=== 5. 玻璃底下那块是不是身后那份卷帘（低频结构像不像）==="
  if ($主左 -ge 0 -and $主上 -ge 0) {
    $甲 = 粗格 $b主 $b主w $主左 $主上 $裁宽 $裁高 24
    $乙 = 粗格 $b $bw $面板左 $面板上 $裁宽 $裁高 24
    $甲均 = ($甲.值 | Measure-Object -Average).Average
    $乙均 = ($乙.值 | Measure-Object -Average).Average
    $甲差 = ($甲.值 | ForEach-Object { [Math]::Abs($_ - $甲均) } | Measure-Object -Average).Average
    $乙差 = ($乙.值 | ForEach-Object { [Math]::Abs($_ - $乙均) } | Measure-Object -Average).Average
    Write-Host ("  主窗那块：均值 {0:N1}，各格偏离均值 {1:N2}" -f $甲均, $甲差)
    Write-Host ("  面板那块：均值 {0:N1}，各格偏离均值 {1:N2}" -f $乙均, $乙差)
    断言真 '面板那块不是一片平（底子确实接上了，玻璃后面有东西）' ($乙差 -ge 1.5) `
      "$([Math]::Round($乙差, 2)) ≥ 1.5（只撒颗粒的话这个数在 0.1 以下）"
    断言真 '主窗那块本身是有结构的（这条对照才有意义）' ($甲差 -ge 1.0) "$([Math]::Round($甲差, 2))"
  }

  # ---------- 7. 收尾前把提权框/曲库窗收干净 ----------
  点后等 { 按id (曲库根) 'CloseButton' } $dw '关闭' { @(别窗).Count -eq 0 } | Out-Null
  断言真 '点「关闭」把曲库窗收了' (@(别窗).Count -eq 0) "$(@(别窗).Count) 个别窗"

  $跑完了 = $true
}
catch {
  Write-Host "`n★ 脚本跑到一半抛了：$_"
  Write-Host "   在 $($_.InvocationInfo.ScriptName):$($_.InvocationInfo.ScriptLineNumber)"
  Write-Host "   那一行：$($_.InvocationInfo.Line.Trim())"
}
finally {
  try { 收窗口 } catch { }
}

# =====================================================================
"`n=== 6. 收尾：曲库目录逐字节没动过 ==="
$曲库现在 = 目录指纹 $曲库
$不一样 = @(Compare-Object $曲库开头 $曲库现在)
断言真 '曲库里的「名字 + 长度 + MD5」一份都没变（装一首歌是只读的）' `
  ($不一样.Count -eq 0) "$($不一样.Count) 处不同"

"`n图存在 $图"
"`n========== 结果（$标签）=========="
if (-not $跑完了) {
  '★ 跑到一半断了：断点之后的每一条都没验过，这不是全过'
  $fail = $fail + 1
} elseif ($fail -eq 0) { '全过' } else { "$fail 条红" }
# 81 号票：裁决行 —— run-all.ps1 拿它跟退出码复核，对不上就把这一条降级成红（别删）。
"==== uitest 裁决 不过=$fail"
exit $fail
