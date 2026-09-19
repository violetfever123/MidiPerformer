#Requires -Version 5.1
<#
.SYNOPSIS
    发布 MidiPerformer：win-x64 自包含单文件 exe（PublishTrimmed 开）。

.DESCRIPTION
    发布形态写在 MidiPerformer.App.csproj 里，但收在显式开关 MidiPerformerPublish 后面 ——
    无条件写 RuntimeIdentifier 会让普通 build 也变成 RID 专属的（产物挪进 win-x64\ 子目录、
    一次 build 就是 96MB / 223 个文件），引用它的测试工程跟着遭殃。原因与实测数字见 csproj 注释。
    本脚本就是那个开关的调用者：直接敲 `dotnet publish` 得到的是非单文件、不裁剪的开发形态，不是交付物。

    本脚本做三件事：
      1. dotnet publish -c Release -p:MidiPerformerPublish=true。
      2. 核对发布目录里**只有一个 exe**（没有散落的 dll / pdb / .so）。
      3. 打印 exe 路径与大小。

    「只有一个 exe」这句不算运行时数据：发布目录下的 songs\ 是 exe 自己写的曲库
    （从发布目录起过一次 app 就会长出来，见 .gitignore），不算夹带 —— 检查跳过它，也不报错。

    本脚本会写 MidiPerformer.App\bin 与 obj（发布就是产出物），但**不改任何源码或受版本控制的文件**。
    发布完想验产物就跑 tools\run-selftest.ps1 —— 那是"裁剪后的 exe 上跑通自检"的那条路。

.PARAMETER Configuration
    构建配置。默认 Release。

.PARAMETER NoVerify
    跳过"发布目录里只有一个 exe"的检查。

.EXAMPLE
    pwsh -File tools/publish.ps1

.NOTES
    退出码：
      0 = 发布成功，且发布目录里只有一个 exe
      1 = dotnet publish 失败
      2 = 发布目录里不止一个 exe（或夹带了 dll / pdb / .so；songs\ 运行时曲库不计）
      3 = 找不到发布目录
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$NoVerify
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 仓库根 = 本脚本上一级目录（tools\ 的父目录，也就是放 MidiPerformer.slnx 的那一级）
$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot 'MidiPerformer.App\MidiPerformer.App.csproj'

Write-Host ">> 发布 : $appProject（$Configuration，win-x64 自包含单文件 + 裁剪）"

& dotnet publish $appProject -c $Configuration -p:MidiPerformerPublish=true
if ($LASTEXITCODE -ne 0) {
    Write-Host "!! dotnet publish 失败，退出码 $LASTEXITCODE。"
    exit 1
}

# 发布目录：开关打开后 RuntimeIdentifier=win-x64，所以落在 net8.0\win-x64\publish\ 下
$publishDir = Join-Path $repoRoot "MidiPerformer.App\bin\$Configuration\net8.0\win-x64\publish"
if (-not (Test-Path -LiteralPath $publishDir -PathType Container)) {
    Write-Host "!! 找不到发布目录：$publishDir"
    exit 3
}

$exe = Join-Path $publishDir 'MidiPerformer.exe'
if ($NoVerify) {
    if (Test-Path -LiteralPath $exe -PathType Leaf) { Write-Host ">> 产物 : $exe" }
    exit 0
}

# 要拦的是散落的 dll / pdb / .so，**不是这个 exe 自己写出来的运行时数据**。
# 曲库就住在发布目录下（App.axaml.cs 那句 Path.Combine(AppContext.BaseDirectory, "songs")，
# .gitignore 里也是按运行时数据收的），从发布目录起过一次 app 它就落在那儿了 ——
# 这一条 -Recurse 会把它连同里面的 .mproj 一起数进来，于是检查变成「跑过一次就必炸」：
# 2026-09-19 实测就是这么炸的（5 个 .mproj，退出码 2，而 exe 本身是好的）。
# 排除按**目录前缀**比，不走文件名白名单 —— 将来再多一个运行时目录也不会重演这一出。
$runtimeDataDir = Join-Path $publishDir 'songs'
$sep = [System.IO.Path]::DirectorySeparatorChar
$files = @(Get-ChildItem -LiteralPath $publishDir -File -Recurse |
    Where-Object { -not $_.FullName.StartsWith("$runtimeDataDir$sep", [System.StringComparison]::OrdinalIgnoreCase) })
if ($files.Count -ne 1 -or $files[0].Extension -ne '.exe') {
    Write-Host "!! 发布目录里不止一个文件（$($files.Count) 个）："
    # 打相对路径不打裸文件名：-Recurse 捞上来的东西可能藏在子目录里，
    # 只打 Name 的话看着跟顶层散落的一模一样（上面那次误判就是这么看出来的）。
    foreach ($f in $files) { Write-Host "     $($f.FullName.Substring($publishDir.Length + 1))" }
    Write-Host '   单文件发布的目标是「目录里只有一个 exe」。'
    Write-Host "   常见原因：DebugType 不是 none（多出 .pdb）、"
    Write-Host "            DryWetMidi 的原生库没剔掉（多出 .so / .dylib，见 csproj 的 RemoveForeignNativeLibsFromPublish）。"
    exit 2
}

$size = [math]::Round($files[0].Length / 1MB, 1)
Write-Host ''
Write-Host ">> 产物 : $exe（$size MB，目录里只有这一个 exe）"
if (Test-Path -LiteralPath $runtimeDataDir -PathType Container) {
    $songCount = @(Get-ChildItem -LiteralPath $runtimeDataDir -File -Recurse).Count
    Write-Host "   旁注：songs\ 下有 $songCount 个曲库文件（运行时数据，未计入上面那句，也没动过）"
}
Write-Host '   验产物：pwsh -File tools/run-selftest.ps1'
exit 0
