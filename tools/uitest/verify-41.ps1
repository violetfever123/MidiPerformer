# 41 号工单的实机验证：演奏器下拉框不再按「谁最像主旋律」打分排序，改为**原曲下标升序**，
# 默认选中第一条能弹的。
#
# 用法: pwsh -NoProfile -File verify-41.ps1     （脚本自己起 app、自己收尾）
#
# 屏幕上能看见的证人只有一个：演奏器窗口那个轨下拉框。要验两句：
#   · 里面每一项**开头那个序号严格递增**；
#   · 选中的是**第一项**（不是旧实现挑出来的那条「最像主旋律的」）。
#
# **判别力靠挑曲子**：要是随便挑一首，旧实现也给同一个顺序，这两条断言在改之前就是绿的，
# 那就什么都没证明。挑的是 `sm_mol.mid` —— `.scratch/probe-41` 量过：
#   可弹 8 条；原曲下标 [0,1,2,3,4,6,10,11]（→ 序号 01,02,03,04,05,07,11,12，**中间是断的**）；
#   旧打分给的顺序是 [11,0,6,4,3,2,1,10]（→ 序号 12,01,07,05,04,03,02,11），
#   默认选中下标 11「Melody」（旧分 133.90）。所以改之前：序号不递增、第一项是 12 —— 两条都红。
#
# 序号里那个断档（缺 06，以及缺 08/09/10）顺带证明了另一件事：这个号是**原曲里的位置**，
# 不是它在下拉框里的位置。所以还断言一条「序号不是 1..8 连号」。
#
# 这一票不碰曲库（只读它、点开一首曲子），所以没有 25 号那套备份/还原。

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
# ---- 共用库（67 号票并的那份）---------------------------------------------
# 这里原先内联着两个类（一份 P/Invoke + 驱动输入 + 取窗，一份只管「这个窗在屏幕上的框」）——
# 两个都并进了 tools/uitest/uitest-lib.ps1（库的类名是 P40），这边改成点源。
# 点源的那一刻库自己的四条前提检查就跑：没桌面会话 / 有实例在跑 / 分辨率不够 / 没编译 —— 一律抛。
. (Join-Path $PSScriptRoot 'uitest-lib.ps1')
# 搬过来只有一处「路径」要改（不是逻辑）：exe 从 `$PSScriptRoot 上一级变成上两级（.scratch/ → tools/uitest/）。
# 没改的话第一句自查就抛「没找到 …—— 先编译」，一次实机跑就是这么废掉的。
#
# 成员名统一（也不是逻辑）：原内联类里那些静态方法进库之后仍是同一个类（P40）的成员，两处改了名：
#   · 取窗的「这个点是哪个窗」在库里叫 WinAt（库自己那个 At 是「截图里某一格的颜色」）——
#     这一份不用 At，所以碰不到那次改名。
#   · 原先直写 IsWindowVisible / IsWindowEnabled，库里各有一个同义的壳（Visible / Enabled，
#     底下是同一个 P/Invoke）—— 统一用库里的名字，不在库里放两个名字做同一件事。
#
# ⚠️ 并库引进来的一处**非改名**改动：库的第 48 行是 `Set-StrictMode -Version Latest`，
# 而库是被点源的 —— 严格模式按作用域生效、dot-source 就跑在调用方的作用域里，于是**这一份脚本
# 也一并被收紧了**。收紧之后「辅助函数返回 0 个或 1 个元素 → 输出被拆成 $null / 标量」的地方，
# 读 `.Count` 会当场抛（严格模式下标量上没有 Count 属性）。修法：**凡是从这些函数取值、又读
# `.Count` 的地方，自己包一层 `@(...)`**（共 9 处）。`.Count` / `[0]` / `foreach` 在两种模式下
# 结果一样，只是不再依赖宽松模式兜底。**辅助函数本身一个字没动。**
# 反面教材（67 号实测）：给辅助函数加前置逗号 `, @(...)` 看着更「治本」，其实把**管道**废了 ——
# `找类型 $CT::Button | Where-Object {...}` 收到的是「一整个数组」这一个对象，当场数成 1。
#
# ✅ **80 号票已修**（依据：71 号 `4fd9f40`「点『演奏』把主窗那首递给演奏器」+ 78 号把素材种进 Debug 曲库）。
# 改的是**两件事**：
#   · 载歌不再走演奏器里那颗文件选择器（47 号已按规格把它连同「曲目」行一起删了）——
#     改成**在主窗的曲库模态窗里装上那一首**（`开一首` 的新身体），曲库里那份 `sm_mol.mid`
#     就是取证要的那一首（78 号种的，18236 字节，与语料那分同源）；
#   · **顺序倒过来：先载歌、后按「演奏」**。42 号之后 `PerformerButton.IsEnabled = _song is not null`，
#     主窗里没歌时那颗按钮是灰的，对灰按钮 Invoke 抛的是个**光秃秃的 System.Exception** ——
#     80 号实测：改之前这一份**开跑 20 秒内就死**（红在第 1 节那句 Invoke，压根走不到载歌）。
# 曲目那一行（`SongValue`）也是同一次改版删掉的（探针 `probe-47-*` 的零命中清单里有它），
# 所以「递进来的是哪一首」改由**提示行那两个数**坐实：sm_mol = 13 条轨里 8 条可演奏（78 号量）。
# 这一票不碰产品代码，也不碰曲库（只读它、点开一首曲子）。

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$条件 = { param($t) New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $t) }

