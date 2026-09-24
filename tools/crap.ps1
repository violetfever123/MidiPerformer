#Requires -Version 5.1
<#
.SYNOPSIS
    跑一遍 CRAP 闸门：先采覆盖率，再从报告算每个方法的 CRAP，超过 30 就红。

.DESCRIPTION
    CRAP(m) = comp(m)² × (1 − cov(m)/100)³ + comp(m)

    闸门本身是测试工程里的一条 NUnit 测试（Category=Crap，见
    MidiPerformer.Tests\Tools\CrapGateTests.cs）—— 因为这个仓库的闸门就是 dotnet test。
    本脚本只管「先有报告、再跑闸门」这个先后：

      1. dotnet test --collect:"XPlat Code Coverage" —— 全量跑一遍，采一份覆盖率报告。
      2. 报告真的生成了吗？没生成就退 2（闸门没跑 ≠ 通过）。
      3. 把报告路径塞进环境变量 MIDIPERFORMER_CRAP_REPORT，
         再 dotnet test --filter Category=Crap —— 跑闸门。
      4. 汇总，按下面的退出码表结束。

    报告落在 %TEMP% 下（--results-directory 指过去的），闸门测试从那个环境变量拿路径。
    本脚本不往仓库里写东西：-ReportPath 落在仓库里就直接退 3。

    范围只算功能层 MidiPerformer.Core（跟覆盖率采集、跟 63/65 同一范围）。
    App 的 Avalonia 视图与 Adapters 的 Gateway 明确不测 —— 理由写在
    CoberturaCrapReader.ScopePrefix 的注释里。

    门槛是「每个方法 ≤ 30」，但**它不是全局分数闸门**：闸门下沉到工单，每张工单自己
    负责杀掉它改动的那些。这里提供的是口径与工具。

.PARAMETER ReportPath
    本脚本自己那份跑批记录（两趟 dotnet test 的完整输出）的落点。
    默认 %TEMP%\midiperformer-crap-<时间戳>.txt。不接受仓库内的路径。

.PARAMETER Configuration
    构建配置。默认 Debug。

.EXAMPLE
    pwsh -File tools/crap.ps1

.EXAMPLE
    # 构建与测试必须包在仓库锁里跑（多 agent 共用工作树）
    pwsh -NoProfile -File <repolock.ps1> -Cmd 'pwsh -NoProfile -File tools/crap.ps1' -Tag '64 crap'

.NOTES
    退出码（本脚本只吐出这四个，别的一律并进来）：
      0 = 闸门通过：功能层每个方法的 CRAP 都 ≤ 30
      1 = 闸门红了：有方法超线（哪个方法、多少分、在文件的哪一行，都在上面的输出里）
      2 = 闸门没跑 —— 这一条**不是通过**：
            覆盖率报告没生成、闸门一条都没执行（被跳过、或筛选器一条都没匹配上）、
            或者跑批记录读不懂
      3 = 用法错误：报告路径落在仓库里

    退出码说的是**闸门**的结论。覆盖率那一趟自己有红的测试时，脚本会照跑不误
    （报告已经采到了，闸门的数还是算得出来的），只在输出里报一行红 —— 别把那行当成没看见。
    但那一趟**没构建起来**时（多 agent 共用工作树，别人的半成品会挂在编译上），
    报告根本不会生成，落到的就是 2：闸门没跑。
#>
[CmdletBinding()]
param(
    [string]$ReportPath = '',
    [string]$Configuration = 'Debug'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 仓库根 = 本脚本上一级目录（tools\ 的父目录，也就是放 MidiPerformer.slnx 的那一级）
$repoRoot = Split-Path -Parent $PSScriptRoot
$slnx = Join-Path $repoRoot 'MidiPerformer.slnx'

# 闸门的范围与筛选器。跟 CrapGateTests 上的 [Category] 是同一个词。
$筛选器 = 'Category=Crap'
$报告变量 = 'MIDIPERFORMER_CRAP_REPORT'

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $ReportPath = Join-Path $env:TEMP "midiperformer-crap-$stamp.txt"
}
$report = [System.IO.Path]::GetFullPath($ReportPath)

