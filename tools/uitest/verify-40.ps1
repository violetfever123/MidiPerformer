# 40 号工单的实机验证：**曲库从左边那条侧栏搬进一个窗口，工具栏那一排统一长相**。
#
# 用法: pwsh -NoProfile -File verify-40.ps1     （脚本自己起 app、自己收尾）
#
# 用户 2026-09-20 的原话：
#   「1. 歌曲库栏目调整：(a) 位置：将"歌曲库"这一栏收纳到上方的"文件操作"菜单中。
#     (b) 交互：点击时会以某种形式展现（具体形式暂未确定）。
#     (c) 样式与功能：展示区域最好稍微宽一点，需显示每首歌的完整名字，
#         并能显示是否修改过，同时支持选择删除曲库的内容。
#     2. 菜单名称统一：将"演奏器"与"文件操作"的表述形式统一。
#        将"演奏器"改名为"演奏"，并放在"操作"旁边。」
# 以及他对我三个追问的选择：
#   · 曲库形态 = **独立窗口**，且「我的希望是它增加一个与文件平齐的按钮」
#     —— 不是塞进「文件」的下拉里，是和「文件」并排
#   · 演奏入口 = **并排的一颗按钮**，和菜单头一个长相
#   · 删除方式 = **每行一颗 ×**（现在这样），有确认框，一次一首，不做多选
#
# **这一票的判据大半是几何而不是像素**：用户要的是「平齐」「并排」「宽一点」，
# 这三句话在屏幕上都有精确的几何含义，UIA 读得到。
#
# ⚠️ **这一票会碰用户真实的曲库目录**（删除是这一票的正题，不真删就证明不了）。
#   所以脚本**开头整目录备份、finally 无条件还原**，收尾还要逐个文件比对 md5。
#   造的两份临时工程（`_verify40_改过` / `_verify40_坏工程`）也在这套备份里，
#   万一中途崩了，还原那一支照样会把它们清掉。
#
# 前提：**非提权**（本脚本不点「开始演奏」，但同样不许提权跑）。

. (Join-Path $PSScriptRoot 'uitest-lib.ps1')

$fail = 0
function 断言真([string]$名字, [bool]$条件, [string]$原文) {
  if ($条件) { "  OK   $名字（$原文）" } else { "  FAIL $名字（$原文）"; $script:fail++ }
}
function 断言([string]$名字, $实际, $期望) {
  if ("$实际" -eq "$期望") { "  OK   $名字 = $实际" }
  else { "  FAIL $名字 = $实际（期望 $期望）"; $script:fail++ }
}
# **逐字**比对。PowerShell 的 `-eq` 对字符串不区分大小写，比文案得用 `-ceq`。
function 断言字([string]$名字, [string]$实际, [string]$期望) {
  if ($实际 -ceq $期望) { "  OK   $名字（逐字相同）" }
  else { "  FAIL $名字 读到「$实际」，期望「$期望」"; $script:fail++ }
}
function 断言含([string]$名字, [string]$实际, [string]$片段) {
  if ($实际 -like "*$片段*") { "  OK   $名字（「$片段」在里面）" }
  else { "  FAIL $名字 读到「$实际」，里面没有「$片段」"; $script:fail++ }
}