$fail = 0
function 断言真([string]$名字, [bool]$条件, [string]$原文) {
  if ($条件) { "  OK   $名字（$原文）" } else { "  FAIL $名字（$原文）"; $script:fail++ }
}

function 找类型([object]$类型) { @($root.FindAll($TS::Descendants, (& $条件 $类型))) }
function 某根里([object]$根, [object]$类型) { @($根.FindAll($TS::Descendants, (& $条件 $类型))) }
function 某根编号([object]$根, [string]$id) {
  @($根.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition(
    $AE::AutomationIdProperty, $id))))
}
function 某根文字([object]$根, [string]$含) {
  @(某根里 $根 $CT::Text | Where-Object { $_.Current.Name -like "*$含*" })
}
function 按钮([string]$名) { @(找类型 $CT::Button | Where-Object { $_.Current.Name -eq $名 }) }
function 矩形($e) { $e.Current.BoundingRectangle }
function 中心($e) { $r = 矩形 $e; @([int]($r.X + $r.Width/2), [int]($r.Y + $r.Height/2)) }
function 支持的模式($e) { @($e.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName }) }
function 某属性($e, [string]$名) { try { $e.Current.$名 } catch { "（取不到 $名）" } }
# 原先这儿还有 `类名` 和 `找原生框` 两个（认 `#32770*`，是给原生文件框用的）：47 号删掉演奏器
# 的文件选择器之后，这条路上再也不会冒出 `#32770` 了，留着就是两个「调用不报错、永远返回空」
# 的陷阱。80 号把它们连同 `开一首` 的老身体一起删掉。
function 别的顶层窗 { @([P40]::Others($脚本PID, $h)) }
# TextBox 里的字在 ValuePattern 上，不在 `Current.Name` 上（那个多半是空的）——
# 主窗那个曲名框（`SongNameBox`）就得这么读（verify-27 里那个 `取值` 是同一个东西）。
function 取值($e) { $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
function 查别的窗([string]$标) {
  $o = @(别的顶层窗)
  $说 = if ($o.Count) { ($o | ForEach-Object { $r = [P40]::RectOf($_); "$([P40]::Describe($_)) 框=$($r[0]),$($r[1]) $($r[2])x$($r[3])" }) -join ' ;; ' } else { '没有' }
  Write-Host "  [$标] app 的别的顶层窗 $($o.Count) 个：$说"
}
# 一行「这是什么控件」，诊断用。UIA 里下探一层要显式拿 TreeWalker，所以这里只列自己。
function 长相($e) {
  "类型=$($e.Current.ControlType.ProgrammaticName -replace 'ControlType\.','') id='$($e.Current.AutomationId)' 名='$($e.Current.Name)' 隐=$($e.Current.IsOffscreen)"
}

# 抛异常也要把实例收掉。先前没有这个 trap，一条 throw 就把 app 留在桌面上，
# 下一次跑又撞上「已经有 MidiPerformer 在跑」那条自检 —— 自己给自己下绊子。
$proc = $null
trap {
  Write-Host "`n[异常] $_"
  if ($null -ne $proc) {
    try { $proc.Refresh(); if (-not $proc.HasExited) { $proc.Kill(); Write-Host "收了实例 PID $($proc.Id)" } } catch { }
  }
  break
}

# ---------- 起一个干净实例 ----------
# **不替用户关他自己开着的实例**：先看有没有，有就停手报出来。
# （verify-40-lib 的「起窗口」会把所有 MidiPerformer 都关掉，桌面上有别人的实例时不能用。）
$在跑的 = @(Get-Process -Name MidiPerformer -ErrorAction SilentlyContinue)
if ($在跑的.Count) {
  throw "已经有 MidiPerformer 在跑（PID $(($在跑的 | ForEach-Object { $_.Id }) -join ', ')）—— 这是脚本自己的规矩：不替你关窗口，请你先关掉再跑"
}

$exe = Join-Path $PSScriptRoot '..\..\MidiPerformer.App\bin\Debug\net8.0\MidiPerformer.exe'
if (-not (Test-Path $exe)) { throw "没找到 $exe —— 先编译" }
$proc = Start-Process -FilePath $exe -PassThru
$期限 = (Get-Date).AddSeconds(30)
do { Start-Sleep -Milliseconds 500; if ($proc.HasExited) { throw "窗口没起来，退出码 $($proc.ExitCode)" }; $proc.Refresh() }
while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $期限)
if ($proc.MainWindowHandle -eq 0) { throw '等不到窗口句柄' }
Start-Sleep -Seconds 3
$h = $proc.MainWindowHandle
$脚本PID = [uint32]$proc.Id
"起了个干净实例：PID $($proc.Id)"

