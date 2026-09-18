#Requires -Version 5.1
<#
.SYNOPSIS
    跑一次 MidiPerformer 的内置自检（MIDIPERFORMER_SELFTEST），统计 PASS / FAIL。

.DESCRIPTION
    自检是纯逻辑用例（事件表 EventBuilder.Build 的 9 条边界语料 + 裁剪后钉住的程序集还在不在）。
    程序走这条路时不建窗口、不注册热键、不碰按键与 MIDI 设备，所以可以在无人值守的机器上跑。

    本脚本做五件事：
      1. 定位 exe（依次找发布目录、带开关的 Release 输出、开发构建）。
      2. 设环境变量 MIDIPERFORMER_SELFTEST=<报告文件>，启动 exe。
      3. 等它退出（超时可配）。
      4. 读回报告文件，统计 PASS / FAIL。
      5. 打印结果，用同样的退出码结束。

    本脚本不写 bin 或 obj，不改仓库文件。报告只写到 %TEMP%。
    本程序不要求管理员权限（清单里没有 requireAdministrator），所以这里没有
    midikey-player 那份脚本里的 UAC 检查 —— 别把那段一起抄过来。

.PARAMETER ExePath
    exe 路径。默认按下面的候选顺序取第一个存在的。

.PARAMETER ReportPath
    自检报告的输出路径。默认 %TEMP%\midiperformer-selftest-<时间戳>.txt。
    不接受仓库内的路径（本脚本不修改仓库文件）。

.PARAMETER TimeoutSeconds
    等待自检退出的秒数。默认 120。

.EXAMPLE
    pwsh -File tools/run-selftest.ps1

.EXAMPLE
    pwsh -File tools/publish.ps1 ; pwsh -File tools/run-selftest.ps1

.NOTES
    退出码（本脚本只吐出这四个，别的一律并进来）：
      0  = 自检全部通过
      1  = 报告里有 FAIL（或自检进程返回 1）
      2  = 找不到 exe
      3  = 启动失败、超时、报告缺失，或报告路径落在仓库里
           —— 自检崩了（返回别的非零码）也走这条，原始码打在控制台上
#>
[CmdletBinding()]
param(
    [string]$ExePath = '',
    [string]$ReportPath = '',
    [int]$TimeoutSeconds = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 仓库根 = 本脚本上一级目录（tools\ 的父目录，也就是放 MidiPerformer.slnx 的那一级）
$repoRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    # 候选按"离交付物有多近"排序：
    #   1. 发布目录 —— tools/publish.ps1 的产物，单文件 + 裁剪，这才是要守的那个产物；
    #   2. 带开关的 Release 构建（publish 之前的那一步）；
    #   3. 开发构建（不裁剪）—— 只想快速过一遍用例时用。
    # 不按这个顺序查，就可能悄悄跑到一个旧的、没裁剪的 exe 上，白验一场。
    $candidates = @(
        (Join-Path $repoRoot 'MidiPerformer.App\bin\Release\net8.0\win-x64\publish\MidiPerformer.exe'),
        (Join-Path $repoRoot 'MidiPerformer.App\bin\Release\net8.0\win-x64\MidiPerformer.exe'),
        (Join-Path $repoRoot 'MidiPerformer.App\bin\Debug\net8.0\MidiPerformer.exe')
    )
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c -PathType Leaf) { $ExePath = $c; break }
    }
    if ([string]::IsNullOrWhiteSpace($ExePath)) { $ExePath = $candidates[0] }
}
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $ReportPath = Join-Path $env:TEMP "midiperformer-selftest-$stamp.txt"
}

$exe = [System.IO.Path]::GetFullPath($ExePath)
$report = [System.IO.Path]::GetFullPath($ReportPath)

