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
#   造的那几份临时素材也在这套备份里，万一中途崩了，还原那一支照样会把它们清掉。
#
# ⚠️ **素材按产品现在的读法造**（52 / 53 / 57 号之后）：曲库成员只认 `songs\<名字>.mid`，
#   而行上那格小字读的是 `songs\.work\<名字>.mproj` 的**文件头**（v2）。
#   所以 `.work\` 这一层也一并备份 / 还原 / 比对 —— 造缓存就是往那儿造。
#   往曲库根下放一份老 `.mproj` 今天**影响不到任何一行**（83 号票量过，见 造缓存 那段）。
#
# 前提：**非提权**（本脚本不点「开始演奏」，但同样不许提权跑）。
#   ⚠️ 60 号之后，非提权 shell 里启动会弹一颗**模态**的「要以管理员身份重启吗？」，
#      它把主窗整个禁用掉。本脚本**不带关它的那一段**（83 号票特意没动，属 60 号后续的活），
#      所以在这样的 shell 里跑，红的是「§1 开不出曲库窗」那一步 —— 那是别的票的红。

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
$work目录 = Join-Path $曲库 '.work'
$备份 = Join-Path $env:TEMP ("midiperformer-songs-backup-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
function 目录指纹([string]$目录) {
  # 目录不在 = 一份都没有（`.work\` 平时就不在：只有存过盘才会长出来）
  if (-not (Test-Path $目录)) { return }
  @(Get-ChildItem -File $目录 -EA SilentlyContinue | Sort-Object Name | ForEach-Object {
      $h = (Get-FileHash $_.FullName -Algorithm MD5).Hash
      "{0} {1} {2}" -f $_.Name, $_.Length, $h })
}
$原样 = @(目录指纹 $曲库)
$原样work = @(目录指纹 $work目录)
# 81 号票之后曲库成员只认 .mid，而根下还躺着用户自己那两份**老 .mproj**（cargo / Carulli）——
# 它们既不进列表，这一票也一份都不许动，所以数量记下来给 §2 / §9 当判据。
$原样mproj数 = @(Get-ChildItem -File $曲库 -Filter *.mproj -EA SilentlyContinue).Count
# 造出来的每一份（.mid 和 .work 里的缓存）都记在这儿，收尾一份不落地删掉。
$临时文件 = @()
Copy-Item $曲库 $备份 -Recurse -Force
"曲库 $曲库"
"  （备份到 $备份）"
"  开头有 $($原样.Count) 份："
$原样 | ForEach-Object { "    $_" }
"  缓存目录 .work 开头有 $($原样work.Count) 份："
$原样work | ForEach-Object { "    $_" }

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
    # `.work\` 同一条规矩：备份里没有就整条撤掉（本脚本自己造的），有就先清后拷。
    # ⚠️ 这一层**不能只管「删掉我造的那几份」** —— 半路崩掉时，谁造的都说不清。
    $备份work = Join-Path $备份 '.work'
    if (Test-Path $备份work) {
      if (-not (Test-Path $work目录)) { New-Item -ItemType Directory $work目录 | Out-Null }
      Get-ChildItem -File $work目录 -EA SilentlyContinue | Remove-Item -Force -EA SilentlyContinue
      Get-ChildItem -File $备份work | Copy-Item -Destination $work目录 -Force -EA SilentlyContinue
    } elseif (Test-Path $work目录) {
      Get-ChildItem -File $work目录 -EA SilentlyContinue | Remove-Item -Force -EA SilentlyContinue
      Remove-Item $work目录 -Recurse -Force -EA SilentlyContinue
    }
    Remove-Item $备份 -Recurse -Force -EA SilentlyContinue
  } catch { Write-Host "★ 还原曲库时出错：$_" }
}