if (-not [P40]::Take($h)) { throw '拽不到前台' }
$摆 = { param($摆X, $摆Y, $摆宽, $摆高)
  [void][P40]::SetWindowPos($h, [IntPtr]::Zero, $摆X, $摆Y, $摆宽, $摆高, 0x0004 -bor 0x0010)
  Start-Sleep -Milliseconds 700
  ($AE::FromHandle($h)).Current.BoundingRectangle
}
$r1 = & $摆 405 450 2360 1520
$win = & $摆 (810 - [int]$r1.X) (900 - [int]$r1.Y) (4720 - [int]$r1.Width) (3040 - [int]$r1.Height)
"窗口 $([int]$win.Width)x$([int]$win.Height) @ $([int]$win.X),$([int]$win.Y)"
if ([int]$win.Width -ne 2360 -or [int]$win.Height -ne 1520) { throw "窗口没摆成 2360x1520（摆完 $([int]$win.Width)x$([int]$win.Height)）" }

$root = $AE::FromHandle($h)
$期限 = (Get-Date).AddSeconds(20)
while ((Get-Date) -lt $期限 -and -not (@(某根编号 $root 'SongNameBox').Count)) {
  Start-Sleep -Milliseconds 500; $root = $AE::FromHandle($h)
}
Start-Sleep -Seconds 2
$root = $AE::FromHandle($h)

# 原先这儿还有个「停车点」（点的落点是原生文件框里那颗「打开」）：80 号起这一份不再点鼠标了 ——
# 载歌靠 UIA 选中曲库那一行 + 回车，按「演奏」靠 InvokePattern，两个都不落点。