# 报告不许落在仓库里：本脚本不改仓库文件。
# 比到目录分隔符为止 —— 裸前缀会把 C:\...\MidiPerformer.logs\x.txt 这种仓库外路径也判进来，
# 而需要显式 -ReportPath 的场合（CI 分目录留档、多份产物对比）恰好最容易撞上这种路径。
$repoRootFull = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd('\', '/')
$sep = [System.IO.Path]::DirectorySeparatorChar
if ($report -eq $repoRootFull -or
    $report.StartsWith($repoRootFull + $sep, [System.StringComparison]::OrdinalIgnoreCase)) {
    Write-Host "!! 报告路径落在仓库里：$report"
    Write-Host "   本脚本不修改仓库文件。请把报告写到 %TEMP% 或其它仓库外目录。"
    exit 3
}

if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    Write-Host "!! 找不到 exe：$exe"
    Write-Host "   先发布：pwsh tools/publish.ps1"
    Write-Host "   或用 -ExePath <路径> 指定别的 exe。"
    exit 2
}

Write-Host ">> exe    : $exe"
Write-Host ">> 报告   : $report"

# 只为子进程设置环境变量，跑完恢复原值。
$oldValue = $env:MIDIPERFORMER_SELFTEST
$env:MIDIPERFORMER_SELFTEST = $report
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
try {
    $proc = Start-Process -FilePath $exe -PassThru
}
catch {
    Write-Host "!! 启动失败：$($_.Exception.Message)"
    Write-Host "   常见原因：exe 被占用、被安全软件拦下、路径不可执行。"
    exit 3
}
finally {
    if ($null -eq $oldValue) {
        Remove-Item Env:\MIDIPERFORMER_SELFTEST -ErrorAction SilentlyContinue
    }
    else {
        $env:MIDIPERFORMER_SELFTEST = $oldValue
    }
}

if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
    Write-Host "!! 自检超过 $TimeoutSeconds 秒没有退出，强制结束进程 $($proc.Id)。"
    try { $proc.Kill() } catch { }
    exit 3
}
$stopwatch.Stop()
$code = $proc.ExitCode

if (-not (Test-Path -LiteralPath $report -PathType Leaf)) {
    Write-Host "!! 自检没有写出报告文件。进程退出码：$code"
    # 只放行 1（自检自己说「有用例失败」，那是文档里约定好的一条），别的非零码一律并成 3。
    # 这里原先写的是 `exit $code`，等于把子进程的码原样漏出去 —— 而本脚本对外承诺的只有
    # 0/1/2/3 四个，漏出来的 2 恰好撞上「找不到 exe」：exe 明明找到了、只是崩了，
    # CI 看到 2 会朝反方向查。非零码已经不在这条分支的正常路径上了（自检只返回 0 或 1，
    # 报告写不出去也照样返回），所以这里并成 3 不丢信息 —— 原始码就在上面那行里。
    if ($code -eq 1) { exit 1 }
    exit 3
}

$lines = @(Get-Content -LiteralPath $report -Encoding UTF8)
$passLines = @($lines | Where-Object { $_ -match '^PASS' })
$failLines = @($lines | Where-Object { $_ -match '^FAIL' })
$resultLines = @($lines | Where-Object { $_ -match '^结果：' })

Write-Host ''
Write-Host '---- 自检报告 ----'
foreach ($line in $lines) { Write-Host $line }
Write-Host '------------------'
Write-Host ''
Write-Host "耗时  : $([math]::Round($stopwatch.Elapsed.TotalSeconds, 1)) 秒"
Write-Host "PASS  : $($passLines.Count)"
Write-Host "FAIL  : $($failLines.Count)"
if ($resultLines.Count -gt 0) { Write-Host $resultLines[$resultLines.Count - 1] }

if ($failLines.Count -gt 0) {
    Write-Host ''
    Write-Host '失败明细：'
    foreach ($line in $failLines) { Write-Host "  $line" }
}

if ($code -ne 0) {
    Write-Host ''
    Write-Host "自检失败。退出码 $code。"
    # 和「报告缺失」那条同一个道理：报告读得到、进程却是非零，就是「自检失败」——
    # 并成 1，别把原始码原样漏出去（那会让「只吐 0/1/2/3」这句变成假话）。
    # 原始码在上面那行里，没丢。
    exit 1
}
if ($failLines.Count -gt 0) {
    Write-Host ''
    Write-Host "退出码是 0，但报告里有 $($failLines.Count) 条 FAIL。按失败处理。"
    exit 1
}

Write-Host ''
Write-Host '自检全部通过。退出码 0。'
exit 0
