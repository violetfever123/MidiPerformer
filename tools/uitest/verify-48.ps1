#Requires -Version 5.1
<#
  48 号工单的实机验证：「开始演奏」那颗按钮上的小字是「任意键急停」。

  用法（**整段都在仓库锁里** —— 起 → 交互 → 关，收干净了再放锁）：
    pwsh -NoProfile -File 'C:\Users\cao17\.claude\jobs\10c1fd1e\tmp\repolock.ps1' `
         -Script '<repo>\tools\uitest\verify-48.ps1' -Tag '48 machine'

  ⚠️ 这张票的验收里有一条这台机器上**验不了**，脚本会把它明明白白打出来，不拿别的东西冒充：

    ① 「悬浮层那两句也是任意键」：悬浮层只在**演奏真跑起来之后**才出现。今天从界面起不了一场演奏 ——
       演奏器窗口拿不到曲子（`PerformerWindow.LoadSong` 全仓没有调用方，40/47 把「曲目」行删掉之后
       没人接手），所以「开始」那颗按钮永远是灰的。这一条等那处接线落地之后才验得了。

    ② 「真的按一个字母键，停了」：合成按键（`keybd_event` / `SendInput`）**恰好**是急停判定要滤掉的
       那一类（`LLKHF_INJECTED`）—— 滤掉自己发的键是这张票明确要保住的行为。所以这一下只能由人
       在真键盘上按，**任何点脚本都点不动它**（这一条值得记住：急停键永远不可能被点击脚本验完）。

  能验的那条（按钮小字）+ 一条烟测（钩子装上之后连打一串键，进程不许倒）是这个脚本的全部内容。

  退出码：0 全过 / 1 有条断言没过 / 2 环境没到位（**不是**「全过」）/ 3 前提不满足。
#>

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'

# ★ $proc 必须在 dot-source **之前**绑好：严格模式下读未绑定变量会再抛一条，
#   把调用方的收尾顶掉 —— 于是「起了实例、报错了、没收掉」，孤儿实例锁死下一个人的构建。
$proc = $null
$失败 = 0

function 断言([bool]$过, [string]$说什么, [string]$实际) {
  if ($过) { Write-Host "  [通过] $说什么" -ForegroundColor Green }
  else { Write-Host "  [不过] $说什么`n          实际：$实际" -ForegroundColor Red; $script:失败++ }
}

try { . (Join-Path $PSScriptRoot 'uitest-lib.ps1') }
catch { Write-Host "前提不满足，停手：$($_.Exception.Message)" -ForegroundColor Red; exit 3 }

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

function 找编号([object]$根, [string]$id) {
  @($根.FindAll($TS::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id))))
}
function 找类型([object]$根, [object]$类型) {
  @($根.FindAll($TS::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $类型))))
}
function 窗口上的字([object]$根) {
  @(找类型 $根 $CT::Text | ForEach-Object { $_.Current.Name } | Where-Object { $_ })
}

# 「要以管理员身份重启吗？」非收掉不可：它压着的时候主窗是禁用的，UIA 会静默地取到空。
# 60 号那颗框**两种形状都见过**（42/46 观测到它可能是画在主窗里的浮层，而不是顶层窗），
# 所以这里两条路都走：先按顶层窗找它那颗「不重启」；找不到就发一个 Esc（Esc 两种形状都吃得下）。
function 收启动模态([IntPtr]$主窗, [uint32]$脚本PID) {
  foreach ($w in [P40]::Others($脚本PID, [IntPtr]::Zero)) {
    if ($w -eq $主窗) { continue }
    $里根 = $AE::FromHandle($w)
    $钮 = 找类型 $里根 $CT::Button
    if ($钮.Count -eq 0) { continue }
    Write-Host "  顶层模态框：'$([P40]::Title($w))' 按钮 $(($钮 | ForEach-Object { $_.Current.Name }) -join ' / ')"
    foreach ($词 in '取消', '不重启', '否', '不用', '稍后') {
      $挑 = $钮 | Where-Object { $_.Current.Name -like "*$词*" } | Select-Object -First 1
      if ($挑) {
        Write-Host "  按了「$($挑.Current.Name)」"
        [void]$挑.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Seconds 2
        return $true
      }
    }
    Write-Host '  框里没有一个「不重启」意思的按钮 —— 不替你按' -ForegroundColor Red
    return $false
  }

  # 顶层窗那一路什么也没找到：可能是窗内浮层。发一个 Esc —— 要发就得先让 app 站到前台，
  # keybd_event 进的是**前台窗口**那条队列。
  if ([P40]::Take($主窗)) { [P40]::Key(0x1B) ; Start-Sleep -Milliseconds 600 }
  return $false
}

$exe = Join-Path $PSScriptRoot '..\..\MidiPerformer.App\bin\Debug\net8.0\MidiPerformer.exe'
$日志目录 = Join-Path $PSScriptRoot 'logs'