# =====================================================================
"`n=== 1. 在主窗里载入 sm_mol.mid（判别力全在这首曲子上）==="
# =====================================================================
# ⚠️ **顺序是这一票的一半：先载歌、后按「演奏」（第 2 节）。**
#   · 主窗里没歌的时候那颗「演奏」是灰的（`PerformerButton.IsEnabled = _song is not null`，42 号票），
#     对灰按钮 Invoke 抛的是个**光秃秃的 System.Exception** —— 80 号实测：改之前这一份就死在那儿
#     （开跑 20 秒内报 `Exception of type 'System.Exception' was thrown.`，压根走不到载歌那一步）；
#   · 71 号之后的真实用户路径也是这个顺序：曲子先在主窗手上，点「演奏」由主窗把它递进演奏器。
#
# 载歌走**曲库模态窗**（主窗工具栏「歌曲库」那颗开出来的，里面才有 `SongList`；直接数主窗的
# ListItem 是 0 行 —— 40 号把曲库搬出去之后主窗一个都不剩）。47 号把演奏器里的「曲目」行
# 连同那颗文件选择器一起删了，曲子不再由演奏器自己挑，「载一首」这件事现在只发生在主窗。
#
# 曲库那扇窗是**模态**的，装上了它自己才关（`MainWindow.axaml.cs` 的 `OnLibraryClick` 里
# `dialog.OpenRequested` 那两行：`TryOpenLibrarySong(name) is { } error` → `window.ShowMessage(error)`，
# 否则 `window.Close()`；那句话落在页脚的 `StatusText`，见 `SongLibraryWindow.axaml.cs` 的 `ShowMessage`），
# 所以「窗口关了」本身就是「装上了」的判据；下面再拿主窗曲名框对一遍名字。
# 列表是异步填的，每一段都得轮询等。
function 开一首([string]$曲名) {
  $库钮 = @(某根编号 ($AE::FromHandle($h)) 'LibraryButton')
  if ($库钮.Count -ne 1) { throw "工具栏上找不到「歌曲库」那颗按钮（按 AutomationId=LibraryButton 数到 $($库钮.Count) 颗）" }
  [void]$库钮[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

  # 等模态窗：它是 app 的另一个可见顶层窗，里面有 SongList
  $期限 = (Get-Date).AddSeconds(20)
  $库窗 = [IntPtr]::Zero
  while ((Get-Date) -lt $期限 -and $库窗 -eq [IntPtr]::Zero) {
    foreach ($w in 别的顶层窗) {
      try { if (@(某根编号 ($AE::FromHandle($w)) 'SongList').Count -gt 0) { $库窗 = $w; break } } catch { }
    }
    if ($库窗 -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 400 }
  }
  if ($库窗 -eq [IntPtr]::Zero) { throw '点了「歌曲库」之后没等到带 SongList 的那个窗口' }
  Start-Sleep -Milliseconds 800
  $库根 = $AE::FromHandle($库窗)

  # 行的 Name **不是曲名**（是容器的类名，实测 'Avalonia.Controls.Grid'），曲名在行里的 Text 上；
  # 也不能拿「行里所有文字拼起来的串」比 —— 那是 '曲名 / 没动过'，多一格状态字（verify-27 量过这两个坑）。
  # 所以挖子树找**正好等于曲名**的那个 Text。
  $期限 = (Get-Date).AddSeconds(20)
  $行 = $null
  while ((Get-Date) -lt $期限 -and $null -eq $行) {
    $行 = @(某根里 $库根 $CT::ListItem | Where-Object {
      @(某根里 $_ $CT::Text | Where-Object { $_.Current.Name -eq $曲名 }).Count -gt 0
    }) | Select-Object -First 1
    if ($null -eq $行) { Start-Sleep -Milliseconds 400 }
  }
  if ($null -eq $行) {
    $在 = (@(某根里 $库根 $CT::ListItem | ForEach-Object {
      @(某根里 $_ $CT::Text | ForEach-Object { $_.Current.Name }) }) -join ' / ')
    throw "曲库里没有「$曲名」这一行（现在有：$在）"
  }

  # 回车送给**库窗**：SendKeys 投给「当前有焦点的窗口」，别的程序盖在上面时这一下会往人家的
  # 编辑器里敲一个回车（32 号实测栽过）。模态期间主窗是死的，焦点给主窗这一下就白敲了。
  [void][P40]::Take($库窗)
  [void]$行.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  Start-Sleep -Milliseconds 400
  [void]$行.SetFocus()
  Start-Sleep -Milliseconds 400
  [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')

  # 装上了才关窗（没装上窗口不走）
  $期限 = (Get-Date).AddSeconds(30)
  while ((Get-Date) -lt $期限 -and (@(别的顶层窗) -contains $库窗)) { Start-Sleep -Milliseconds 300 }
  if (@(别的顶层窗) -contains $库窗) {
    $页脚 = (@(某根编号 $库根 'StatusText') | ForEach-Object { $_.Current.Name }) -join ' / '
    throw "曲库窗口没关掉 —— 多半是没装上（页脚：「$页脚」）"
  }
  Start-Sleep -Seconds 4
}

# 取证的曲子：**从曲库里开**。曲库里那份 `sm_mol.mid` 就是 41 号探针量过的那一首
# （探针量的是语料目录 `...\drywetmidi\...\Middle\` 里那份，78 号把它种进了 Debug 曲库：
# 18236 字节）。参数是**曲名**（= 文件名去掉 .mid），不是全路径 —— 曲库窗口只认它自己列表里的名字。
$曲名 = 'sm_mol'
开一首 $曲名
$root = $AE::FromHandle($h)
$名字格 = @(某根编号 $root 'SongNameBox')
$读到的主窗名 = if ($名字格.Count) { 取值 $名字格[0] } else { '（没有）' }
断言真 '载进来的是曲库里点的那一首' ($读到的主窗名 -eq $曲名) "点了曲库里的「$曲名」，主窗曲名框写着「$读到的主窗名」"

# 反证：上面那条在「一首都没载进来」的空窗口上也照样成立的话就没意思了 —— 轨头得真在场。
# 轨数用「折叠」按钮数（每条轨的头上都有一颗，和 verify-23/27 同一把尺子）。
$轨头数 = @(找类型 $CT::Button | Where-Object { $_.Current.Name -eq '折叠' }).Count
断言真 '反证：曲子真的载进来了（13 条轨的轨头都在场）' ($轨头数 -eq 13) "$轨头数 条轨"

# =====================================================================
"`n=== 2. 点「演奏」：主窗把手上的 sm_mol 递给演奏器 ==="
# =====================================================================
# 按 AutomationId 找，不按名字：这颗按钮屏幕上写的是「演奏」（MainWindow.axaml 里
# `x:Name="PerformerButton" Content="演奏"`）。verify-27 里头写的是『演奏器…』——
# 那是更早的一版文案，照抄过来会一个都数不到。
$开钮 = @(某根编号 $root 'PerformerButton')
断言真 '工具栏上那颗「演奏」按钮还在（入口没被顺手删掉）' ($开钮.Count -eq 1) "数到 $($开钮.Count) 颗（按 PerformerButton 找的）"
if ($开钮.Count -ne 1) { throw '没有 PerformerButton 这颗按钮，下面全都没得验' }
# 刚载完歌，这一颗就不该还是灰的。它灰着的话下面那句 Invoke 抛的是个光秃秃的
# System.Exception（哪一颗按钮灰、为什么灰，那句话里一个字都没有）—— 先自己报清楚。
$开钮可按 = $开钮[0].Current.IsEnabled
断言真 '「演奏」不再是灰的（载了歌就亮 —— 判据就是 _song is not null）' $开钮可按 "IsEnabled=$开钮可按"
if (-not $开钮可按) { throw '「演奏」是灰的 —— 主窗里没装上曲子（IsEnabled = _song is not null）' }
[void]$开钮[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

$期限 = (Get-Date).AddSeconds(20)
$演奏器 = [IntPtr]::Zero
while ((Get-Date) -lt $期限 -and $演奏器 -eq [IntPtr]::Zero) {
  foreach ($w in 别的顶层窗) {
    try { if (@(某根编号 ($AE::FromHandle($w)) 'StartButton').Count -gt 0) { $演奏器 = $w; break } } catch { }
  }
  if ($演奏器 -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 400 }
}
if ($演奏器 -eq [IntPtr]::Zero) { throw '等不到演奏器窗口（按了「演奏」之后没多出 StartButton 那个顶层窗）' }
Start-Sleep -Seconds 2
$根二 = $AE::FromHandle($演奏器)
$r二 = [P40]::RectOf($演奏器)
断言真 '演奏器窗口开出来了' ($r二[2] -gt 300) "$([P40]::Describe($演奏器)) 框=$($r二[0]),$($r二[1]) $($r二[2])x$($r二[3])"

# 「递进来的到底是哪一首」：曲目那一行（`SongValue`）跟「打开 MIDI…」那颗按钮是**同一次改版
# （47 号）一起删掉的**（探针 `probe-47-*` 那份零命中清单里两个都在），所以只能拿**提示行那两个数**坐实 ——
# 它们不只是「有没有提示行」，还是这首歌的指纹，而且是 Core 数出来的
#（`PerformerWindow.ApplySong` 里那句 `只列出单声部轨 · {轨数} 条轨里 {可弹数} 条可演奏`）。
# 78 号用产品自己的判据（`PlayableTracks.Of` / `MonophonyCheck.IsMonophonic`）量过：
# sm_mol.mid = 13 条轨 / 1927 音 / PPQ 384 / **8 条可弹**。所以这里**逐字**对，
# 不写 `-like '只列出单声部轨 · *'` —— 软写法任何一首 13 轨 8 弹的歌都过得去，等于没验身份。
$轨提示 = @(某根编号 $根二 'TrackHint')
$提示文 = if ($轨提示.Count) { $轨提示[0].Current.Name } else { '（没有提示行）' }
Write-Host "  提示行：「$提示文」"
$指纹 = '只列出单声部轨 · 13 条轨里 8 条可演奏'
断言真 '递进来的是那首 sm_mol（提示行逐字对得上 13 条轨里 8 条可演奏）' ($提示文 -eq $指纹) "「$提示文」，期望「$指纹」"

# 提示行里那个 N 是**另一个独立的证人** ——
# 下拉框那几项是界面自己列的，提示行这个是 Core 数出来的，两个数对不上就是有一边在撒谎。
$提示数 = $null
if ($提示文 -match '条轨里\s*(\d+)\s*条可演奏') { $提示数 = [int]$Matches[1] }
断言真 '提示行报出了可弹数（下面拿它跟下拉框的项数对）' ($null -ne $提示数) "解析出 N=$提示数"
断言真 '可弹数 ≥ 2（只有 1 条的话「顺序」这件事压根没得验）' ($提示数 -ge 2) "N=$提示数"

$开始 = @(某根编号 $根二 'StartButton')
$开始可按 = if ($开始.Count) { $开始[0].Current.IsEnabled } else { $false }
断言真 '「开始演奏」可按（8 条能弹的，不该是灰的）' $开始可按 "IsEnabled=$开始可按"

# =====================================================================
"`n=== 3. 下拉框：项数、序号顺序、默认选中 ==="
# =====================================================================
$框 = @(某根编号 $根二 'TrackCombo')
if ($框.Count -ne 1) { $框 = @(某根里 $根二 $CT::ComboBox) }
断言真 '找得到那个轨下拉框' ($框.Count -eq 1) "数到 $($框.Count) 个 ComboBox"
if ($框.Count -ne 1) { throw '找不到轨下拉框，下面没得验' }
$下拉 = $框[0]

Write-Host "  下拉框 $($下拉.Current.BoundingRectangle.Width)x$($下拉.Current.BoundingRectangle.Height) 支持的模式：$(支持的模式 $下拉 -join ' / ')"
Write-Host "  折叠着的时候：$(长相 $下拉)"
Write-Host "  它的 Name（UIA 惯例是「选中的那一项」）：'$(某属性 $下拉 'Name')'"
Write-Host "  它的 HelpText：'$(某属性 $下拉 'HelpText')'"

# ---------- 把列表展开，把每一项的名字按屏幕上的顺序读下来 ----------
# Avalonia 的下拉浮层可能是主窗里的一个 overlay、也可能是另一个顶层窗，两种都找一遍。
function 读展开的项 {
  $项 = @()
  foreach ($根 in @($根二) + @(别的顶层窗 | ForEach-Object { $AE::FromHandle($_) })) {
    try { $项 += @(某根里 $根 $CT::ListItem) } catch { }
  }
  # 同一个元素可能从两个根都走到（overlay 在子树上、窗口也在），去重按运行时 id
  $见过 = @{}
  $干净 = @()
  foreach ($e in $项) {
    $k = $e.GetRuntimeId() -join '.'
    if (-not $见过.ContainsKey($k)) { $见过[$k] = $true; $干净 += $e }
  }
  ,$干净
}

$项文 = @()
$展开得了 = $false
try {
  $展 = $下拉.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
  $展.Expand()
  $展开得了 = $true
  Start-Sleep -Milliseconds 1200
  $项文 = @(读展开的项 | ForEach-Object { $_.Current.Name })
  Write-Host "  展开之后读到 $($项文.Count) 项：$(if ($项文.Count) { $项文 -join ' | ' } else { '（一项都没读到）' })"
  $展.Collapse()
  Start-Sleep -Milliseconds 500
} catch {
  Write-Host "  展开不了（$($_.Exception.Message)）"
}

if ($项文.Count -eq 0) {
  # 兜底：浮层不在 UIA 树里就换个不依赖浮层的读法 —— 把焦点给下拉框，用键盘一项一项走，
  # 每走一步读一次它自己的 Name（UIA 惯例：ComboBox 的 Name 就是选中项的文字）。
  Write-Host '  改用键盘走列表（不依赖浮层在不在 UIA 树里）'
  [void][P40]::Take($演奏器)
  $下拉.SetFocus(); Start-Sleep -Milliseconds 500
  [P40]::Key(0x24)   # VK_HOME
  Start-Sleep -Milliseconds 400
  $项文 = @($下拉.Current.Name)
  for ($i = 1; $i -lt 40; $i++) {
    [P40]::Key(0x28) # VK_DOWN
    Start-Sleep -Milliseconds 250
    $现在 = $下拉.Current.Name
    if ($现在 -eq $项文[-1]) { break }
    $项文 += $现在
  }
  # 走回第一项；走不回去就把它拉回来 —— 后面要验「默认选中第一条」
  [P40]::Key(0x24)
  Start-Sleep -Milliseconds 400
  Write-Host "  键盘走下来 $($项文.Count) 项：$($项文 -join ' | ')"
}

断言真 '读到了下拉框里的项（读到 0 项的话下面全是空过）' ($项文.Count -gt 0) "数到 $($项文.Count) 项"
断言真 '下拉框项数 == 提示行报的可弹数（界面自己列的 vs Core 数出来的，两个证人要对上）' `
  ($项文.Count -eq $提示数) "项 $($项文.Count) 项，提示行说 $提示数 条"

# 每一项开头那个两位序号。取不到序号的项单独记下来，别让它把顺序断言悄悄变成「跳过」。
$序号 = @()
$没序号的 = @()
foreach ($t in $项文) {
  if ($t -match '^\s*(\d+)') { $序号 += [int]$Matches[1] } else { $没序号的 += $t }
}
断言真 '每一项都以序号开头' ($没序号的.Count -eq 0) "没有序号的项 $($没序号的.Count) 个$(if($没序号的.Count){'：' + ($没序号的 -join ' / ')})"
Write-Host "  序号：$($序号 -join ', ')"

# ---------- 断言一：序号严格递增 ----------
# 「严格」是关键的三个字：相等就说明有并列，那已经是旧打分那套 ThenBy 的痕迹了。
$递增 = $false
if ($序号.Count -ge 2) {
  $递增 = $true
  for ($i = 1; $i -lt $序号.Count; $i++) { if ($序号[$i] -le $序号[$i-1]) { $递增 = $false; break } }
}
断言真 '下拉框里的序号**严格递增**（原曲下标升序，不打分、不重排）' $递增 `
  "读到 $($序号 -join ', ')（旧实现按打分排，这里会是 12, 01, 07, 05, 04, 03, 02, 11 —— 不递增）"

# ---------- 断言二：序号是**原曲下标**，不是下拉框里的位置 ----------
# sm_mol.mid 的原曲下标是 [0,1,2,3,4,6,10,11] → 序号 [1,2,3,4,5,7,11,12]：**中间是断的**。
# 断档本身就是证据 —— 要是界面按「第几条」编号，就该是 1..N 连号。
$连号 = $false
if ($序号.Count -ge 2) {
  $连号 = $true
  for ($i = 0; $i -lt $序号.Count; $i++) { if ($序号[$i] -ne $i + 1) { $连号 = $false; break } }
}
断言真 '序号不是 1..N 连号（所以在编号的是**原曲里的位置**，不是它在下拉框里的位置）' (-not $连号) `
  "读到 $($序号 -join ', ')$(if($连号){' —— 连号了，这说明号是拿下拉框位置编的'})"
断言真 '序号都是从 1 起的正整数（没编出曲子里不存在的号）' `
  ($序号.Count -gt 0 -and @($序号 | Where-Object { $_ -lt 1 }).Count -eq 0) `
  "最小的号 $(if($序号.Count){($序号 | Measure-Object -Minimum).Minimum}else{'（没读到）'})"

# ---------- 断言三：默认选中**第一项** ----------
# 这是「我一眼就能找到主旋律，不需要你替我排」那句话在屏幕上的样子：
# 打开一首曲子，选中的该是曲子里第一条能弹的，不是旧实现挑出来的那条「最像主旋律的」。
#
# 读法得挑：**这个 ComboBox 的 UIA Name 是空的**（实测 `名=''`），拿不到「显示着的那一项」。
# 它支持的模式里有 SelectionPattern 和 ValuePattern，两条路都试：
#   · SelectionPattern.GetSelection() 返回选中那一项**本身**，文字跟列表里逐字一样 —— 首选；
#   · ValuePattern.Value 是某些 peer 才给的第二条路；
#   · 都不行就退回子树里那个可见的 Text（面板上显示的字）。
# 哪条路读到的写进日志 —— 断言红了的时候，得先分清是「选错了」还是「读不到」。
function 读选中项([object]$e) {
  try {
    $sp = $e.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern)
    $sel = @($sp.Current.GetSelection())
    if ($sel.Count -ge 1 -and $sel[0].Current.Name) { return @('SelectionPattern', $sel[0].Current.Name) }
  } catch { }
  try {
    $v = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    if ($v) { return @('ValuePattern', $v) }
  } catch { }
  $t = @(某根里 $e $CT::Text | Where-Object { -not $_.Current.IsOffscreen -and $_.Current.Name })
  if ($t.Count) { return @('子树里的 Text', $t[0].Current.Name) }
  return @('（三条路都读不到）', '')
}

$读法, $选中文 = 读选中项 $下拉
$选中号 = $null
if ($选中文 -match '^\s*(\d+)') { $选中号 = [int]$Matches[1] }
Write-Host "  现在选中：'$选中文'（用「$读法」读的，序号 $选中号）"

# 先单列一条「读得到」。不然下面两条在「读不到」时会**空过**：
# $选中号 是 $null 时 `$null -ne 12` 为真，「选中的不是 12」就自动绿了 —— 那不是通过。
断言真 '读得到「现在选中哪一项」' ($null -ne $选中号) "用「$读法」读到 '$选中文'"
断言真 '默认选中的是**第一项**（序号 == 列表里第一个号）' `
  ($序号.Count -gt 0 -and $null -ne $选中号 -and $选中号 -eq $序号[0]) `
  "选中序号 $选中号，列表第一项序号 $(if($序号.Count){$序号[0]}else{'（没读到）'})"
断言真 '选中的不是旧实现挑的那条「最像主旋律的」（sm_mol 里是下标 11，序号 12）' `
  ($null -ne $选中号 -and $选中号 -ne 12) "选中序号 $选中号（旧实现会选 12，也就是名叫 Melody 的那条）"
断言真 '选中的是曲子里**第一条**能弹的（sm_mol 里是下标 0，序号 01）' `
  ($选中号 -eq 1) "选中序号 $选中号"

# 顺带把「旧实现会选谁」这件事在原曲里坐实：那条轨现在还在列表里，只是不再被特殊对待。
$有12 = @($序号 | Where-Object { $_ -eq 12 }).Count -eq 1
断言真 '旧实现选的那条轨还在列表里（排序没了，轨没被顺手删掉）' $有12 "序号里有 12 的：$($序号 -join ', ')"

# ---------- 判别力自检：这套断言在改之前**是不是真的会红** ----------
# 「现在绿了」本身不说明什么。要是随便挑一首曲子、旧实现也给出同一个顺序，
# 上面那些断言在改之前就是绿的 —— 那就一个东西都没证明。所以把 probe-41 逐字量出来的
# **旧顺序**摆进来，拿同一套判据过一遍：它必须被判红，且第一个号必须不是 1。
# 这不是在测产品代码，是在测**这个脚本**有没有判别力。
$旧序 = @(12, 1, 7, 5, 4, 3, 2, 11)   # sm_mol.mid：旧下标 [11,0,6,4,3,2,1,10] + 1
$旧递增 = $true
for ($i = 1; $i -lt $旧序.Count; $i++) { if ($旧序[$i] -le $旧序[$i-1]) { $旧递增 = $false; break } }
断言真 '自检：旧顺序过不了「严格递增」这条（所以那条在改之前是红的）' (-not $旧递增) "旧序 $($旧序 -join ', ')"
断言真 '自检：旧顺序的第一项不是 1（所以「默认选第一项」那条在改之前也是红的）' ($旧序[0] -ne 1) "旧序第一项 $($旧序[0])"
断言真 '自检：新旧默认选中不是同一条（不然这两条断言是白验的）' `
  ($null -ne $选中号 -and $旧序[0] -ne $选中号) "旧默认 $($旧序[0])，新默认 $选中号"

# =====================================================================
"`n=== 4. 收尾 ==="
# =====================================================================
if ($fail -eq 0) { Write-Host "`n全绿。" } else { Write-Host "`n$fail 条红了。" }
# 81 号票：裁决行 —— run-all.ps1 拿它跟退出码复核，对不上就把这一条降级成红（别删）。
"==== uitest 裁决 不过=$fail"
try {
  [void][P40]::ShowWindow($演奏器, 9)
  $p二 = (别的顶层窗 | ForEach-Object { $_ })  # 演奏器是自己的顶层窗，关它不影响主窗
} catch { }
$proc.Refresh()
if (-not $proc.HasExited) {
  [void][P40]::ShowWindow($h, 9)
  [void]$proc.CloseMainWindow()
  if (-not $proc.WaitForExit(8000)) { $proc.Kill() }
}
"收了实例 PID $($proc.Id)"
exit $fail