# 报告不许落在仓库里：本脚本不改仓库文件。比到目录分隔符为止 ——
# 裸前缀会把 C:\...\MidiPerformer.logs\x.txt 这种仓库外路径也判进来。
$repoRootFull = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd('\', '/')
$sep = [System.IO.Path]::DirectorySeparatorChar
if ($report -eq $repoRootFull -or
    $report.StartsWith($repoRootFull + $sep, [System.StringComparison]::OrdinalIgnoreCase)) {
    Write-Host "!! 报告路径落在仓库里：$report"
    Write-Host "   本脚本不修改仓库文件。请把报告写到 %TEMP% 或其它仓库外目录。"
    exit 3
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host '!! 找不到 dotnet。'
    exit 2
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$resultsDir = Join-Path $env:TEMP "midiperformer-crap-$stamp"
New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null

$记录 = [System.Text.StringBuilder]::new()
function 记录行([string]$Text) {
    Write-Host $Text
    [void]$记录.AppendLine($Text)
}

记录行 ">> 仓库   : $repoRoot"
记录行 ">> 报告   : $report"
记录行 ">> 覆盖率 : $resultsDir"
记录行 ""

# dotnet 把进度写 stdout、把错误写 stderr；这里只认它的退出码，所以临时放松
# $ErrorActionPreference，免得 stderr 的每一行都被当成终止错误。
function 跑一趟([string[]]$参数) {
    $ErrorActionPreference = 'Continue'
    $文本 = (& dotnet @参数 2>&1 | Out-String)
    $码 = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    return @{ 文本 = $文本; 码 = $码 }
}

$旧值 = [System.Environment]::GetEnvironmentVariable($报告变量)
$第二趟 = $null
try {
    # ---------- 第一趟：采覆盖率 ----------
    # 环境变量先指到结果目录（报告还没生成）—— 报告存在之前的闸门是「没跑」，
    # 不能让它在这一趟里顺手去读别的什么报告。
    [System.Environment]::SetEnvironmentVariable($报告变量, $resultsDir)
    记录行 "========== 第一趟：dotnet test --collect（全量，只为覆盖率） =========="
    $第一趟 = 跑一趟 @('test', $slnx, '-c', $Configuration, '--nologo',
        '--collect:XPlat Code Coverage', '--results-directory', $resultsDir)
    记录行 $第一趟.文本

    if ($第一趟.码 -ne 0) {
        记录行 ''
        记录行 "!! 覆盖率那一趟没跑干净（dotnet test 退出码 $($第一趟.码)）。"
        记录行 '   两种可能，看上面的输出分辨：有红的测试 —— 或者**根本没构建起来**'
        记录行 '   （比如别人未提交的 XAML/C# 编译不过；那种情况下不会有覆盖率报告，下面会按「没跑」收场）。'
        记录行 '   无论是哪种，都不是闸门的结论。这一趟照样采了覆盖率，下面照样跑闸门 —— 但那几条红的别忘掉，'
        记录行 '   它们可能是别人的、也可能是你的；先把测试跑绿，再回来看这次闸门的结论算不算数。'
    }

    $覆盖率报告 = Get-ChildItem -LiteralPath $resultsDir -Recurse -Filter 'coverage.cobertura.xml' `
        -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($null -eq $覆盖率报告) {
        记录行 ''
        记录行 "!! 没生成覆盖率报告（$resultsDir 下找不到 coverage.cobertura.xml）。"
        记录行 '   闸门没跑，不是通过。'
        exit 2
    }
    记录行 ">> 覆盖率报告：$($覆盖率报告.FullName)"

    # ---------- 第二趟：跑闸门 ----------
    [System.Environment]::SetEnvironmentVariable($报告变量, $覆盖率报告.FullName)
    记录行 ''
    记录行 "========== 第二趟：dotnet test --filter $筛选器（闸门） =========="
    # 顺带要一份 TRX：下面算数用它的，不用控制台输出。
    $第二趟 = 跑一趟 @('test', $slnx, '-c', $Configuration, '--nologo', '--no-build',
        '--filter', $筛选器, '--logger', 'trx;LogFileName=gate.trx', '--results-directory', $resultsDir)
    记录行 $第二趟.文本
}
finally {
    [System.Environment]::SetEnvironmentVariable($报告变量, $旧值)
}

if ($null -eq $第二趟) {
    记录行 '!! 闸门那一趟没跑起来。闸门没跑，不是通过。'
    exit 2
}

# 数数从 TRX 里读，**不读控制台**：dotnet test 的汇总行是本地化的
# （本机上是「失败!  - 失败: 0，通过: 13，已跳过: 1，总计: 14」），拿它算数太脆；
# TRX 的元素名与属性名不会本地化。
# 读不懂就当「没跑」—— 一条都没匹配上的筛选器也走这条路，而那种情形如果算成
# 「闸门红了」，会让人去改本来没问题的代码。
$trx路径 = Join-Path $resultsDir 'gate.trx'
$总数 = 0; $执行数 = 0; $通过数 = 0; $失败数 = 0; $错误数 = 0
if (-not (Test-Path -LiteralPath $trx路径 -PathType Leaf)) {
    记录行 ''
    记录行 "!! 闸门那一趟没留下 TRX 记录（$trx路径）——多半一条测试都没匹配上。"
    记录行 "   闸门没跑，不是通过。检查筛选器 $筛选器 跟 CrapGateTests 上的 [Category] 对不对得上。"
    exit 2
}
try {
    [xml]$trx = Get-Content -LiteralPath $trx路径 -Raw
    $计数器 = $trx.TestRun.ResultSummary.Counters
    $总数 = [int]$计数器.GetAttribute('total')
    $执行数 = [int]$计数器.GetAttribute('executed')
    $通过数 = [int]$计数器.GetAttribute('passed')
    $失败数 = [int]$计数器.GetAttribute('failed')
    $错误数 = [int]$计数器.GetAttribute('error')
}
catch {
    记录行 ''
    记录行 "!! 读不懂 TRX 记录（$trx路径）：$($_.Exception.Message)"
    记录行 '   闸门没跑，不是通过。'
    exit 2
}

记录行 ''
记录行 ">> 闸门：总计 $总数 条，执行 $执行数，通过 $通过数，失败 $失败数"

$校验 = 0
if ($总数 -le 0 -or $执行数 -le 0 -or $执行数 -lt $总数 -or $错误数 -gt 0) {
    记录行 ''
    记录行 "!! 闸门没跑：该跑的没跑成（总计 $总数 条、执行 $执行数 条、错误 $错误数 条）。"
    记录行 '   被跳过就是没跑 —— 不能当成通过。'
    $校验 = 2
}
elseif ($失败数 -gt 0) {
    记录行 ''
    记录行 '闸门红了：有方法的 CRAP 超过 30（上面那份清单里点名了是哪个、多少分）。'
    $校验 = 1
}
else {
    记录行 ''
    记录行 '闸门通过：功能层每个方法的 CRAP 都 ≤ 30。'
    $校验 = 0
}

Set-Content -LiteralPath $report -Value $记录.ToString() -Encoding UTF8
Write-Host ">> 跑批记录：$report"

exit $校验