try {
  $proc = Start-Process -FilePath $exe -PassThru
  $期限 = (Get-Date).AddSeconds(30)
  do { Start-Sleep -Milliseconds 500; $proc.Refresh() }
  while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $期限)
  if ($proc.MainWindowHandle -eq 0) { Write-Host '等不到主窗口'; exit 2 }

  Start-Sleep -Seconds 3
  $h = $proc.MainWindowHandle
  $脚本PID = [uint32]$proc.Id
  Write-Host "起了实例 PID $($proc.Id)"

  $根 = $AE::FromHandle($h)
  $期限 = (Get-Date).AddSeconds(30)
  while ((Get-Date) -lt $期限 -and -not (找编号 $根 'SongNameBox').Count -and $根.Current.IsEnabled) {
    Start-Sleep -Milliseconds 400; $根 = $AE::FromHandle($h)
  }
  if (-not $根.Current.IsEnabled -or -not (找编号 $根 'SongNameBox').Count) {
    Write-Host '主窗还不可用（禁用或没有 UIA 子树）—— 多半是提权模态框还压着，先收它'
    [void](收启动模态 $h $脚本PID)
    for ($i = 0; $i -lt 12; $i++) {
      Start-Sleep -Milliseconds 500
      $根 = $AE::FromHandle($h)
      if ($根.Current.IsEnabled -and (找编号 $根 'SongNameBox').Count) { break }
      [void](收启动模态 $h $脚本PID)
    }
  }
  断言 ($根.Current.IsEnabled) '主窗可驱动（提权模态框已经收掉）' "能点=$($根.Current.IsEnabled)"

  # ---------- 开演奏器窗口 ----------
  $开钮 = 找编号 $根 'PerformerButton'
  if ($开钮.Count -ne 1) { Write-Host '主窗上找不到 PerformerButton（是不是模态框还压着？）'; exit 2 }
  try { [void]$开钮[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
  catch { Write-Host "点不动「演奏」：$($_.Exception.Message)" -ForegroundColor Red; exit 2 }

  $期限 = (Get-Date).AddSeconds(20)
  $演奏器 = [IntPtr]::Zero
  while ((Get-Date) -lt $期限 -and $演奏器 -eq [IntPtr]::Zero) {
    foreach ($w in [P40]::Others($脚本PID, $h)) {
      try { if ((找编号 ($AE::FromHandle($w)) 'StartButton').Count) { $演奏器 = $w; break } } catch { }
    }
    if ($演奏器 -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 400 }
  }
  if ($演奏器 -eq [IntPtr]::Zero) { Write-Host '等不到演奏器窗口'; exit 2 }
  Start-Sleep -Seconds 2

  $窗 = $AE::FromHandle($演奏器)
  $字 = 窗口上的字 $窗
  Write-Host ''
  Write-Host "① 演奏器窗口上那行小字（窗口 $([P40]::Rect($演奏器))）"
  Write-Host "  窗口上的字：$($字 -join ' / ')"
  断言 ($字 -contains '任意键急停') '「开始演奏」那颗按钮上的小字是「任意键急停」' ($字 -join ' / ')
  断言 (@($字 | Where-Object { $_ -like '*F6*' }).Count -eq 0) '演奏器窗口上一处 F6 的说法都没有' (@($字 | Where-Object { $_ -like '*F6*' }) -join ' / ')
  断言 (@($字 | Where-Object { $_ -like '*任意键*' }).Count -ge 1) '倒计时那行提示说的是「按任意键停」' ($字 -join ' / ')

  $张 = 存图 ([P40]::Shot($演奏器)) ([P40]::LastW) ([P40]::LastH) (Join-Path $日志目录 '48-演奏器窗口.png')
  Write-Host "  截图：$(Join-Path $日志目录 '48-演奏器窗口.png')"

  # ---------- 烟测：钩子装上之后连打一串键，进程不许倒 ----------
  # 这不是急停行为的验证（合成的键会被 LLKHF_INJECTED 滤掉，一个都不会触发急停）——
  # 它验的是另一件事：**钩子回调里没有会抛的东西**。钩子回调抛出去就是整个进程躺下，
  # 而躺下的时候按键还按着。所以打一串真实会出现的键（字母 / 数字 / 空格 / 回车 / Esc /
  # 单按的修饰键），完事之后进程和窗口都还得好端端在。
  # Win 与 Alt+Tab 故意不打：它们会动桌面（开开始菜单 / 切走前台），烟测不需要付这个代价。
  Write-Host ''
  Write-Host '② 烟测：钩子装上之后连打一串合成键，进程不许倒'
  [void][P40]::Take($演奏器)
  foreach ($vk in 0x41, 0x42, 0x30, 0x20, 0x0D, 0x1B, 0x10, 0x11, 0x12) { [P40]::Key([byte]$vk) }
  Start-Sleep -Seconds 1
  $proc.Refresh()
  断言 (-not $proc.HasExited) '打完之后进程还在' "退出码 $($proc.ExitCode)"
  断言 ([P40]::Alive($演奏器) -and [P40]::Visible($演奏器)) '演奏器窗口还在' "Alive=$([P40]::Alive($演奏器)) Visible=$([P40]::Visible($演奏器))"
  $字之后 = 窗口上的字 $窗
  断言 ($字之后 -contains '任意键急停') '打完之后窗口上的字还是那行' ($字之后 -join ' / ')

  Write-Host ''
  Write-Host '③ 本机验不了的两条（不冒充、不计入通过）' -ForegroundColor Yellow
  Write-Host '  [没验] 悬浮层那两句写的也是任意键 —— 悬浮层要演奏真跑起来才出现，'
  Write-Host '         而演奏器窗口现在拿不到曲子（LoadSong 没有调用方），从界面起不了一场演奏。'
  Write-Host '  [没验] 真的按一个字母键就停 —— 合成按键恰好是急停判定要滤掉的那一类（LLKHF_INJECTED），'
  Write-Host '         这一下只能由人在真键盘上按。'
}
finally {
  # 只关自己起的那一个，按 PID 收；绝不按进程名杀一片。
  if ($null -ne $proc) {
    try {
      $proc.Refresh()
      if (-not $proc.HasExited) {
        [void]$proc.CloseMainWindow()
        if (-not $proc.WaitForExit(8000)) { $proc.Kill() }
        Write-Host "收了实例 PID $($proc.Id)"
      }
    } catch { }
  }
}

if ($失败) { Write-Host "`n$失败 条没过" -ForegroundColor Red; exit 1 }
Write-Host "`n全过（另有 2 条本机验不了，见上）" -ForegroundColor Green
exit 0