# =====================================================================
# 曲库目录：备份 / 还原
# =====================================================================
$曲库 = (Resolve-Path (Join-Path $PSScriptRoot '..\..\MidiPerformer.App\bin\Debug\net8.0\songs')).Path
$备份 = Join-Path $env:TEMP ("midiperformer-songs-backup-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
function 目录指纹([string]$目录) {
  @(Get-ChildItem -File $目录 | Sort-Object Name | ForEach-Object {
      $h = (Get-FileHash $_.FullName -Algorithm MD5).Hash
      "{0} {1} {2}" -f $_.Name, $_.Length, $h })
}
$原样 = 目录指纹 $曲库
Copy-Item $曲库 $备份 -Recurse -Force
"曲库 $曲库"
"  （备份到 $备份）"
"  开头有 $($原样.Count) 份："
$原样 | ForEach-Object { "    $_" }

function 还原曲库 {
  try {
    # 备份要是空的（拷的时候就失败了），**一个字都不许动** —— 宁可留下一份临时工程，
    # 也不能把用户的曲库清空。
    if (-not (Test-Path $备份) -or @(Get-ChildItem -File $备份).Count -eq 0) {
      Write-Host "★ 备份是空的，不还原（免得把曲库清空）；备份目录：$备份"
      return
    }
    Get-ChildItem -File $曲库 | Remove-Item -Force -EA SilentlyContinue
    Get-ChildItem -File $备份 | Copy-Item -Destination $曲库 -Force -EA SilentlyContinue
    Remove-Item $备份 -Recurse -Force -EA SilentlyContinue
  } catch { Write-Host "★ 还原曲库时出错：$_" }
}

# 造一份「改过」的临时工程（cargo 的副本，只把文件头里的 Edited 翻成 true）。
# 用临时文件这个办法而不是「载一首真歌再改」，是因为**这一票不该动用户任何一份工程**。
function 造改过的([string]$目标) {
  $源 = Join-Path $曲库 'cargo.mproj'
  $字节 = [System.IO.File]::ReadAllBytes($源)
  $有BOM = $字节.Length -ge 3 -and $字节[0] -eq 0xEF -and $字节[1] -eq 0xBB -and $字节[2] -eq 0xBF
  $文 = [System.Text.Encoding]::UTF8.GetString($字节, $(if ($有BOM) { 3 } else { 0 }), $字节.Length - $(if ($有BOM) { 3 } else { 0 }))
  if ($文 -notmatch '"Edited"\s*:\s*false') { throw 'cargo.mproj 里找不到 "Edited": false —— 副本没法造' }
  $文 = [regex]::Replace($文, '"Edited"\s*:\s*false', '"Edited": true', 1)
  [System.IO.File]::WriteAllText($目标, $文, (New-Object System.Text.UTF8Encoding($有BOM)))
}

# ---------- UIA 取数的小工具 ----------
# ⚠️ $根 可能是 $null：**窗口该关的那一节里，窗口真的会关掉**（§6 双击一首好的，
#    曲子装上之后窗口自己关 —— 那正是要验的行为）。这时候「现取一遍根」拿到的就是
#    $null，往下 `$根.FindAll(...)` 会抛「不能对值为 Null 的表达式调用方法」，
#    报出来的位置还在这一行，看不出是谁传了个空进来（本票真踩过）。
#    所以这里对空根一律当成「什么都没有」，让调用方自己判「窗口还在不在」。
function 找类型($根, $类型) { if ($null -eq $根) { return @() } @($根.FindAll($TS::Descendants, (& $条件 $类型))) }
function 按id($根, [string]$id) {
  foreach ($t in @($CT::Text, $CT::Button, $CT::Edit, $CT::List, $CT::ListItem, $CT::Pane, $CT::Custom)) {
    $h = @(找类型 $根 $t | Where-Object { $_.Current.AutomationId -eq $id })
    if ($h.Count) { return $h[0] }
  }
  return $null
}
function 按名字($根, [string]$名) {
  foreach ($t in @($CT::MenuItem, $CT::Button, $CT::Text)) {
    $h = @(找类型 $根 $t | Where-Object { $_.Current.Name -eq $名 })
    if ($h.Count) { return $h }
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
function 全部名字 {
  $出 = @()
  foreach ($t in @($CT::MenuItem, $CT::Button, $CT::Text)) {
    foreach ($e in @(找类型 $root $t)) { $出 += $e.Current.Name }
  }
  $出
}
function 矩形($e) { $e.Current.BoundingRectangle }
function 曲库窗 {
  $别 = @([P40]::Others($pid脚本, $h) | Where-Object { [P40]::Title($_) -eq '歌曲库' })
  if ($别.Count -eq 0) { return $null }
  return $别[0]
}
function 别窗 { @([P40]::Others($pid脚本, $h)) }
# 曲库窗的 UIA 根，每次现取 —— 抓在手上的根会过时。
function 曲库根 { $w = 曲库窗; if ($w) { $AE::FromHandle($w) } else { $null } }
# 主窗曲名框里那几个字。**每次现取一遍根**，读不到就报一句看得见的怪话：
#   (1) 装一首曲子会把主窗的控件**整套重建**，开场抓在手上的那个根/元素当场作废；
#   (2) 重建是压在 UI 线程上做的（好几秒），这期间 UIA 读一次就得等 ——
#       所以这里的读法要容得下「读不到」，不能抛。
# ⚠️ 这一格是个 **TextBox**，它的字**不在 Name 里，在 ValuePattern 里**：
#    TextBlock（「N 首」「页脚」那些）Name 就是它画出来的字，TextBox 不是 —— 读 Name
#    永远读到空串。本票真栽过一回：§5 拿 Name 跟 Name 比，两边都是空串，
#    「逐字相同」自然成立，看着绿，其实一次都没验过。
function 主窗曲名 {
  $c = 按id ($AE::FromHandle($h)) 'SongNameBox'
  if ($null -eq $c) { return '★找不到曲名框' }
  try { return $c.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
  catch { return "★读不到曲名框($($_.Exception.GetType().Name))" }
}
# 除了某一窗之外还剩几个顶层窗。IntPtr 用 ToInt64 比 —— 直接 -ne 比 IntPtr 不稳。
function 除它之外([IntPtr]$这窗) { @(别窗 | Where-Object { $_.ToInt64() -ne $这窗.ToInt64() }) }
# 一小片里出现最多的那个颜色 = 那一行的底色。**不能**改成「取最左边那一点」——
# 曲名那一格是拉伸铺满整列的，「名字左边」和「名字右边」都在同一格里，取样点挑不好就取到字上。
function 行底色($b, [int]$w, [int]$x1, [int]$x2, [int]$y) {
  $计数 = @{}
  for ($x = $x1; $x -le $x2; $x++) {
    $c = [P40]::Rgb($b, $w, $x, $y)
    if ($计数.ContainsKey($c)) { $计数[$c]++ } else { $计数[$c] = 1 }
  }
  $最好 = -1; $最多 = -1
  foreach ($k in $计数.Keys) { if ($计数[$k] -gt $最多) { $最多 = $计数[$k]; $最好 = $k } }
  $最好
}

# 点一个元素：先把它那个窗口抬到前台，再在它正中**规规矩矩**点一下。
#
# ⚠️ 这里有两个坑，都是这一票踩出来的，别再回头：
#   1) 从前那套 `SetCursorPos` + 相对 `mouse_event` **时灵时不灵**：同一颗「歌曲库」按钮，
#      第一次点开得出来，关掉之后再点就没反应（probe-40d/e/f 一路查下来）。
#      换成 ClickAt（绝对坐标 + 落点前在旁边抖一下）之后，开关窗连做四轮 4/4 全成（probe-40h）。
#   2) 就算是 ClickAt，合成点击偶尔也会丢。所以判据**不放在「点过了」上，放在「点出来了吗」**：
#      点一下 → 轮询等后果 → 没等到再点一次（最多三次）。见 点后等。
function 点元素($e, [IntPtr]$谁的窗, [string]$谁) {
  $r = 矩形 $e
  if ([double]::IsNaN($r.X) -or $r.Width -le 0) { throw "「$谁」在屏幕上没有位置（矩形是 $r）—— 多半抓到了一个还没摆好的容器" }
  [void][P40]::Take($谁的窗)
  Start-Sleep -Milliseconds 250
  $cx = [int]($r.X + $r.Width / 2); $cy = [int]($r.Y + $r.Height / 2)
  # 按下之前**必须确认那一点上压着的还是本进程** —— 不然点的是别的窗口，测出来的全是假象。
  # （这一条挡不住「同进程的另一扇窗」，所以还得靠后果来判。）
  if ([P40]::PidAt($cx, $cy) -ne $pid脚本) {
    throw "点「$谁」之前 $cx,$cy 上压着的不是本进程（是 PID $([P40]::PidAt($cx, $cy))）"
  }
  [P40]::ClickAt($cx, $cy)
}
# 点，然后**等一件事发生**；等不到就再点一次（最多 $最多点 次）。
#
# $找 是**每次要动手之前现算一遍**的，不是一个抓在手上的元素 —— UIA 的元素会过时：
# 一个抓在手里等了六秒的元素，再去读它的位置会得到一个 Empty 矩形
# （本票真踩过：这一票 §6 就是拿了一个 6 秒前找到的行，报「在屏幕上没有位置」）。
# $期望 同理，每轮开头都复算。
# $最少等：这事本来就要慢慢来的场合（装一首曲子要好几秒），调它把补点往后推。
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
    if ($null -eq $e) { Start-Sleep -Milliseconds 400; continue }   # 这一刻控件不在，下一轮再说
    点元素 $e $谁的窗 $谁
    $点了++
    Start-Sleep -Milliseconds 500
  }
}
# 双击：两下之间不能停太久，不然系统当两次单击。
function 双击元素($e, [IntPtr]$谁的窗, [string]$谁) {
  $r = 矩形 $e
  if ([double]::IsNaN($r.X) -or $r.Width -le 0) { throw "「$谁」在屏幕上没有位置（矩形是 $r）—— 多半抓到了一个还没摆好、或者已经过时的容器" }
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

# 曲库窗里某一首那一行。
#
# ⚠️ **只认已经摆好的行**：列表刷新（删掉一首）之后，旧的行容器会在树里赖一会儿，
# 它们的 BoundingRectangle 全是 NaN —— 不筛掉的话，「找 Carulli 那一行」就可能
# 抓到一个已经不属于任何位置的旧容器，$cx 算出来是 NaN，报的却是
# 「无法将值 "NaN" 转换为类型 "System.Int32"」这种看不出所以然的话（本票真踩过）。
function 某行($根, [string]$曲名) {
  foreach ($it in @(找类型 $根 $CT::ListItem)) {
    $r = $it.Current.BoundingRectangle
    if ([double]::IsNaN($r.X) -or $r.Width -le 0 -or $r.Height -le 0) { continue }
    $t = @(找类型 $it $CT::Text)
    if (@($t | Where-Object { $_.Current.Name -eq $曲名 }).Count -gt 0) { return $it }
  }
  return $null
}
# 一行里那三格：曲名 / 改过没动过 / 那颗叉。
# ⚠️ 别拿「×」当变量名 —— 那是 PowerShell 的乘号，词法就过不去（实测 ParserError）。
function 行里($行, [string]$曲名) {
  $t = @(找类型 $行 $CT::Text)
  $名 = @($t | Where-Object { $_.Current.Name -eq $曲名 })[0]
  $别 = @($t | Where-Object { $_.Current.Name -ne $曲名 })
  $meta = $别[0]
  $叉 = @(找类型 $行 $CT::Button)[0]
  @{ 名 = $名; meta = $meta; 叉 = $叉 }
}

$跑完了 = $false
try {

# =====================================================================
"`n=== 0. 干净实例：工具栏左边那一排四样 ==="
# =====================================================================
$h = 起窗口
$root = $AE::FromHandle($h)
$win = $root.Current.BoundingRectangle
"窗口 $([int]$win.Width)x$([int]$win.Height) @ $([int]$win.X),$([int]$win.Y)"

$四样 = @()
foreach ($名 in @('文件', '歌曲库', '操作', '演奏')) { $四样 += (按种类 $名) }
断言 '四个入口一个不少（文件 / 歌曲库 / 操作 / 演奏）' $四样.Count 4
if ($四样.Count -ne 4) { throw '入口不齐，后面每一节都建立在它们身上' }

$序 = $四样 | Sort-Object { (矩形 $_).X }
$排 = ($序 | ForEach-Object { $_.Current.Name }) -join ' | '
"  从左到右：$排"
断言字 '从左到右正好是「文件 | 歌曲库 | 操作 | 演奏」' $排 '文件 | 歌曲库 | 操作 | 演奏'

# 「平齐 / 并排」在屏幕上的意思：**同高、同中线、间距一样**。三样都量。
$高 = @($序 | ForEach-Object { (矩形 $_).Height })
$中 = @($序 | ForEach-Object { $r = 矩形 $_; $r.Y + $r.Height / 2 })
$左 = @($序 | ForEach-Object { (矩形 $_).X })
$右 = @($序 | ForEach-Object { $r = 矩形 $_; $r.X + $r.Width })
$间 = @(1..3 | ForEach-Object { $左[$_] - $右[$_ - 1] })
"  高 $($高 -join ',')  中线Y $($中 -join ',')  间隙 $($间 -join ',')"
断言真 '四样一样高（差 ≤ 2px）' ((($高 | Measure-Object -Max).Maximum - ($高 | Measure-Object -Min).Minimum) -le 2) `
  "最高 $([int]($高|Measure-Object -Max).Maximum) 最矮 $([int]($高|Measure-Object -Min).Minimum)"
断言真 '四样在同一条中线上（差 ≤ 2px）' ((($中 | Measure-Object -Max).Maximum - ($中 | Measure-Object -Min).Minimum) -le 2) `
  "中线 $($中 -join ',')"
断言真 '三处间隙一样宽（差 ≤ 2px）——「并排」' ((($间 | Measure-Object -Max).Maximum - ($间 | Measure-Object -Min).Minimum) -le 2) `
  "间隙 $($间 -join ',')"
# ⚠️ `-join` 写在**实参位置**上不行：命令模式的解析器会把 `-join` 当成参数名，
#    数组就会被 `"$实际"` 按默认分隔符（空格）拼出来。先算成变量再传。
$类型 = (@($序[1], $序[3]) | ForEach-Object { $_.Current.ControlType.ProgrammaticName }) -join ','
断言 '「歌曲库」和「演奏」是按钮（按一下就开窗，没有下拉）' $类型 'ControlType.Button,ControlType.Button'
断言字 '「歌曲库」那颗按钮就是 LibraryButton' $序[1].Current.AutomationId 'LibraryButton'
断言字 '「演奏」那颗按钮还是叫 PerformerButton（老测试按这个名字找它）' $序[3].Current.AutomationId 'PerformerButton'

# 右上角那颗「演奏器…」蓝按钮撤了没有：窗口里不许再有任何控件说「演奏器」
$带演奏器 = @(全部名字 | Where-Object { $_ -like '*演奏器*' })
断言 '窗口里没有任何控件叫「演奏器」（右上那颗蓝按钮撤了）' $带演奏器.Count 0
$带歌曲库 = @(全部名字 | Where-Object { $_ -eq '歌曲库' })
断言 '窗口里叫「歌曲库」的控件只有一个（就是那颗按钮，侧栏标题没了）' $带歌曲库.Count 1

# 曲库那条列表真的不在主窗里了
$列表数 = (找类型 $root $CT::List).Count + (找类型 $root $CT::ListItem).Count
断言 '主窗里一个列表都没有（曲库那条 ListBox 搬走了）' $列表数 0

# =====================================================================
"`n=== 1. 点「歌曲库」→ 开出一个窗口，里面是完整曲名 + 改过没动过 + 每行一颗 × ==="
# =====================================================================
点后等 { (按种类 '歌曲库')[0] } $h '歌曲库' { $null -ne (曲库窗) } | Out-Null
$dw = 曲库窗
断言真 '点「歌曲库」开出了一个顶层窗，标题叫「歌曲库」' ($null -ne $dw) "别窗：$((别窗 | ForEach-Object { [P40]::Title($_) }) -join ',')"
if (-not $dw) { throw '曲库窗口没开出来' }
$droot = $AE::FromHandle($dw)
$dr = New-Object P40+RECT; [void][P40]::GetWindowRect($dw, [ref]$dr)
$d宽 = $dr.R - $dr.L; $d高 = $dr.B - $dr.T
"  曲库窗口 $($d宽)x$($d高) @ $($dr.L),$($dr.T)（主窗 $([int]$win.Width)x$([int]$win.Height)）"
断言真 '曲库窗口比主窗窄得多（它是一个独立的小窗，不是又一块面板）' ($d宽 -lt $win.Width / 2) "$d宽 < $([int]($win.Width / 2))"
断言真 '曲库窗口「稍微宽一点」—— 物理宽 ≥ 1000px（= 500 DIP @2x）' ($d宽 -ge 1000) "$d宽"

$行们 = @(找类型 $droot $CT::ListItem)
$应是 = @(Get-ChildItem -File $曲库 -Filter *.mproj | Sort-Object Name)
断言 '列表里的行数 = 曲库目录里的 .mproj 份数' $行们.Count $应是.Count
断言字 '「N 首」和行数对得上' (按id $droot 'CountText').Current.Name "$($应是.Count) 首"

$底 = (按id $droot 'StatusText').Current.Name
断言字 '页脚那一行一开始是空的（还没删过东西）' $底 ''
断言真 '页脚上有一颗「关闭」' ($null -ne (按id $droot 'CloseButton')) ''

# ---- 逐行：曲名 / 改过没动过 / ×，以及**完整名字的像素证人** ----
$b = [P40]::Shot($dw)
$bw = [P40]::LastW
"  曲库窗的图 $bw x $([P40]::LastH)"
foreach ($名 in ($应是 | ForEach-Object { $_.BaseName })) {
  $行 = 某行 $droot $名
  if (-not $行) { "  FAIL 列表里没有「$名」这一行"; $script:fail++; continue }
  $格 = 行里 $行 $名
  $rn = 矩形 $格.名; $rm = 矩形 $格.meta; $r叉 = 矩形 $格.叉
  # UIA 读到的 Name 是**完整文本**（省略号只影响绘制，不影响这个），所以「完整名字」这句
  # 得靠像素判：名字那一格右边要留着一大片空白 ⇒ 没被截断。
  # 判据是**墨水右沿离格子右沿还有多远**，不依赖字体度量、也不依赖缩放。
  $x1 = [int]($rn.X - $dr.L); $x2 = [int]($rn.X - $dr.L + $rn.Width) - 1
  $y1 = [int]($rn.Y - $dr.T); $y2 = [int]($rn.Y - $dr.T + $rn.Height) - 1
  $底色 = 行底色 $b $bw $x1 $x2 ([int](($y1 + $y2) / 2))
  $墨右 = [P40]::InkRight($b, $bw, $x1, $x2, $y1, $y2, $底色, 16)
  $墨宽 = $墨右 - $x1
  "  「$名」: 名字格 $([int]$rn.Width)px / 小字「$($格.meta.Current.Name)」 / × $([int]$r叉.Width)px"
  "        底色 #$('{0:X6}' -f $底色)，名字画了 $墨宽 px，格子右边还剩 $([int]$rn.Width - $墨宽) px 空白"
  断言真 "「$名」这一行的曲名没被省略号截断（墨迹右边还留着空白）" `
    ($墨右 -ge 0 -and ($x2 - $墨右) -gt 40) "墨水右沿 $墨右，格子右沿 $x2"
  断言真 "「$名」这一行有「改过 / 没动过 / 读不出来」那格小字" `
    (@('改过', '没动过', '读不出来') -contains $格.meta.Current.Name) "读到「$($格.meta.Current.Name)」"
  断言真 "「$名」这一行有一颗 ×" ($null -ne $格.叉 -and $格.叉.Current.Name -eq '×') "读到「$($格.叉.Current.Name)」"
}

# 关窗收工：点页脚那颗「关闭」（它挂着 IsCancel，Esc 也走得通，但点按钮更实在）
点后等 { 按id (曲库根) 'CloseButton' } $dw '关闭' { (别窗).Count -eq 0 } | Out-Null
断言 '点「关闭」就把曲库窗口关掉了' (别窗).Count 0

# =====================================================================
"`n=== 2. 「改过」那格是真从工程文件里读出来的（造一份改过的临时工程）==="
# =====================================================================
$改过的 = Join-Path $曲库 '_verify40_改过.mproj'
$坏的 = Join-Path $曲库 '_verify40_坏工程.mproj'
造改过的 $改过的
[System.IO.File]::WriteAllText($坏的, '这不是一个工程', (New-Object System.Text.UTF8Encoding($false)))
"  造了两份临时工程：$(Split-Path -Leaf $改过的) / $(Split-Path -Leaf $坏的)"

点后等 { (按种类 '歌曲库')[0] } $h '歌曲库' { $null -ne (曲库窗) } | Out-Null
$dw = 曲库窗
if (-not $dw) { throw '第二次开曲库窗没开出来' }
$droot = $AE::FromHandle($dw)
断言 '现在列表里是 4 行（原来 2 + 造的两份）' (找类型 $droot $CT::ListItem).Count 4

$行改 = 某行 $droot '_verify40_改过'
$行原 = 某行 $droot 'cargo'
断言字 '临时那份（文件头里 Edited=true）那格小字是「改过」' (行里 $行改 '_verify40_改过').meta.Current.Name '改过'
断言字 'cargo（文件头里 Edited=false）那格小字是「没动过」' (行里 $行原 'cargo').meta.Current.Name '没动过'
$行坏 = 某行 $droot '_verify40_坏工程'
断言字 '乱写的那份那格小字是「读不出来」' (行里 $行坏 '_verify40_坏工程').meta.Current.Name '读不出来'

# =====================================================================
"`n=== 3. 每行那颗 ×：先「取消」（什么都不该发生）==="
# =====================================================================
$格 = 行里 $行改 '_verify40_改过'
点后等 { (行里 (某行 (曲库根) '_verify40_改过') '_verify40_改过').叉 } $dw '那一行的 ×' { (除它之外 $dw).Count -ge 1 } | Out-Null
$确认 = 除它之外 $dw
断言真 '点 × 弹出了一问（多出一个顶层窗）' ($确认.Count -eq 1) "别窗 $($确认.Count) 个"
if ($确认.Count -ne 1) { throw '确认框没弹出来' }
$croot = $AE::FromHandle($确认[0])
断言字 '那一问的标题是「删除曲子」' ([P40]::Title($确认[0])) '删除曲子'
$文 = (找类型 $croot $CT::Text | ForEach-Object { $_.Current.Name }) -join ' / '
"  那一问上写着：「$文」"
断言含 '那一问说清了「文件会一起删掉，撤不回来」' $文 '撤不回来'

点后等 { (按名字 ($AE::FromHandle($确认[0])) '取消')[0] } $确认[0] '取消' { (除它之外 $dw).Count -eq 0 } | Out-Null
断言 '点「取消」之后那一问没了' (除它之外 $dw).Count 0
断言真 '点「取消」之后文件还在' (Test-Path $改过的) $改过的
断言 '点「取消」之后那一行还在' (@(找类型 $AE::FromHandle($dw) $CT::ListItem)).Count 4

# =====================================================================
"`n=== 4. 再点一次 ×，这回按「删除」：文件真的没了、行也没了 ==="
# =====================================================================
$droot = $AE::FromHandle($dw)
$格 = 行里 (某行 $droot '_verify40_改过') '_verify40_改过'
点后等 { (行里 (某行 (曲库根) '_verify40_改过') '_verify40_改过').叉 } $dw '那一行的 ×' { (除它之外 $dw).Count -ge 1 } | Out-Null
$确认 = 除它之外 $dw
if ($确认.Count -ne 1) { throw '第二次点 × 没弹出确认框' }
$croot = $AE::FromHandle($确认[0])
点后等 { (按名字 ($AE::FromHandle($确认[0])) '删除')[0] } $确认[0] '删除' { (除它之外 $dw).Count -eq 0 } | Out-Null
断言 '按「删除」之后确认框收了' (除它之外 $dw).Count 0
断言真 '文件真的从盘上删掉了' (-not (Test-Path $改过的)) "Test-Path $改过的 = $(Test-Path $改过的)"
$droot = $AE::FromHandle($dw)
断言 '那一行也没了（4 → 3）' (找类型 $droot $CT::ListItem).Count 3
断言真 '那一行确实找不到了' ($null -eq (某行 $droot '_verify40_改过')) ''
断言字 '「N 首」跟着减到 3' (按id $droot 'CountText').Current.Name '3 首'
$底 = (按id $droot 'StatusText').Current.Name
断言含 '页脚上说了那一句' $底 '已从曲库删掉'
断言 '曲库窗口**留着**（删完还能接着删下一首）' (除它之外 $dw).Count 0

# =====================================================================
"`n=== 5. 双击一首好的：窗口自己关掉，曲子装上 ==="
# =====================================================================
$droot = $AE::FromHandle($dw)
"  列表里现在这些行（NaN 的是刷新之后赖着没走的旧容器）："
foreach ($it in @(找类型 $droot $CT::ListItem)) {
  $r = 矩形 $it
  $名 = (@(找类型 $it $CT::Text) | Select-Object -First 1).Current.Name
  "    「$名」 @ $([int]$r.X),$([int]$r.Y) $([int]$r.Width)x$([int]$r.Height)"
}
# 这一节兼着后头几节的**前置**：Carulli 装上之后 §7 才量得到轨道头。装一首曲子要好几秒
# （重建全部控件、重算场景），装载期间 UI 线程是满的，UIA 读一次得等它跑完 ——
# 所以期望写成「曲名框里读到了那一首」，补点推到 6 秒之后（$最少等），
# 免得点击还没生效就判红。
# ⚠️ 「窗口找不到了」在这一节**不是异常，是答案**：这一段要验的正是「装上了窗口就自己关」。
#    找行之前得先判窗口还在不在 —— 一看到「没窗」就跑去 某行($null)，报出来的是
#    「不能对值为 Null 的表达式调用方法」，位置还落在 找类型 那一行，看着跟本节毫无关系
#    （本票真栽在这儿：probe-40j 拿 IsWindow 逐秒采样，证明窗口 12 秒里一直好端端的，
#    是验证脚本自己把「窗口按设计关了」当成了崩溃）。
$script:等装上的名 = '__还没读过__'
双击后等 {
  $w = 曲库窗
  if (-not $w) { return $null }   # 窗口没了就等于「这一行没得点」，让 双击后等 回去复算期望
  某行 (曲库根) 'Carulli_Duetto_No2_Op4'
} $dw 'Carulli 那一行' {
  $名 = 主窗曲名
  # 只在「读到的名字变了」的时候报一行 —— 这个块每 400ms 就要跑一遍，
  # 不加这个门的话日志里会刷出几十行一样的话。
  # 用 $script: 前缀是因为 $期望 是 & 在子作用域里跑的，不带前缀的赋值出了块就没了。
  if ($名 -ne $script:等装上的名) {
    $script:等装上的名 = $名
    Write-Host "    [等装上] 曲名框里现在读到「$名」"
  }
  (别窗).Count -eq 0 -and $名 -eq 'Carulli_Duetto_No2_Op4'
} 30 2 6 | Out-Null
断言 '双击打开之后曲库窗口自己关了' (别窗).Count 0
断言字 '主窗的曲名框换成了那一首' (主窗曲名) 'Carulli_Duetto_No2_Op4'

# ⚠️ 上一节把 Carulli 装上了，而**装曲子会把主窗的控件整套重建** —— 开场抓在手上的那个
#    $root 从这一刻起就作废了。下面 §6 要再点一次「歌曲库」、§7 要找轨号文字、§8 要点「演奏」，
#    都还在用主窗：拿着一个作废的根去找，轻则一个都找不到、重则抛 ElementNotAvailable，
#    报出来都不像本因。所以这儿重新抓一遍。（§1..§4 期间没装过曲子，$root 一直是好的。）
$root = $AE::FromHandle($h)

# =====================================================================
"`n=== 6. 再开一次曲库，双击一份读不出来的：窗口留着、页脚说中文、手上那首不动 ==="
# =====================================================================
# 顺序是**故意**先好后坏的：坏的那一节要验的「手上那份没被弄坏」，得先真有一份在手上
# 才算数。反过来的话曲名框两头都是空的，两边一比「逐字相同」自然成立 —— 看着绿，
# 其实一次都没验（本票真这么空判过一轮，见 主窗曲名 那条注释）。
点后等 { (按种类 '歌曲库')[0] } $h '歌曲库' { $null -ne (曲库窗) } | Out-Null
$dw = 曲库窗
if (-not $dw) { throw '第二次开曲库窗没开出来' }
$droot = $AE::FromHandle($dw)
$名前 = 主窗曲名
# ⚠️ 判据必须是「页脚**换了话**」，不能是「页脚上有话」——
#    没删过东西时页脚是空的，但写成「非空」这种判据迟早会在别处空判（页脚上本来就有字）。
$底前 = (按id $droot 'StatusText').Current.Name
"  双击之前：曲名框「$名前」，页脚「$底前」"
双击后等 { 某行 (曲库根) '_verify40_坏工程' } $dw '坏工程那一行' {
  $w = 曲库窗
  $w -and (按id ($AE::FromHandle($w)) 'StatusText').Current.Name -ne $底前
} 20 2 0 | Out-Null
断言真 '读不出来的那一份没把窗口关掉（窗口一闪是看不见话的）' ($null -ne (曲库窗)) ''
$droot = $AE::FromHandle($dw)
$底 = (按id $droot 'StatusText').Current.Name
"  页脚说：「$底」"
断言真 '页脚上换了一句新话（双击确实被接住了）' ($底 -ne $底前) "读到「$底」"
断言真 '页脚上报了一句中文，不是空的' ($底.Length -gt 0) "读到「$底」"
断言字 '手上正开着的那首没被弄坏（曲名框还写着 Carulli）' (主窗曲名) 'Carulli_Duetto_No2_Op4'

# 后头 §7 要量主窗的像素、§8 要按主窗上的「演奏」—— 模态框压着头顶时主窗是禁用的，
# 按什么都没反应。所以量之前先把它收了（顺带把「关闭」这颗按钮再走一遍）。
点后等 { 按id (曲库根) 'CloseButton' } $dw '关闭' { (别窗).Count -eq 0 } | Out-Null
断言 '点「关闭」把曲库窗收了' (别窗).Count 0

# =====================================================================
"`n=== 7. 侧栏撤了、卷帘真的变宽了（载了曲子才量得到轨道头）==="
# =====================================================================
# 侧栏是 212 DIP 宽 = 这台机器上 **424 物理像素**（3072 物理 / 1536 DIP，缩放 2）。
# 两个独立的证人：
#   (1) 轨号文字「01」的位置：从前量到 X 落在 440..490，现在应该往左挪整整一个侧栏；
#   (2) 卷帘底色 `#1E2A3A` 的左沿：现在应该紧跟在窗口最左边那条 6px 焦点条后面。
# ⚠️ 下面 `$旧位` 里的 **440..490 是有意写的旧位**，别当成「写死的旧版式坐标」来修（76 号）：
# 这里量的是**「轨号不再落在那一段」这件事本身** —— 这就是本票第 7 节的回归判据。
# 当活定位器用的那几处（find-note.ps1 / verify-18.ps1 / verify-19.ps1）已经改成按「折叠」按钮锚定行了，
# 而**这一处必须留旧数**；改掉它 = 把这条回归检查删掉。
$旧位 = @(找类型 $root $CT::Text | Where-Object {
    $_.Current.Name -match '^\d{2}$' -and (矩形 $_).X -gt 440 -and (矩形 $_).X -lt 490 })
$新位 = @(找类型 $root $CT::Text | Where-Object {
    $_.Current.Name -match '^\d{2}$' -and (矩形 $_).X -lt 200 }) |
  Sort-Object { (矩形 $_).Y }
断言 '轨号文字不再落在旧的 440..490 那一段（侧栏原来占着那儿）' $旧位.Count 0
断言 '轨道头一共 4 条（Carulli 4 轨）' $新位.Count 4
if ($新位.Count -eq 0) { throw '找不到轨道头，量不了卷帘' }
$x新 = [int](矩形 $新位[0]).X
"  轨号「$($新位[0].Current.Name)」现在在 x=$x新（旧的量法是 440..490）"
断言真 '轨号往左挪了**整整一个侧栏**（424 物理像素 ± 40）' ([Math]::Abs((453 - $x新) - 424) -le 40) `
  "453 − $x新 = $(453 - $x新)，期望 424 ± 40"

$b = [P40]::Shot($h)
$bw = [P40]::LastW
$y = [int]((矩形 $新位[0]).Y - $win.Y) + 200
$左卷 = -1; $左焦点 = -1
for ($x = 0; $x -lt 700; $x++) {
  $c = [P40]::Rgb($b, $bw, $x, $y)
  if ($左焦点 -lt 0 -and $c -eq 0x74ABDD) { $左焦点 = $x }
  if ($左卷 -lt 0 -and $c -eq 0x1E2A3A) { $左卷 = $x }
}
"  y=$y 那一行：焦点条 #74ABDD 从 x=$左焦点 起，卷帘底色 #1E2A3A 从 x=$左卷 起"
断言真 '卷帘底色出现在旧的侧栏右边线（424）**左边**' ($左卷 -ge 0 -and $左卷 -lt 424) `
  "左沿 x=$左卷，旧的侧栏右沿是 424"
断言真 '焦点条紧挨着卷帘左沿（它是轨头左沿那条 6px 的线）' ($左焦点 -ge 0 -and ($左卷 - $左焦点) -le 12) `
  "焦点条 x=$左焦点、卷帘 x=$左卷"
"  卷帘宽度：$([int]$win.Width - $左卷) px（从前是 $([int]$win.Width - 424 - 6) px 左右）"

# =====================================================================
"`n=== 8. 「演奏」那颗按钮还开着演奏器窗口 ==="
# =====================================================================
点后等 { (按种类 '演奏')[0] } $h '演奏' {
  @(别窗 | Where-Object { [P40]::Title($_) -eq '演奏器' }).Count -ge 1
} | Out-Null
$演奏器 = @(别窗 | Where-Object { [P40]::Title($_) -eq '演奏器' })
断言真 '点「演奏」开出了标题叫「演奏器」的窗口' ($演奏器.Count -eq 1) `
  "别窗：$((别窗 | ForEach-Object { [P40]::Title($_) }) -join ',')"

  $跑完了 = $true
}
catch {
  # 报出**哪一行**抛的。只报一句 "$_" 的话，像「不能对值为 Null 的表达式调用方法」
  # 这种到处都是的错就只能靠猜（本票为这一条多跑了一整轮）。
  Write-Host "`n★ 脚本跑到一半抛了：$_"
  Write-Host "   在 $($_.InvocationInfo.ScriptName):$($_.InvocationInfo.ScriptLineNumber)"
  Write-Host "   那一行：$($_.InvocationInfo.Line.Trim())"
  Write-Host "   调用栈：$($_.ScriptStackTrace)"
}
finally {
  try { 收窗口 } catch { }
  try {
    # 造的两份临时工程：删干净（改过那份可能已经被 UI 删掉了，删不到不算错）
    foreach ($f in @('_verify40_改过.mproj', '_verify40_坏工程.mproj')) {
      $p = Join-Path $曲库 $f
      if (Test-Path $p) { Remove-Item $p -Force }
    }
    还原曲库
  } catch { Write-Host "★ 收尾时出错：$_" }
}

# =====================================================================
"`n=== 9. 收尾：曲库目录逐字节回到开头那个样子 ==="
# =====================================================================
$现在 = 目录指纹 $曲库
断言 '曲库里的文件数和开头一样' $现在.Count $原样.Count
$不一样 = @(Compare-Object $原样 $现在)
断言真 '每一份工程的「名字 + 长度 + MD5」都和开头一样（用户的曲子一份都没动）' `
  ($不一样.Count -eq 0) "$($不一样.Count) 处不同$(if ($不一样.Count) { '：' + ($不一样 | ForEach-Object { $_.InputObject }) -join ' / ' })"
断言真 '备份目录清掉了' (-not (Test-Path $备份)) $备份

"`n========== 结果 =========="
if (-not $跑完了) {
  '★ 跑到一半断了：断点之后的每一条都没验过，这不是全过'
  $fail = $fail + 1
} elseif ($fail -eq 0) { '全过' } else { "$fail 条红" }
exit $fail