# ---- 造素材：按产品现在的读法造（往 `.work\` 造 v2 缓存头）----
#
# 🔴 83 号票量过的那件事，写在这儿免得下次又走回头路：
#   从前这儿是「从曲库里捞一份老 `.mproj`、把里面的 `"Edited": false` 翻成 true」，
#   直接丢在**曲库根下**。今天那条路**已经影响不到任何一行**：
#     · 曲库成员只认 `.mid`（`SongLibrary.Names()` 拿 `EnumerateFiles()` 筛 `.mid`），
#       根下那份 `.mproj` 连一行都长不出来；
#     · 行上那格小字读的是 `songs\.work\<名字>.mproj`（`SongLibraryPanel.AddRow` 里
#       `_library.WorkPathOf(name)`）—— 不是 `.mid`，更不是根下那份老 `.mproj`。
#   ⇒ 素材必须造在**产品读的那个位置**，否则造出来的东西谁也看不见，红了还以为是字眼问题。
#
# 缓存是从 `cargo.mproj`（用户曲库里那份**老 v1 工程**）改出来的：只动文件头那几行，
# 谱面那 100KB 一个字节不碰。字段名照产品自己的写法（`SongProjectFile.WriteProject`）：
#   `Version`（v2 才认，见 `ProjectVersion` / `TryReadProjectHeader` 那道版本闸）、
#   `Edited`、`PlayableTrackCount`（产品写的就是这三个名字，别自己另起）。
#   `PlayableTrackCount > 0` ⇒ 那一行说「可播放」，`= 0` ⇒ 「不可播放」（`MarkFor` 的第三档）。
#   ⚠️ 老 v1 文件里没有 `PlayableTrackCount`，读它是 `0` —— 所以 v1 整个被判成读不出来，
#      这一票也正是拿这条当素材（见 §2 的坏工程那份）。
function 造缓存([string]$曲名, [bool]$改过, [int]$可弹轨数) {
  $mid = Join-Path $曲库 "$曲名.mid"
  Copy-Item (Join-Path $曲库 'cargo.mid') $mid -Force
  $源 = Join-Path $曲库 'cargo.mproj'
  $字节 = [System.IO.File]::ReadAllBytes($源)
  $有BOM = $字节.Length -ge 3 -and $字节[0] -eq 0xEF -and $字节[1] -eq 0xBB -and $字节[2] -eq 0xBF
  $文 = [System.Text.Encoding]::UTF8.GetString($字节, $(if ($有BOM) { 3 } else { 0 }), $字节.Length - $(if ($有BOM) { 3 } else { 0 }))

  # 三个模式都**锚在行首**（文件头那些字段各自一行），而且先验一句「只出现一次」——
  # 匹配到两处的话 `-replace` 会把谱面里同名的字段一起改掉，那是无声的坏素材。
  $版本式 = '(?m)^(\s*)"Version"\s*:\s*[0-9]+'
  $改过式 = '(?m)^(\s*)"Edited"\s*:\s*(true|false)'
  $歌式 = '(?m)^(\s*)"Song"\s*:'
  foreach ($式 in @($版本式, $改过式, $歌式)) {
    $n = @([regex]::Matches($文, $式)).Count
    if ($n -ne 1) { throw "cargo.mproj 里「$式」出现了 $n 次（要 1 次）—— 副本没法造" }
  }
  $文 = [regex]::Replace($文, $版本式, '$1"Version": 2')
  $文 = [regex]::Replace($文, $改过式, ('$1"Edited": ' + $(if ($改过) { 'true' } else { 'false' })))
  # 插在 `"Song"` 那一行前面：跟产品写出来的字段次序一致（版本 / 名字 / 改过 / 来源 / 可弹轨数）
  $文 = [regex]::Replace($文, $歌式, ('$1"PlayableTrackCount": ' + $可弹轨数 + "," + [Environment]::NewLine + '$1"Song":'))
  if ($文 -notmatch '"PlayableTrackCount"\s*:\s*' + $可弹轨数) { throw 'PlayableTrackCount 没插进文件头' }
  if ($文 -notmatch '"Version"\s*:\s*2') { throw 'Version 没改成 2' }
  [System.IO.File]::WriteAllText((Join-Path $work目录 "$曲名.mproj"), $文, (New-Object System.Text.UTF8Encoding($有BOM)))
}

