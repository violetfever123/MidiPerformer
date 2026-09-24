#Requires -Version 5.1
<#
  48 号工单的实机验证：「开始演奏」那颗按钮上的小字是「任意键急停」，而且急停钩子真装上了。

  用法（**整段都在仓库锁里** —— 起 → 交互 → 关，收干净了再放锁）：
    pwsh -NoProfile -File 'C:\Users\cao17\.claude\jobs\10c1fd1e\tmp\repolock.ps1' `
         -Script '<repo>\tools\uitest\verify-48.ps1' -Tag '48 machine'

  验什么（都是这台机器上真的看得见的东西）：
    ① 工具栏那颗「演奏」在装上曲子之后是能点的
       （没曲子时它是灰的 —— 判据在 MainWindow.RefreshEditState，所以脚本得先装一首）
    ② 演奏器窗口上那行小字是「任意键急停」，窗口上一处 F6 的说法都没有
    ③ 钩子真装上了：状态行里没有「急停热键没装上」那句（= SetWindowsHookEx 成了）
    ④ 烟测：钩子装上之后连打一串合成键，进程和窗口都不许倒
       （合成的键会被 LLKHF_INJECTED 滤掉，一个都不会触发急停 —— 这**不是**急停行为的验证，
        验的是钩子回调里没有会抛的东西：回调抛出去就是整个进程躺下）

  ⚠️ 这张票有两条**这台机器上验不了**，脚本会把它们打出来，不拿别的东西冒充：
    ① 「悬浮层那两句也是任意键」：悬浮层只在**演奏真跑起来之后**才出现，而起一场演奏要过
       `PerformancePreflight` 三道门 —— **管理员** + 英文输入法 + 手里有一条能弹的单声部轨。
       第一道这台机器上没有任何脚本过得去：UAC 开着（`EnableLUA=1`）而同意框画在**安全桌面**上
       （`PromptOnSecureDesktop=1`），提权起 app 会弹一个**点不到**的框 —— 所以
       `InputSender.CheckElevation()` 永远是 false，预检永远回 `NotElevated`。
       （第三道随口一提：曲库那首 Carulli 4 条轨一条都弹不了，实机状态行原话是「4 条轨里一条都弹不了」。）
    ② 「真的按一个字母键，停了」：合成按键（`keybd_event` / `SendInput`）**恰好**是判定要滤掉的
       那一类（`LLKHF_INJECTED`），所以这一下只能由人在真键盘上按 —— **任何点脚本都点不动它**。
       滤掉自己发的键是这张票明确要保住的行为，所以这不是「工具不够好」，是设计如此。

  退出码：0 全过 / 1 有条断言没过 / 2 环境没到位（**不是**「全过」）/ 3 前提不满足。
#>

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'

# ★ 这几个名字要在**脚本作用域**里先存在（StrictMode 下没定义就抛）。
#   `$proc` 尤其要在 dot-source **之前**绑：uitest-lib 往调用者作用域里漏了一条
#   Set-StrictMode -Version Latest，读未绑定变量会再抛一条，把收尾顶掉 —— 于是
#   「起了实例、报错了、没收掉」，孤儿实例锁死下一个人的构建（brief 第 5 节那条漏水）。
$proc = $null
$script:proc = $null
$script:pid脚本 = 0
$script:h = [IntPtr]::Zero
$root = $null
$失败 = 0

function 断言([bool]$过, [string]$说什么, [string]$实际) {
  if ($过) { Write-Host "  [通过] $说什么" -ForegroundColor Green }
  else { Write-Host "  [不过] $说什么`n          实际：$实际" -ForegroundColor Red; $script:失败++ }
}

try { . (Join-Path $PSScriptRoot 'uitest-lib.ps1') }
catch { Write-Host "前提不满足，停手：$($_.Exception.Message)" -ForegroundColor Red; exit 3 }

# =====================================================================
# UIA 取数与点击：照抄 verify-46（同一套，67 号并库时没并进来的那几件）
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
function 窗口上的字($根) { @(找类型 $根 $CT::Text | ForEach-Object { $_.Current.Name } | Where-Object { $_ }) }

# ⚠️ 数窗口个数**必须写 `@(别窗).Count`**，`@` 一个都不能省：单元素数组从函数返回值里会被
#    拆包成标量，标量上没有 Count 属性 —— 严格模式底下那是**脚本半路死掉**那种失败。
#    （48 号自己第一趟就是这么断的，同一天在 NUnit 那边踩的是同一件事：惰性序列没有 Count 属性。）
function 别窗 { @([P40]::Others($pid脚本, $h)) }
function 曲库窗 {
  $别 = @(别窗 | Where-Object { [P40]::Title($_) -eq '歌曲库' })
  if ($别.Count -eq 0) { return $null }
  return $别[0]
}
# 演奏器窗口 = app 的另一个顶层窗，里面有一顆 StartButton。
function 演奏器窗 {
  foreach ($w in @(别窗)) {
    try { if ($null -ne (按id (取根 $w) 'StartButton')) { return $w } } catch { }
  }
  return $null
}
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
function 某行($根, [string]$曲名) {
  foreach ($it in @(找类型 $根 $CT::ListItem)) {
    $r = $it.Current.BoundingRectangle
    if ([double]::IsNaN($r.X) -or $r.Width -le 0 -or $r.Height -le 0) { continue }
    if (@(找类型 $it $CT::Text | Where-Object { $_.Current.Name -eq $曲名 }).Count -gt 0) { return $it }
  }
  return $null
}
function 点元素($e, [IntPtr]$谁的窗, [string]$谁) {
  $r = $e.Current.BoundingRectangle
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
  $r = $e.Current.BoundingRectangle
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

# 启动那颗「要以管理员身份重启吗？」（60 号票）—— 不点掉主窗就是死的，连「演奏」都点不动。
# 它是**顶层窗**（48 号实测：标题原样就是 '要以管理员身份重启吗？'，里面有「以管理员身份重启」/「取消」）。
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

$日志 = Join-Path (Join-Path $PSScriptRoot 'shots') '48'
New-Item -ItemType Directory -Force -Path $日志 | Out-Null

try {
  $h = 起窗口   # lib 里那个：起 → 摆满工作区 → 拽到前台，并设好 $script:proc / $script:pid脚本 / $script:h
  关提权框
  $root = 取根 $h

  # ---------- 0. 先装一首：没曲子时「演奏」是灰的，点不动 ----------
  Write-Host "`n=== 0. 从曲库装一首 Carulli（工具栏的「演奏」要有曲子才亮）==="
  点后等 { @(按种类 '歌曲库')[0] } $h '歌曲库' { $null -ne (曲库窗) } | Out-Null
  $dw = 曲库窗
  if (-not $dw) { Write-Host '曲库窗没开出来'; exit 2 }
  $装上了 = 双击后等 {
    $w = 曲库窗
    if (-not $w) { return $null }          # 窗口没了就是「这一行没得点」
    某行 (曲库根) 'Carulli_Duetto_No2_Op4'
  } $dw 'Carulli 那一行' { @(别窗).Count -eq 0 } 30 2 6
  断言 $装上了 '双击曲库里那首 Carulli，把它装上了（曲库窗自己关掉）' "别窗 $(@(别窗).Count) 个"
  if (-not $装上了) {
    点后等 { 按id (曲库根) 'CloseButton' } (曲库窗) '关闭' { @(别窗).Count -eq 0 } | Out-Null
  }
  Start-Sleep -Seconds 2
  $root = 取根 $h        # 装曲子会把主窗控件整套重建，开场抓的那个根从这一刻起作废

  # ---------- 1. 开演奏器窗口 ----------
  Write-Host "`n=== 1. 点工具栏的「演奏」，把演奏器窗口开出来 ==="
  $开钮 = 按id $root 'PerformerButton'
  if ($null -eq $开钮) { Write-Host '主窗上找不到 PerformerButton'; exit 2 }
  Write-Host "  PerformerButton：能点=$($开钮.Current.IsEnabled) 位置=$($开钮.Current.BoundingRectangle)"
  断言 ($开钮.Current.IsEnabled) '装上曲子之后「演奏」是能点的' "能点=$($开钮.Current.IsEnabled)"
  if (-not $开钮.Current.IsEnabled) { exit 2 }

  $开了 = 点后等 { 按id $root 'PerformerButton' } $h '演奏' { $null -ne (演奏器窗) } 25 3
  $演奏器 = 演奏器窗
  if (-not $开了 -or $null -eq $演奏器) {
    Write-Host '演奏器窗口没开出来。app 现在的顶层窗：'
    foreach ($w in @(别窗)) { Write-Host "    $([P40]::Title($w)) $([P40]::Rect($w))" }
    exit 2
  }
  Start-Sleep -Seconds 2
  $窗 = 取根 $演奏器
  Write-Host "  演奏器窗口：'$([P40]::Title($演奏器))' $([P40]::Rect($演奏器))"

  # ---------- 2. 那三处提示的字 ----------
  Write-Host "`n=== 2. 演奏器窗口上的字 ==="
  $字 = @(窗口上的字 $窗)
  Write-Host "  窗口上的字：$($字 -join ' / ')"
  断言 ($字 -contains '任意键急停') '「开始演奏」那颗按钮上的小字是「任意键急停」' ($字 -join ' / ')
  $留着的F6 = @($字 | Where-Object { $_ -like '*F6*' })
  断言 ($留着的F6.Count -eq 0) '演奏器窗口上一处 F6 的说法都没有' ($留着的F6 -join ' / ')
  $任意键的 = @($字 | Where-Object { $_ -like '*任意键*' })
  断言 ($任意键的.Count -ge 1) '倒计时那行提示说的是「按任意键停」' ($字 -join ' / ')

  # 钩子真装上了没有：装不上时状态行会缀一句「急停热键没装上，只能用这个窗口上的按钮」。
  断言 (@($字 | Where-Object { $_ -like '*没装上*' }).Count -eq 0) `
    '急停钩子装上了（状态行里没有「没装上」那句 = SetWindowsHookEx 成了）' ($字 -join ' / ')

  $张 = 存图 ([P40]::Shot($演奏器)) ([P40]::LastW) ([P40]::LastH) (Join-Path $日志 '演奏器窗口.png')
  Write-Host "  截图：$(Join-Path $日志 '演奏器窗口.png')"

  # ---------- 3. 烟测：钩子回调不许把进程带倒 ----------
  Write-Host "`n=== 3. 烟测：钩子装上之后连打一串合成键，进程不许倒 ==="
  [void][P40]::Take($演奏器)
  foreach ($vk in 0x41, 0x42, 0x30, 0x20, 0x0D, 0x1B, 0x10, 0x11, 0x12) { [P40]::Key([byte]$vk) }
  Start-Sleep -Seconds 1
  $script:proc.Refresh()
  断言 (-not $script:proc.HasExited) '打完之后进程还在' "退出码 $($script:proc.ExitCode)"
  断言 ([P40]::Alive($演奏器) -and [P40]::Visible($演奏器)) `
    '演奏器窗口还在' "Alive=$([P40]::Alive($演奏器)) Visible=$([P40]::Visible($演奏器))"
  $字之后 = @(窗口上的字 (取根 $演奏器))
  断言 ($字之后 -contains '任意键急停') '打完之后窗口上的字还是那行' ($字之后 -join ' / ')

  Write-Host "`n=== 4. 这台机器上验不了的两条（不冒充、不计入通过）===" -ForegroundColor Yellow
  Write-Host '  [没验] 悬浮层那两句写的也是任意键 —— 悬浮层要演奏真跑起来才出现，而起一场演奏要过'
  Write-Host '         PerformancePreflight 三道门：管理员 + 英文输入法 + 一条能弹的单声部轨。'
  Write-Host '         第一道这台机器上没有任何脚本过得去：UAC 开着，同意框画在**安全桌面**上'
  Write-Host '         （EnableLUA=1 / PromptOnSecureDesktop=1），提权起 app 会弹一个点不到的框。'
  Write-Host '  [没验] 真的按一个字母键就停 —— 合成按键恰好是判定要滤掉的那一类（LLKHF_INJECTED），'
  Write-Host '         这一下只能由人在真键盘上按。'
  Write-Host '  （这一趟顺便看到的：那首 Carulli 4 条轨一条都弹不了，状态行原话「4 条轨里一条都弹不了」。）'
}
finally {
  # 只关自己起的那一个（lib 的 收窗口 收的是 $script:proc），绝不按进程名杀一片。
  收窗口
  if ($null -ne $script:proc) { Write-Host "收了实例 PID $($script:proc.Id)" }
}

if ($失败) { Write-Host "`n$失败 条没过" -ForegroundColor Red } else { Write-Host "`n全过（另有 2 条本机验不了，见上）" -ForegroundColor Green }
# 81 号票：裁决行（run-all.ps1 拿它跟退出码复核，对不上就把这一条降级成红）。
# 这一条的红数变量叫 `$失败`，不是 `$fail` —— 照它自己的写。
"==== uitest 裁决 不过=$失败"
exit $失败