# 🔴 那一格小字**脚本自己照真值表算一遍**，再拿去跟屏幕上读到的那格**逐字**比。
#    判据不许放松成「是那五句里的某一句」—— 自己按缓存头算出该说哪一句，比的才是
#    「屏幕上那句话跟缓存里的事实对不对得上」。产品那边的算法是 `SongLibraryPanel.MarkFor`
#    （真值表的规格在 docs/spec-存储与曲库.md 的四态那一节）：
#      ① 缓存**在不在**（不是读不读得出）—— 不在 ⇒ 那格空的（散装 .mid 不带标记，
#         「本程序没给它存过盘」不是「没动过」）；
#      ② 缓存在、头读不出来（坏了 / 版本不是 2，v1 也算） ⇒ 「读不出来」；
#      ③ 否则 Edited ? 「编辑过 · 」: "" 再拼（可弹轨数 > 0 ? 「可播放」: 「不可播放」）。
function 该显什么([string]$名) {
  $缓存 = Join-Path $work目录 "$名.mproj"
  if (-not (Test-Path $缓存)) { return '' }
  try { $头 = (Get-Content -Raw -Encoding UTF8 $缓存) | ConvertFrom-Json } catch { return '读不出来' }
  if ($头 -isnot [System.Management.Automation.PSCustomObject]) { return '读不出来' }
  if ($头.Version -ne 2) { return '读不出来' }
  $可弹 = 0
  if ($头.PlayableTrackCount -is [int] -or $头.PlayableTrackCount -is [long]) { $可弹 = [int]$头.PlayableTrackCount }
  $弹字 = if ($可弹 -gt 0) { '可播放' } else { '不可播放' }
  if ($头.Edited -eq $true) { return "编辑过 · $弹字" }
  return $弹字
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
# ⚠️ 这是**函数返回管道**，不是数组：调用方**一律**得写 `@(除它之外 $dw).Count`。
#    不套 `@()` 的话，回来 0 个是 `$null`、回来 1 个是**标量**，两种情况下 `.Count`
#    在严格模式下都直接抛「在此对象上找不到属性 Count」—— 0 个和 1 个报的错一模一样，
#    红的位置还指在断言那一行（83 号票实跑栽在这儿：确认框到底弹没弹出来，从那条错里
#    根本看不出来）。要多数几个窗口时，先用 `$x = @(除它之外 $dw)` 落到变量上再数。
function 除它之外([IntPtr]$这窗) { @(别窗 | Where-Object { $_.ToInt64() -ne $这窗.ToInt64() }) }
# 「那一问」= 删除确认框：除 $dw 之外、**标题正好是「删除曲子」**的那个顶层窗。
# 为什么不写成「除 $dw 之外只剩一个窗就算」：$dw 自己的 ToolTip、Avalonia 那些细碎的浮层
# 也是本进程的顶层窗，按个数数会把它们算进来（`Others` 那道 80x60 的闸门拦不住
# 296x60 的提示框）。按**标题**认它，比数个数更死 —— 下面紧跟着还有一条
# 「标题是「删除曲子」」的逐字断言，两条说的是同一件事，不冲突。
function 那一问([IntPtr]$除谁) { @(除它之外 $除谁 | Where-Object { [P40]::Title($_) -eq '删除曲子' }) }
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
# 一行里那三格：曲名 / 那一格小字（四态真值表） / 那颗叉。
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
"`n=== 0. 干净实例：工具栏那一排入口 ==="
# =====================================================================
$h = 起窗口
$root = $AE::FromHandle($h)
$win = $root.Current.BoundingRectangle
"窗口 $([int]$win.Width)x$([int]$win.Height) @ $([int]$win.X),$([int]$win.Y)"

# 那一排 = 中线跟「歌曲库」一样高的那些按钮 / 菜单（±2px）。
# 拿「歌曲库」当锚、不写死坐标，也不写死那一排里有几样：存盘组（保存 / 另存为…）和
# 还没搬走的「导入 MIDI…」都长在这一排里，可它们各有各的工单（存盘组归 55、导入归 55），
# 这一票只钉自己名下那三样（歌曲库 / 操作 / 演奏）和「整排长相统一」。
$曲库钮 = 按id $root 'LibraryButton'
if ($null -eq $曲库钮) { throw '主窗上没有「歌曲库」那颗按钮（LibraryButton）—— 后面每一节都建立在它身上' }
$中线 = (矩形 $曲库钮).Y + (矩形 $曲库钮).Height / 2
$那一排 = @()
foreach ($t in @($CT::Button, $CT::MenuItem)) {
  foreach ($e in @(找类型 $root $t)) {
    $r = 矩形 $e
    if ([double]::IsNaN($r.X) -or $r.Width -le 0) { continue }
    if ([Math]::Abs(($r.Y + $r.Height / 2) - $中线) -le 2) { $那一排 += $e }
  }
}
$那一排 = @($那一排 | Sort-Object { (矩形 $_).X })
$名字们 = @($那一排 | ForEach-Object { $_.Current.Name })
"  从左到右（中线跟「歌曲库」一样高的）：$($名字们 -join ' | ')"
function 序号([string]$名) { $i = 0; foreach ($n in $名字们) { if ($n -eq $名) { return $i }; $i++ } return -1 }

$三样 = @('歌曲库', '操作', '演奏')
$缺 = @($三样 | Where-Object { $名字们 -notcontains $_ })
断言 '「歌曲库」「操作」「演奏」三样一个不少（40 号要的那三个入口）' $缺.Count 0
if ($缺.Count) { throw "入口不齐（缺 $($缺 -join ' / ')），后面每一节都建立在它们身上" }
断言字 '最左那样就是「歌曲库」（和别的一样是一颗并排的按钮，不是塞进下拉里的菜单项）' $名字们[0] '歌曲库'
断言 '「演奏」紧挨着「操作」右边（用户原话：将「演奏器」改名为「演奏」，并放在「操作」旁边）' `
  (序号 '演奏') ((序号 '操作') + 1)

# 「平齐 / 并排」在屏幕上的意思：**同高、同中线、左到右不重叠**。
# ⚠️ 从前这儿还有一条「三处间隙一样宽」—— 它描述的是「文件 | 歌曲库 | 操作 | 演奏」那一排。
#    后来「文件」那个下拉被拆掉、存盘那两颗上到台面收进一个组里（组内组外的间隙本来就不一样，
#    见 docs/spec-界面改版.md 的「工具栏 —— 排法 B（存盘成组）」）⇒ 那条判据描述的那种排法
#    今天已经不存在了。它不是被放松掉的：并排这件事现在由下面三条钉着，而且是拿整排（不管排里
#    有几样、谁加的）一起量的。
$高 = @($那一排 | ForEach-Object { (矩形 $_).Height })
$中 = @($那一排 | ForEach-Object { $r = 矩形 $_; $r.Y + $r.Height / 2 })
$左 = @($那一排 | ForEach-Object { (矩形 $_).X })
$右 = @($那一排 | ForEach-Object { $r = 矩形 $_; $r.X + $r.Width })
$间 = @(1..($那一排.Count - 1) | ForEach-Object { $左[$_] - $右[$_ - 1] })
"  高 $($高 -join ',')  中线Y $($中 -join ',')  间隙 $($间 -join ',')"
断言真 '那一排一样高（差 ≤ 2px）' ((($高 | Measure-Object -Max).Maximum - ($高 | Measure-Object -Min).Minimum) -le 2) `
  "最高 $([int]($高|Measure-Object -Max).Maximum) 最矮 $([int]($高|Measure-Object -Min).Minimum)"
断言真 '那一排在同一条中线上（差 ≤ 2px）——「平齐」' ((($中 | Measure-Object -Max).Maximum - ($中 | Measure-Object -Min).Minimum) -le 2) `
  "中线 $($中 -join ',')"
断言真 '相邻两样不叠在一起（左到右挨着排）——「并排」' (@($间 | Where-Object { $_ -lt 0 }).Count -eq 0) `
  "间隙 $($间 -join ',')"

# ⚠️ `-join` 写在**实参位置**上不行：命令模式的解析器会把 `-join` 当成参数名，
#    数组就会被 `"$实际"` 按默认分隔符（空格）拼出来。先算成变量再传。
$库演类型 = (@($那一排[(序号 '歌曲库')], $那一排[(序号 '演奏')]) | ForEach-Object { $_.Current.ControlType.ProgrammaticName }) -join ','
断言 '「歌曲库」和「演奏」是按钮（按一下就开窗，没有下拉）' $库演类型 'ControlType.Button,ControlType.Button'
断言字 '「歌曲库」那颗按钮就是 LibraryButton' $曲库钮.Current.AutomationId 'LibraryButton'
断言字 '「演奏」那颗按钮还是叫 PerformerButton（老测试按这个名字找它）' $那一排[(序号 '演奏')].Current.AutomationId 'PerformerButton'
断言字 '「操作」还是那个下拉菜单（撤销 / 重做住在里面）' $那一排[(序号 '操作')].Current.ControlType.ProgrammaticName 'ControlType.MenuItem'

# 右上角那颗「演奏器…」蓝按钮撤了没有：窗口里不许再有任何控件说「演奏器」
$带演奏器 = @(全部名字 | Where-Object { $_ -like '*演奏器*' })
断言 '窗口里没有任何控件叫「演奏器」（右上那颗蓝按钮撤了）' $带演奏器.Count 0
$带歌曲库 = @(全部名字 | Where-Object { $_ -eq '歌曲库' })
断言 '窗口里叫「歌曲库」的控件只有一个（就是那颗按钮，侧栏标题没了）' $带歌曲库.Count 1

# 曲库那条列表真的不在主窗里了
$列表数 = @(找类型 $root $CT::List).Count + @(找类型 $root $CT::ListItem).Count
断言 '主窗里一个列表都没有（曲库那条 ListBox 搬走了）' $列表数 0

# =====================================================================
"`n=== 1. 点「歌曲库」→ 开出一个窗口，里面是完整曲名 + 那格小字 + 每行一颗 × ==="
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
# 行 = 曲库成员 = `songs\` 下的一份 `.mid`（52 号之后只认 .mid；根下那两份老 .mproj
# 是用户自己的东西，既不是成员、也不该被这份脚本碰 —— 它们照样进 §9 的指纹比对）。
$应是 = @(Get-ChildItem -File $曲库 -Filter *.mid | Sort-Object Name)
断言 '列表里的行数 = 曲库目录里的 .mid 份数' $行们.Count $应是.Count
断言字 '「N 首」和行数对得上' (按id $droot 'CountText').Current.Name "$($应是.Count) 首"

$底 = (按id $droot 'StatusText').Current.Name
断言字 '页脚那一行一开始是空的（还没删过东西）' $底 ''
断言真 '页脚上有一颗「关闭」' ($null -ne (按id $droot 'CloseButton')) ''

# ---- 逐行：曲名 / 那格小字 / ×，以及**完整名字的像素证人** ----
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
  # 那格小字照真值表验（该说哪一句由**缓存头**算出来，见 该显什么）。
  # ⚠️ 「该是空的」这一格也得验：空 = 「本程序没给它存过盘」，它跟「没动过」是两件事，
  #    从前那句 `@('改过','没动过','读不出来') -contains …` 在空的一格上必然红。
  $该 = 该显什么 $名
  $读到 = if ($null -eq $格.meta) { '（树里没有那格）' } else { $格.meta.Current.Name }
  if ($该 -eq '') {
    断言真 "「$名」那一行没有那格小字（没有缓存 ⇒ 不带标记，不是「没动过」）" `
      ($null -eq $格.meta -or $格.meta.Current.Name -eq '') "读到「$读到」"
  } else {
    断言真 "「$名」那一行的那格小字在树里（读不到就成假绿了）" ($null -ne $格.meta) "读到「$读到」"
    断言字 "「$名」那一行的小字跟缓存头对得上（四态真值表）" $读到 $该
  }
  断言真 "「$名」这一行有一颗 ×" ($null -ne $格.叉 -and $格.叉.Current.Name -eq '×') "读到「$($格.叉.Current.Name)」"
}

# 关窗收工：点页脚那颗「关闭」（它挂着 IsCancel，Esc 也走得通，但点按钮更实在）
点后等 { 按id (曲库根) 'CloseButton' } $dw '关闭' { @(别窗).Count -eq 0 } | Out-Null
断言 '点「关闭」就把曲库窗口关掉了' @(别窗).Count 0

# =====================================================================
"`n=== 2. 那格小字是真从缓存文件头里读出来的（素材按产品现在的读法造）==="
# =====================================================================
# 三份素材 = 一份 `.mid`（cargo.mid 的副本）+ `.work\` 里一份 v2 缓存头，四个格子各占一格。
# 那份缓存的可弹轨数写 **5**：57 号上机时产品自己存出来的 cargo 就是 5（它的真值），
# 造素材照产品自己写出来的数写，别凭空编一个。
$临时曲 = @(
  @{ 名 = '_verify40_改过';   改过 = $true;  可弹 = 5 },   # 编辑过 · 可播放
  @{ 名 = '_verify40_没改过'; 改过 = $false; 可弹 = 5 },   # 可播放（「缓存在 ≠ 改过」那一格）
  @{ 名 = '_verify40_弹不动'; 改过 = $false; 可弹 = 0 }    # 不可播放
)
foreach ($f in $临时曲) {
  造缓存 $f.名 $f.改过 $f.可弹
  $临时文件 += @((Join-Path $曲库 "$($f.名).mid"), (Join-Path $work目录 "$($f.名).mproj"))
}
# 坏工程：`.mid` 和缓存**都乱写**。缓存坏了 ⇒ 那格说「读不出来」；
# .mid 也坏了 ⇒ 双击它**打不开**（§6 验的就是这个：坏的那份不许把窗口关了、也不许弄坏手上那首）。
# ⚠️ 光造一份坏缓存不够：缓存读不出来时产品会**降级去读 .mid**，.mid 好好的就照装不误。
$坏mid = Join-Path $曲库 '_verify40_坏工程.mid'
$坏缓存 = Join-Path $work目录 '_verify40_坏工程.mproj'
[System.IO.File]::WriteAllText($坏mid, '这不是一个 MIDI', (New-Object System.Text.UTF8Encoding($false)))
[System.IO.File]::WriteAllText($坏缓存, '这不是一个工程', (New-Object System.Text.UTF8Encoding($false)))
$临时文件 += @($坏mid, $坏缓存)
"  造了四份临时曲子：4 份 .mid（一份是坏工程）+ 4 份 .work 里的缓存头"
# §3 / §4 拿「改过」那一份走 × 的删除流程。删的是它那份 `.mid`；
# 52/53 之后 `SongLibrary.Delete` 把 `.work\` 里那份缓存**一起删**（这也要验，见 §4）。
$改过的 = Join-Path $曲库 '_verify40_改过.mid'
$改过的缓存 = Join-Path $work目录 '_verify40_改过.mproj'

点后等 { (按种类 '歌曲库')[0] } $h '歌曲库' { $null -ne (曲库窗) } | Out-Null
$dw = 曲库窗
if (-not $dw) { throw '第二次开曲库窗没开出来' }
$droot = $AE::FromHandle($dw)
$该行数 = $应是.Count + 4
断言 "现在列表里是 $该行数 行（原来 $($应是.Count) + 造的 4 份）" @(找类型 $droot $CT::ListItem).Count $该行数
foreach ($f in $临时曲) {
  断言真 "列表里有「$($f.名)」这一行（没有 .mid 就长不出行来）" ($null -ne (某行 $droot $f.名)) ''
}

$行改 = 某行 $droot '_verify40_改过'
$行没 = 某行 $droot '_verify40_没改过'
$行弹 = 某行 $droot '_verify40_弹不动'
$行坏 = 某行 $droot '_verify40_坏工程'
断言字 '临时那份（缓存头 Edited=true、可弹轨数 5）那格小字是「编辑过 · 可播放」' `
  (行里 $行改 '_verify40_改过').meta.Current.Name '编辑过 · 可播放'
断言字 '没改过但有缓存那份那格小字是「可播放」（缓存在 ≠ 改过）' `
  (行里 $行没 '_verify40_没改过').meta.Current.Name '可播放'
断言字 '缓存头里可弹轨数是 0 那份那格小字是「不可播放」（弹不了不是错误，是事实）' `
  (行里 $行弹 '_verify40_弹不动').meta.Current.Name '不可播放'
断言字 '乱写的那份那格小字是「读不出来」' `
  (行里 $行坏 '_verify40_坏工程').meta.Current.Name '读不出来'

# 🔴 这一条是 83 号票量出来的那件事的**回归门**：曲库根下那份老 cargo.mproj（v1、Edited=false）
#    今天**影响不到那一行** —— 行上那格读的是 `.work\cargo.mproj`（SongLibraryPanel.AddRow 里
#    那个 `WorkPathOf`），它不存在 ⇒ 那格必须是**空的**。哪天要是又有人回头读根下那份老工程，
#    这一条会当场红：v1 里没有 PlayableTrackCount，读出来是 0，那格会变成「不可播放」之类的东西。
if (Test-Path (Join-Path $work目录 'cargo.mproj')) {
  "  （cargo 在 .work 里已经有缓存了，那一格不空是应该的 —— 按真值表验）"
  断言字 'cargo 那格小字跟它**在 .work 里的**缓存头对得上' `
    (行里 (某行 $droot 'cargo') 'cargo').meta.Current.Name (该显什么 'cargo')
} else {
  断言字 'cargo 那格小字是空的（老 .mproj 不算数：行读的是 .work 里的缓存，那儿没有它的缓存）' `
    (行里 (某行 $droot 'cargo') 'cargo').meta.Current.Name ''
}
断言 '用户那两份老 .mproj 还躺在曲库根下（这一票一份都没动它们）' `
  (@(Get-ChildItem -File $曲库 -Filter *.mproj).Count) $原样mproj数

# =====================================================================
"`n=== 3. 每行那颗 ×：先「取消」（什么都不该发生）==="
# =====================================================================
$格 = 行里 $行改 '_verify40_改过'
点后等 { (行里 (某行 (曲库根) '_verify40_改过') '_verify40_改过').叉 } $dw '那一行的 ×' { @(那一问 $dw).Count -ge 1 } | Out-Null
$确认 = @(那一问 $dw)
断言 '点 × 弹出了一个顶层窗，标题叫「删除曲子」' $确认.Count 1
if ($确认.Count -ne 1) {
  throw "确认框没弹出来（除曲库窗之外的顶层窗：$((除它之外 $dw | ForEach-Object { '「' + [P40]::Title($_) + '」' }) -join ',')）"
}
$croot = $AE::FromHandle($确认[0])
断言字 '那一问的标题是「删除曲子」' ([P40]::Title($确认[0])) '删除曲子'
$文 = (找类型 $croot $CT::Text | ForEach-Object { $_.Current.Name }) -join ' / '
"  那一问上写着：「$文」"
断言含 '那一问说清了「文件会一起删掉，撤不回来」' $文 '撤不回来'

点后等 { (按名字 ($AE::FromHandle($确认[0])) '取消')[0] } $确认[0] '取消' { @(那一问 $dw).Count -eq 0 } | Out-Null
断言 '点「取消」之后那一问没了' @(那一问 $dw).Count 0
断言真 '点「取消」之后文件还在' (Test-Path $改过的) $改过的
断言 '点「取消」之后那一行还在' (@(找类型 $AE::FromHandle($dw) $CT::ListItem)).Count $该行数

# =====================================================================
"`n=== 4. 再点一次 ×，这回按「删除」：文件真的没了、行也没了 ==="
# =====================================================================
$droot = $AE::FromHandle($dw)
$格 = 行里 (某行 $droot '_verify40_改过') '_verify40_改过'
点后等 { (行里 (某行 (曲库根) '_verify40_改过') '_verify40_改过').叉 } $dw '那一行的 ×' { @(那一问 $dw).Count -ge 1 } | Out-Null
$确认 = @(那一问 $dw)
if ($确认.Count -ne 1) { throw '第二次点 × 没弹出确认框' }
$croot = $AE::FromHandle($确认[0])
点后等 { (按名字 ($AE::FromHandle($确认[0])) '删除')[0] } $确认[0] '删除' { @(那一问 $dw).Count -eq 0 } | Out-Null
断言 '按「删除」之后确认框收了' @(那一问 $dw).Count 0
断言真 '文件真的从盘上删掉了' (-not (Test-Path $改过的)) "Test-Path $改过的 = $(Test-Path $改过的)"
断言真 '缓存也跟着删了（.work 里那一份，53 号之后两份一起走）' (-not (Test-Path $改过的缓存)) `
  "Test-Path $改过的缓存 = $(Test-Path $改过的缓存)"
$droot = $AE::FromHandle($dw)
断言 "那一行也没了（$该行数 → $($该行数 - 1)）" @(找类型 $droot $CT::ListItem).Count ($该行数 - 1)
断言真 '那一行确实找不到了' ($null -eq (某行 $droot '_verify40_改过')) ''
断言字 "「N 首」跟着减到 $($该行数 - 1)" (按id $droot 'CountText').Current.Name "$($该行数 - 1) 首"
$底 = (按id $droot 'StatusText').Current.Name
断言含 '页脚上说了那一句' $底 '已从曲库删掉'
断言 '曲库窗口**留着**（删完还能接着删下一首）' @(除它之外 $dw).Count 0

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
  @(别窗).Count -eq 0 -and $名 -eq 'Carulli_Duetto_No2_Op4'
} 30 2 6 | Out-Null
断言 '双击打开之后曲库窗口自己关了' @(别窗).Count 0
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
点后等 { 按id (曲库根) 'CloseButton' } $dw '关闭' { @(别窗).Count -eq 0 } | Out-Null
断言 '点「关闭」把曲库窗收了' @(别窗).Count 0

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
    # 造出来的每一份临时素材：删干净（`.mid` 和 `.work` 里的缓存都在 $临时文件 里）。
    # 「改过」那一份的 .mid 可能已经被 UI 删掉了（§4），删不到不算错。
    foreach ($p in @($临时文件)) {
      if (Test-Path $p) { Remove-Item $p -Force -EA SilentlyContinue }
    }
    # 再走一遍整目录还原：`.work\` 那一支在里面（备份里没有 `.work` 就整条撤掉）。
    还原曲库
  } catch { Write-Host "★ 收尾时出错：$_" }
}

# =====================================================================
"`n=== 9. 收尾：曲库目录（含 .work 那一层）逐字节回到开头那个样子 ==="
# =====================================================================
$现在 = @(目录指纹 $曲库)
断言 '曲库里的文件数和开头一样' $现在.Count $原样.Count
$不一样 = @(Compare-Object $原样 $现在)
断言真 '每一份曲子的「名字 + 长度 + MD5」都和开头一样（用户的曲子一份都没动）' `
  ($不一样.Count -eq 0) "$($不一样.Count) 处不同$(if ($不一样.Count) { '：' + ($不一样 | ForEach-Object { $_.InputObject }) -join ' / ' })"
$现在work = @(目录指纹 $work目录)
断言 '缓存目录（.work）里的文件数和开头一样' $现在work.Count $原样work.Count
$不一样work = @(Compare-Object $原样work $现在work)
断言真 '缓存里每一份的「名字 + 长度 + MD5」也都和开头一样（脚本没在用户缓存里留下东西）' `
  ($不一样work.Count -eq 0) "$($不一样work.Count) 处不同$(if ($不一样work.Count) { '：' + ($不一样work | ForEach-Object { $_.InputObject }) -join ' / ' })"
断言真 '备份目录清掉了' (-not (Test-Path $备份)) $备份

"`n========== 结果 =========="
if (-not $跑完了) {
  '★ 跑到一半断了：断点之后的每一条都没验过，这不是全过'
  $fail = $fail + 1
} elseif ($fail -eq 0) { '全过' } else { "$fail 条红" }
exit $fail
