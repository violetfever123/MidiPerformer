#Requires -Version 5.1
<#
.SYNOPSIS
    一条命令跑完一个目录里全部 verify-*.ps1：一张总表 + 一个总退出码。

.DESCRIPTION
    这个仓库的「跑一遍全套」从前等于人肉照目录念文件名。这个 runner 是那个入口。

    1. 扫 <目录>\verify-*.ps1（默认就是本脚本所在的目录），逐个用子进程跑。
       **本脚本自己也是这个目录里的一员，但名字不匹配 verify-*，所以扫不到自己。**
    2. 每条脚本的 log 落到 <目录>\logs\<脚本名>.log。**脚本自己仍然不写日志** ——
       重定向由 runner 这一层做（保持 `.scratch/` 那批 log 的现有做法）。
    3. **一条红了不中断**：全跑完再报，总表里「有几条红」和「哪几条红」都看得见，
       总退出码非零。
    4. 跑前跑后各查一次实例：有 MidiPerformer 在跑就**抛**（退出码 2），**不代用户关**；
       跑完还剩几个进总表（那是必须看见的信息，不是内部细节）。

    ---- 五种状态：绿 / 红 / 没跑完 / 超时 / 不判据（**互相分得开，谁都不许冒充绿**）----

    一条脚本可能**跑到一半断掉**（抛异常、被清场问题卡住、disk full）—— 那种时候它留下的 log
    **看着像一段正常的中间输出**，而后面那些断言**一条都没跑**。「退出码非零」和「压根没跑完」
    必须分得开，判据是两行哨兵 + 一条空输出规则（下面逐条说）。历史教训：`find-note.ps1`
    坏了 4 天没人知道，它不响：返回 0 条之后脚本继续往下跑，产出**看起来像结论的垃圾**。

      · **绿**：退出码 0，**说了话**，而且**它自己的裁决站得住**（见下面「态绿那三道自检」）。
      · **红**：退出码非零，且它说了话。
      · **没跑完**：① 一个字节的输出都没有（**退出码是什么都不算数** —— 不响的门不是绿门）；
                  ② log 里没有「止」那行哨兵（脚本没跑到结尾：抛了 / 被杀 / 断电）；
                  ③ log 里没有 runner 补的「判定」那行（这一趟没走完，或被别人截断了）。
      · **超时**：超过 -超时秒 还没退出 ⇒ 按 PID 杀掉那条子进程 + 记成红 + 写明是超时。
        **没有超时的话 runner 会永远挂着**，而「永远挂着」既不是绿也不是红，等于整套 harness 失效。
      · **不判据**：退出码 0、说了话，但它**明说这一趟没有任何判据**（只体检 / 只拍照）。
        它**不许占绿灯**（绿灯的意思是「有一批判据真的通过了」），也不判红（它没验的东西
        谈不上过不过）。总表里单独一格，`说明` 里写它自己给的理由。**它不是「绿」的马甲**。

    哨兵三行（都带 `==== run-all ` 前缀，grep 得到），写进每一条 log：
      `==== run-all 开始 …`  —— 跑之前写（由这一层壳写，写的时候会**截断**旧 log）
      `==== run-all 止 退出码=N` / `==== run-all 断 抛了：…` —— 脚本进程结束之后写
      `==== run-all 判定 状态=… 退出码=… 耗时=…s` —— runner 收完这一条之后写

    ---- 脚本自己的两行记号（不带 `run-all` 前缀，是**脚本**打的）----

      `==== uitest 裁决 不过=N`      —— 这一趟有几条判据没过（全过就是 `不过=0`）。
      `==== uitest 不判据 理由=…`     —— 这一条脚本**没有判据**（只体检 / 只拍照）。

    🔴 **态绿那三道自检**（本票加的防线；防的是「新脚本忘了 `exit`，于是红了也报绿」）：
      退出码 0 **不再**等于绿。这一支里再拿**脚本自己的输出**复核三道：
        1. 有 `裁决` 记号、且 `不过=0` ⇒ 绿；
        2. 有 `裁决` 记号、但 `不过>0` ⇒ **降级成红**，`说明` 里写上「脚本自己说不过 N 条，
           退出码却是 0 —— 裁决没传出去（末尾缺 `exit`）」。这一条同时兜住了「退出码被截成 0」
           （`exit 256` 那种）—— 记号里的 N 是文本，不会被截。
        3. 没有 `裁决` 记号 ⇒ 查它**源码里有没有 `exit`**：
             · 有 ⇒ 绿（老脚本还没补记号，但它至少能把红传出去）；
             · 没有 ⇒ **降级成红**：退出码 0、既没有裁决行也没有 `exit`，它红没红自己传不出来。
        有 `不判据` 记号 ⇒ 记成**不判据**（不算绿，也不判红）。
      🔴 记号**不要求**在：老脚本（和冒烟测试里那些假脚本）没补记号也照跑，只是那三道里
         第 3 道会兜住它。**不许**改成「正则猜中文措辞」（`条不过` / `全过` 那种）：
         各脚本的措辞不止一种，猜错的方向不是漏报就是假红。
      ⚠️ 这三道**只在态绿那一支**里跑：超时、没跑完、红**各有各的判法，一个字都不许动**
         （尤其不许把「超时」和「没跑完」合并 —— 那正是下面 `:786` 那一带专门分开的两个状态）。

    后两个是分开的两句话，因为它们回答两个不同的问题：「脚本自己跑完了吗」和
    「runner 收完这一条了吗」。**log 里没有「判定」那行 = 这一条没跑完**（runner 自己被杀、
    机器断电、磁盘满）—— `-只查日志` 就是拿这条规则去复核已经存在的 log（`C` 那条验收
    「把 log 手动截断，runner 分得出来」走的就是它）。

    ---- 「改之前必须是红的」（本工具的抬头里就有它，因为它是这一整套里唯一一条新增的行为要求）----

    一条**改前改后都绿**的断言证明不了任何东西 —— 它可能只是碰巧成立，也可能根本没在看那个东西。
    所以每张工单的 verify 脚本：**改动前跑一遍必须红，改动后跑一遍必须绿，两份 log 都留**。
    runner 能做的那一半是**让它看得见**：总表里有一条「上一趟」列，取自上一次 `logs\run-all.json`
    记的状态；同一条脚本这次绿、上一趟红，就是「改之前必须是红的」在报告上留下的痕迹。
    （`-只查日志` 是另一半：它判的是「这一趟到底跑完了没有」，而不是「退出码是不是 0」。）

    ---- 关门判据（`-只查引用` 单跑这一节；这一票是这一轮的收口票）----

    1. **`.scratch` 语法级引用**：搬进来的脚本不许再依赖 `.scratch`（新克隆里没有那个目录）。
       判据只认四类**语法形态**（每一条都是「这里真在解析一个路径」的形状）：
         · `param(… = '…/…')` —— 赋值给一个**以 `.scratch` 开头**的字面量（`'..\..\.scratch\x'` 也算）
         · `Join-Path` 的参数
         · dot-source 的路径（行首那个 `. `）
         · `& pwsh -File` 的参数
       **两种注释都不抓**：`#` 开头那一段、以及块注释（`<#` 到它成对的结束标记）整段
       —— 抬头的 `.EXAMPLE` 里写一条 `.scratch` 的示例命令，那是文档不是债。提示字符串
       （`throw '还没载入曲子 —— 先跑 .scratch/load-song.ps1'`）也不是路径解析，同样不抓。
       ⚠️ 判据写粗一点它自己就会制造一片假红，那比不判更坏 —— 这一条实测踩过**两颗**：
          · 裸 `grep '\.scratch'`：连注释和提示文案一起抓（存量几十处，一处都不用改）；
          · 第 4 类写成 `= "…任意…\.scratch…"`：连**本脚本自己**那两句「语法级 `.scratch`
            引用 N 处」的提示文案都判红。所以第 4 类必须卡「字面量以 `.scratch` 开头」。
       一条恒红的判据等于没有判据。
    2. **闭包完整**：每个被 dot-source / 被 `& pwsh -File` 起的子脚本都在同一个目录里；
       `uitest-lib.ps1` / `find-note.ps1` / `probe-41-dlg.ps1` / `probe-41\` 这几个点名的成员在位。
       漏搬一个依赖的症状是「找不到文件，行为变成什么都不做」，**不报错**。
    3. **像素 API 只报不判**：`PrintWindow` / `LockBits` / `GetPixel` / `CopyFromScreen`
       在哪些脚本里出现过（硬规矩：像素读回只能进 probe，不能进 verify）。存量那几处不在本票
       范围里，所以这一条**只列清单、不判红** —— 一条恒红的判据等于没有判据。

    ---- 仓库锁 ----

    **本脚本自己不去抢仓库锁。** 它跑的是那批要起窗口的脚本，天生是「长时间持有」的形状，
    而一次持锁不该超过 ~20 分钟：要整跑一趟，请**在锁里**分段跑（`-只跑 'verify-2*'` 之类），
    别让一次持锁把心跳熬过去（锁被判成陈锁会让别人的 `起窗口` 清掉你的实例）。

.PARAMETER 目录
    脚本目录。默认 = 本脚本所在目录（`$PSScriptRoot`）。
    喂别的目录这条路不是给人用的，是给冒烟测试用的（见 `MidiPerformer.Tests\Tools\RunAllTests.cs`）。

.PARAMETER 超时秒
    每条脚本的上限，默认 600 秒。超了按 PID 杀掉那条子进程、记成红、写明是超时。

.PARAMETER 只跑
    只跑名字匹配这个通配的脚本（默认 `*`）。为了把一次整跑拆成几段持锁。

.PARAMETER 跳过实例检查
    **只跳过跑前那一次**（跑后照查照报）。给冒烟测试用：它测的是 runner，不是被 runner 跑的那些，
    不该被桌面上某个人的实例拦住。别拿它当「先跑了再说」。

.PARAMETER 实例名
    跑前跑后查的那个进程名，默认 `MidiPerformer`。
    **这个口子不是给人用的，是给冒烟测试用的**（同 `-目录`）：测「有实例在跑就抛、且绝不替用户关」
    这一条时，测试起一个**叫别的名字**的进程来当靶子（拿一份 `cmd.exe` 副本改个名），
    这样既真的把那条闸门打响了，又**不会在桌面上造出一个假的 MidiPerformer** 去干扰别人的实机验证。

.PARAMETER 只查日志
    不跑任何脚本，只按哨兵复核 `logs\*.log` 里已经存在的 log（哪一条没跑完）。
    这一节回答的是「上几趟到底跑完了没有」，是「退出码非零」之外的另一半。

.PARAMETER 只查引用
    不跑任何脚本、不查实例，只跑上面那三节关门判据，然后按判据出退出码。
    收口那一轮要单独复核 `tools/uitest/` 能不能脱开 `.scratch/` 独立站着，走这一条。

.PARAMETER 内部跑一条
    **内部用，别自己调。** 它是这个 runner 给自己起的「跑一条脚本的壳」：那一层壳重定向输出、
    写开始/止/断哨兵，然后带着脚本的退出码退出。

.EXAMPLE
    pwsh -NoProfile -File tools/uitest/run-all.ps1
    pwsh -NoProfile -File tools/uitest/run-all.ps1 -只跑 'verify-4*'
    pwsh -NoProfile -File tools/uitest/run-all.ps1 -只查引用
    pwsh -NoProfile -File tools/uitest/run-all.ps1 -只查日志

.NOTES
    退出码（本脚本只吐这四个，别的一律并进来）：
      0  = 全绿（所有脚本绿，且关门判据一条没命中）
      1  = 有红的 —— **总表里逐条列了**哪几条（脚本红，或关门判据命中）
      2  = 压根没跑起来：有实例在跑 / 目录不对 / 找不到 pwsh / 一条脚本都没扫到
      3  = 跑完了，但有脚本**没跑完**（缺哨兵 / 一个字节都没说）或**超时**
           —— 3 比 1 严重，所以它**压过 1**：一条没跑完的脚本，它那些 OK 只覆盖到断点为止，
              「有几条红」这个数本身就不完整。两者都在总表里，信息不丢。

    日志与总表落在 <目录>\logs\（`tools/uitest/logs/` 已被 .gitignore 盖住）：
      <脚本名>.log   —— 那一条的输出 + 三行哨兵
      run-all.json   —— 机器可读的总表（四态用的都是 ASCII 词：green/red/incomplete/timeout）
#>
[CmdletBinding()]
param(
    [string]$目录 = '',
    [int]$超时秒 = 600,
    [string]$只跑 = '*',
    [switch]$跳过实例检查,
    [switch]$只查日志,
    [switch]$只查引用,
    [switch]$内部跑一条,
    [string]$实例名 = 'MidiPerformer',
    [string]$脚本 = '',
    [string]$日志 = '',
    [string]$开始行 = ''
)

# ---------------------------------------------------------------------------
# 「跑一条脚本的壳」—— 它就是这个 runner 被自己起起来的那个子进程。
# ---------------------------------------------------------------------------
# 为什么要单独一层壳，而不是直接起 verify-XX.ps1：**这一层要把「有没有跑到结尾」写进 log**，
# 而脚本自己的 `exit N` 会直接结束进程 —— 只有壳里的 `& $脚本` 才能拿到那个退出码接着往下走
# （实测：脚本里的 exit 只结束那个脚本，$LASTEXITCODE 拿到 N，控制权回到壳里）。
#
# ⚠️ 这一层里**不许**加别的东西，尤其这两句在本脚本别处是对的、在这里是有害的：
#   · `$ErrorActionPreference = 'Stop'`  —— 偏好按作用域生效，`& $脚本` 起的子作用域会**继承**它，
#     于是脚本里一个本来只是警告的非终止错误会变成终止错误 —— 那是**悄悄改掉了它的行为**。
#   · `Set-StrictMode -Version Latest`   —— 同样会传进子作用域（未绑定变量当场抛）。
# 所以壳里只做三件事：截断并写「开始」、把脚本的输出**追加**进 log、按退出码写「止/断」再退出。
if ($内部跑一条) {
    $ErrorActionPreference = 'Continue'
    Set-StrictMode -Off

    $壳UTF8 = [System.Text.UTF8Encoding]::new($false)
    $壳换行 = [Environment]::NewLine
    try {
        [System.IO.File]::WriteAllText($日志, $开始行 + $壳换行, $壳UTF8)
        $global:LASTEXITCODE = 0
        $码 = 0
        try {
            # `*>>` 是 PowerShell 级的追加重定向：log 的文件句柄由**本进程**持有，
            # 脚本拉起的 app 继承的是控制台句柄、不是这个文件 —— 所以孤儿实例不会把 log 锁住
            # （这一点实测过：杀完壳之后往同一份 log 追加仍然成功）。
            & $脚本 *>> $日志
            $码 = $global:LASTEXITCODE
            if ($null -eq $码) { $码 = 0 }
            $尾 = '==== run-all 止 退出码=' + $码
        }
        catch {
            # 脚本自己抛了 = 跑到一半断了。**不许**吞掉算完成：写「断」哨兵，退出码非零，
            # 断在那儿的那句原文留在 log 里（能压成一行就压成一行，哨兵必须是独立的行）。
            $码 = 1
            $尾 = '==== run-all 断 抛了：' + (($_ | Out-String) -replace '\s+', ' ').Trim()
        }
        [System.IO.File]::AppendAllText($日志, $尾 + $壳换行, $壳UTF8)
        exit $码
    }
    catch {
        # 连哨兵都写不下去（磁盘满之类）：退一个非零码，让那一头记成「没跑完」。
        exit 1
    }
}

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$UTF8 = [System.Text.UTF8Encoding]::new($false)
$换行 = [Environment]::NewLine

$哨兵开始 = '==== run-all 开始'
$哨兵止   = '==== run-all 止'
$哨兵断   = '==== run-all 断'
$哨兵判定 = '==== run-all 判定'

# 脚本自己打的两行记号（不带 run-all 前缀，见抬头「脚本自己的两行记号」）。
# 为什么要记号而不是去正则猜中文：各脚本的裁决措辞不止一种（`条不过` / `条红` / `条没过` /
# `全过`…），而且是**人写的**，猜的那种判据迟早会猜错，猜错的方向不是漏报就是假红。
$哨兵裁决   = '==== uitest 裁决'
$哨兵不判据 = '==== uitest 不判据'

# 状态词用 ASCII 写进 log 和 json（控制台那张表才翻译成中文）—— 中文在 936 的宿主里
# 会变成乱码，而按状态词判红绿的编排最怕的就是「匹配不到就默认算过」（那正是最标准的假绿）。
$态绿 = 'green'
$态红 = 'red'
$态断 = 'incomplete'
$态超 = 'timeout'
# 不判据：说了话、退出码 0，但它**明说这一趟没有判据**（只体检 / 只拍照）。
# 它不算绿（绿灯 = 有一批判据真的过了），也不判红（没验的东西谈不上过不过），进总表单占一格。
$态不判 = 'noassert'

function 说([string]$行 = '') { Write-Host $行 }

# =====================================================================
# 目录与扫描
# =====================================================================

if ([string]::IsNullOrWhiteSpace($目录)) { $根目录 = $PSScriptRoot }
else {
    if (-not (Test-Path -LiteralPath $目录 -PathType Container)) {
        说 ''
        说 "!! 目录不存在：$目录"
        exit 2
    }
    $根目录 = (Resolve-Path -LiteralPath $目录).Path
}

$自身路径 = $PSCommandPath
if ([string]::IsNullOrWhiteSpace($自身路径)) { $自身路径 = Join-Path $PSScriptRoot 'run-all.ps1' }
$自身名 = [System.IO.Path]::GetFileName($自身路径)
$日志目录 = Join-Path $根目录 'logs'

# 扫的是 verify-*.ps1 —— 本脚本是 run-all.ps1，名字不匹配；再显式排除一次自己，
# 免得哪天有人把这个文件改了名又忘了这一条（那会变成 runner 递归起 runner）。
$要跑 = @(
    Get-ChildItem -LiteralPath $根目录 -Filter 'verify-*.ps1' -File |
        Where-Object { $_.Name -ne $自身名 } |
        Where-Object { $_.Name -like $只跑 } |
        Sort-Object Name
)

if ($要跑.Count -eq 0) {
    说 ''
    说 "!! $根目录 下一条 verify-*.ps1 都没扫到（只跑='$只跑'）—— 目录给错了，或者真的空。"
    说 '   本脚本自己没有匹配 verify-*，所以它不会是「扫到的那一条」。'
    exit 2
}

# =====================================================================
# 工具：读文本 / 去注释 / 扫语法级引用 / 列实例
# =====================================================================

function 读文本([string]$路径) {
    # 子进程那份输出由 pwsh 自己重定向（7.x 默认 utf8 无 BOM，5.1 会是 UTF-16LE），这里两种都认。
    if (-not (Test-Path -LiteralPath $路径 -PathType Leaf)) { return '' }
    $字节 = [System.IO.File]::ReadAllBytes($路径)
    if ($字节.Length -ge 2 -and $字节[0] -eq 0xFF -and $字节[1] -eq 0xFE) {
        return [System.Text.Encoding]::Unicode.GetString($字节)
    }
    return [System.Text.Encoding]::UTF8.GetString($字节)
}

function 去注释([string]$行) {
    # 把行尾注释切掉，但**不许吞字符串里的 `#`**（`'带 # 号的一句话'` 那种）。
    # 这把刀自己有一条体检用例（在冒烟测试里）：一段「字符串里有 #、后面还有真的 Join-Path」
    # 的行必须**照样被命中** —— 预处理退化成空转时，下面那些断言会悄悄变成「注释里提到也算数」。
    $出 = [System.Text.StringBuilder]::new()
    $引号 = [char]0
    foreach ($ch in $行.ToCharArray()) {
        if ($引号 -ne [char]0) {
            [void]$出.Append($ch)
            if ($ch -eq $引号) { $引号 = [char]0 }
            continue
        }
        if ($ch -eq '#') { break }
        if ($ch -eq "'" -or $ch -eq '"') { $引号 = $ch }
        [void]$出.Append($ch)
    }
    return $出.ToString()
}

function 去块注释([string]$全文) {
    # 块注释也是注释，可 `去注释` 那把刀只认 `#`（逐行、无状态）。这里先把块注释**整段抹掉**
    # （换行都留着，行号才不漂），再交给逐行那把刀。
    # 为什么非抹不可：这批脚本的抬头里到处都是 `.EXAMPLE`，示例命令迟早会写出一条
    # `pwsh -File .scratch\…`；那不折不扣是「文档」，判红就是判据自己制造的假红。
    # ⚠️ 这一段注释里我**故意不写**那两个块的标记本身 —— 把结束标记写进块注释会当场把注释
    #    关掉（本文件实测踩过，跟 `.axaml` 注释里不许出现 `--` 是同一族的坑）。
    $出 = [System.Text.StringBuilder]::new()
    $i = 0
    $引号 = [char]0
    $在块里 = $false
    while ($i -lt $全文.Length) {
        $ch = $全文[$i]
        if ($在块里) {
            if ($ch -eq '#' -and ($i + 1) -lt $全文.Length -and $全文[$i + 1] -eq '>') {
                $在块里 = $false; $i += 2; continue
            }
            if ($ch -eq "`n") { [void]$出.Append($ch) }
            $i++; continue
        }
        if ($引号 -ne [char]0) {
            [void]$出.Append($ch)
            if ($ch -eq $引号) { $引号 = [char]0 }
            $i++; continue
        }
        if ($ch -eq "'" -or $ch -eq '"') { $引号 = $ch; [void]$出.Append($ch); $i++; continue }
        if ($ch -eq '<' -and ($i + 1) -lt $全文.Length -and $全文[$i + 1] -eq '#') {
            $在块里 = $true; $i += 2; continue
        }
        [void]$出.Append($ch)
        $i++
    }
    return $出.ToString()
}

function 读代码行([string]$完整路径) {
    # 只留「真会执行的那部分」：块注释抹掉、`#` 注释切掉、字符串里的 `#` 不许动。
    # 三个扫描器都走这里 —— 判据的精度全押在这把刀上，所以它自己有一条体检用例
    # （冒烟测试里：一行里字符串含 `#`、`#` 后面还有真的路径字面量，必须照样被命中）。
    # ⚠️ 返回值**不许**加前置逗号：调用那头一律 `@(读代码行 …)`，而 `@(一个数组对象)`
    #    会把整个数组塞成**一个元素**（§5l 那个逗号坑的镜像面）—— 实测踩过一次，
    #    症状是「所有断言静默变成 0 命中」，比报错难查得多。
    $全文 = 去块注释 ([System.IO.File]::ReadAllText($完整路径, [System.Text.Encoding]::UTF8))
    $出 = @()
    foreach ($行 in @($全文 -split "\r?\n")) { $出 += 去注释 $行 }
    return $出
}

# =====================================================================
# 工具：脚本自己的裁决（态绿那三道自检要用）
# =====================================================================

function 取裁决([string[]]$行们) {
    # 认 `==== uitest 裁决 不过=N`（可选 `判据=M`）。取**最后**一行（多行以最后一行为准）。
    # 返回值三个键都在（StrictMode 底下访问不存在的键会炸）。
    $命 = @($行们 | Where-Object { $_ -like "$哨兵裁决*" })
    if ($命.Count -eq 0) { return $null }
    $原文 = $命[$命.Count - 1].Trim()
    $不过 = $null
    $判据 = $null
    if ($原文 -match '不过=(\d+)') { $不过 = [int]$Matches[1] }
    if ($原文 -match '判据=(\d+)') { $判据 = [int]$Matches[1] }
    return [pscustomobject]@{ 不过 = $不过; 判据 = $判据; 原文 = $原文 }
}

function 取不判据([string[]]$行们) {
    # 认 `==== uitest 不判据 理由=…`。没写理由也是「不判据」，理由那句话留空。
    $命 = @($行们 | Where-Object { $_ -like "$哨兵不判据*" })
    if ($命.Count -eq 0) { return $null }
    $原文 = $命[$命.Count - 1].Trim()
    if ($原文 -match '理由=(.+)$') { return $Matches[1].Trim() }
    return '（说了「不判据」但没写理由）'
}

function 脚本有exit([string]$完整路径) {
    # 态绿那三道里的第 3 道：没有裁决记号的脚本，**看它源码里有没有 exit**。
    # 只看代码位（块注释 / `#` 注释都切掉，跟那三个扫描器同一把刀），
    # 所以抬头里顺手写一句「末尾要 exit」不会算数。
    foreach ($行 in @(读代码行 $完整路径)) {
        if ($行 -match '(^|[^\w])exit([^\w]|$)') { return $true }
    }
    return $false
}

function 扫脚本([string]$目录路径) {
    # ⚠️ 这里**不许**加前置逗号。逗号那一招只在「直接赋值」下成立；本函数的返回值**一律被
    #    `foreach` 消费**，而 `foreach` 对「一个数组对象」会取它的成员（`$f.FullName` 变成
    #    一整串路径拼起来），报出来的错还长得像「文件名语法不对」—— 实测踩过一次。
    return @(Get-ChildItem -LiteralPath $目录路径 -Filter '*.ps1' -File | Sort-Object Name)
}

function 扫语法级引用([string]$目录路径) {
    # 只看 `.scratch` 出现在哪一类**语法形态**里（注释与提示字符串不算，理由见抬头）。
    # 命中一律是同一个形状 {file, line, kind, text} —— kind 是 ASCII 词，因为它要进 JSON
    # 给机器断言（冒烟测试就按 kind 断，不按说明那句话的措辞断）。
    #
    # ⚠️ 调用方约定（`return ,` 那 4 个函数都是这一条）：**必须 `$x = f` 先赋值，再 `$x.Count`**。
    #    不许写 `@(f).Count` —— 前置逗号把空数组保成了一个「一个元素的数组」，
    #    外面再套 `@()` 就把它当那**一个**元素，`.Count` 恒等于 1（73 号实测栽在这上面：
    #    `while (@(f).Count -ne 0)` 永不退出，差点当成产品缺陷开票）。
    #    本函数的所有调用点走的都是「先赋值」：`$引用 = 扫语法级引用 …`（就这一处调用点）。
    $命中 = @()
    foreach ($f in 扫脚本 $目录路径) {
        $码们 = @(读代码行 $f.FullName)
        for ($i = 0; $i -lt $码们.Count; $i++) {
            $码 = $码们[$i]
            if ($码 -notmatch '\.scratch') { continue }
            $类 = ''
            if ($码 -match 'Join-Path') { $类 = 'join-path' }
            elseif ($码 -match '^\s*\.\s') { $类 = 'dot-source' }
            elseif ($码 -match '&\s*pwsh\b') { $类 = 'argv' }
            # ⚠️ 第 4 类必须卡「字面量**以 `.scratch` 开头**」这个条件，不能写成
            #    `= "…任意…\.scratch…"`：那样连**提示文案**都会命中（本脚本自己那两句
            #    「语法级 `.scratch` 引用 N 处」就当场被自己判红过）——
            #    判据自己制造假红比不判更坏，这是实测踩到的第二颗。
            elseif ($码 -match "=\s*['""](?:\.\.\\)*\.scratch") { $类 = 'literal' }
            if ($类 -ne '') {
                $命中 += [pscustomobject]@{ file = $f.Name; line = ($i + 1); kind = $类; text = $码.Trim() }
            }
        }
    }
    return ,$命中
}

function 扫闭包引用([string]$目录路径) {
    # 每个被 dot-source / 被 `& pwsh -File` 起的子脚本，都得在**同一个目录**里。
    # 漏搬一个依赖的症状是「找不到文件，那几步变成什么都不做」，**不报错**。
    # 返回值是**一个对象**（不是「两个数组」）：两个空数组当两个返回值送出去的时候，
    # 空数组在管道里会被摊平成一个都没有，收的那头 `$闭包[0]` 就变成了 $null（§5l 那个坑的近亲）。
    $缺 = @()
    $外 = @()
    foreach ($f in 扫脚本 $目录路径) {
        $码们 = @(读代码行 $f.FullName)
        for ($i = 0; $i -lt $码们.Count; $i++) {
            $码 = $码们[$i]
            if ($码 -notmatch '\.ps1') { continue }
            $候选 = @()
            # ① Join-Path 的第二个参数是 `.ps1` 字面量（点源与起子进程都写成这个形状）
            foreach ($m in [regex]::Matches($码, 'Join-Path\s+\$\w+\s+[''"]([^''"]+\.ps1)[''"]')) {
                $候选 += $m.Groups[1].Value
            }
            # ② dot-source 直接跟字面量
            foreach ($m in [regex]::Matches($码, "^\s*\.\s+['""]([^'""]+\.ps1)['""]")) {
                $候选 += $m.Groups[1].Value
            }
            foreach ($名 in $候选) {
                if ($名 -match '^\.\.') {
                    $外 += [pscustomobject]@{ file = $f.Name; line = ($i + 1); kind = 'outside'; text = $名 }
                }
                elseif (-not (Test-Path -LiteralPath (Join-Path $目录路径 $名) -PathType Leaf)) {
                    $缺 += [pscustomobject]@{ file = $f.Name; line = ($i + 1); kind = 'missing'; text = $名 }
                }
            }
        }
    }
    return [pscustomobject]@{ 缺 = @($缺); 外 = @($外) }
}

function 扫像素API([string]$目录路径) {
    # 硬规矩：像素读回只能进 probe，不能进 verify（受 DPI / 主题 / 字体渲染影响，当回归门会变成随机红）。
    # 这一节**只列清单**，不判红 —— 存量那几处（find-note 这把按像素找音符的尺子、
    # 共用库的存图、verify-21/22 里那几处颜色判据）不在本票范围里，判红等于挂一条恒红的闸门。
    # ⚠️ 必须排除本脚本自己：run-all.ps1 里那份**词表本身**就写着这四个词，不排掉的话这一节
    #    永远比真实存量多 1 条，而且多出来那条正是「判据把自己算成命中」的老毛病。
    #    这是「把数报干净」，不是「改成判红」—— 这一节本来就不进退出码。
    #
    # ⚠️ 调用方约定同 `扫语法级引用`：**先赋值再 `.Count`，不许 `@(...)` 包**。
    #    本函数的调用点：`$像素 = 扫像素API …`（就这一处；后面那个 `@($像素 | Group-Object …)`
    #    包的是**已经落到变量里的数组**，管道照样逐元素走，不在此列）。
    $命中 = @()
    foreach ($f in @(扫脚本 $目录路径 | Where-Object { $_.Name -ne $自身名 })) {
        $码们 = @(读代码行 $f.FullName)
        for ($i = 0; $i -lt $码们.Count; $i++) {
            $码 = $码们[$i]
            foreach ($词 in @('PrintWindow', 'LockBits', 'GetPixel', 'CopyFromScreen')) {
                if ($码 -match $词) {
                    $命中 += [pscustomobject]@{ file = $f.Name; line = ($i + 1); kind = 'pixel'; text = $词 }
                    break
                }
            }
        }
    }
    return ,$命中
}

function 列实例 {
    # 只列，不关 —— 本脚本（以及那批 verify）**绝不代用户关窗口**（硬规矩第 1 条）。
    # 前置逗号：这个返回值只走「直接赋值」，别处也不许拿它去 @() -contains（见 §5l 那张表）。
    # ⚠️ 调用方约定同 `扫语法级引用`：**先赋值再 `.Count`，不许 `@(...)` 包**（包了恒等于 1）。
    #    本函数的调用点：`$前 = 列实例` / `$后 = 列实例`（都是先赋值，随后 `.Count`、`foreach`、
    #    或 `@($后 | ForEach-Object …)` —— 那个 `@()` 包的是**已经落到变量里的数组**，不在此列）。
    $出 = @(Get-Process -Name $实例名 -ErrorAction SilentlyContinue)
    return ,$出
}

function 写JSON([string]$路径, $对象) {
    $文本 = $对象 | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText($路径, $文本 + $换行, $UTF8)
}

# =====================================================================
# 关门判据（三节）—— 收口票的那一条总检查
# =====================================================================

$必须有 = @('uitest-lib.ps1', 'find-note.ps1', 'probe-41-dlg.ps1', 'probe-41\Program.cs', 'probe-41\Probe41.csproj')

function 出关门判据([string]$目录路径, [string[]]$脚本名们, [string]$日志路径) {
    # 每一行都带上 `命中`（结构化的 {file,line,kind,text} 数组）+ 人话的 `说明`。
    # 说明是给人读的，命中是给机器断言的 —— 冒烟测试只断后者，绝不按中文措辞断
    # （措辞一改测试就假红，那又是一种「判据自己制造红」）。
    #
    # ⚠️ 调用方约定同 `扫语法级引用`：**先赋值再 `.Count` / `foreach`，不许 `@(...)` 包**。
    #    本函数的调用点：`$关门 = 出关门判据 …`（两处，都是先赋值，随后 `foreach` /
    #    `@($关门 | Where-Object …)` / 传进 `判据行转JSON` —— 都没有 `@(出关门判据 …)` 这种写法）。
    $行 = @()

    # ---- ① `.scratch` 语法级引用 ----
    $引用 = 扫语法级引用 $目录路径
    if ($引用.Count -eq 0) {
        $行 += [pscustomobject]@{
            判据 = 'scratch'; 结果 = 'pass'; 命中 = @()
            说明 = "$($脚本名们.Count) 个脚本里没有一处语法级 `.scratch` 引用（注释与提示字符串不抓，见抬头）"
        }
    }
    else {
        $说 = ($引用 | ForEach-Object { "$($_.file):$($_.line) [$($_.kind)] $($_.text)" }) -join ' ;; '
        $行 += [pscustomobject]@{
            判据 = 'scratch'; 结果 = 'fail'; 命中 = @($引用)
            说明 = "语法级 `.scratch` 引用 $($引用.Count) 处：$说"
        }
    }

    # ---- ② 闭包完整 ----
    $闭包 = 扫闭包引用 $目录路径
    $缺 = @($闭包.缺); $外 = @($闭包.外)
    $缺成员 = @()
    foreach ($名 in $必须有) {
        if (-not (Test-Path -LiteralPath (Join-Path $目录路径 $名))) {
            $缺成员 += [pscustomobject]@{ file = '(member)'; line = 0; kind = 'missing-member'; text = $名 }
        }
    }
    if ($缺.Count -eq 0 -and $缺成员.Count -eq 0) {
        $尾 = if ($外.Count -eq 0) { '' } else { "（另有 $($外.Count) 处指向目录外的引用，列在下面）" }
        $行 += [pscustomobject]@{ 判据 = 'closure'; 结果 = 'pass'; 命中 = @(); 说明 = "点名的成员都在位，被 dot-source / 起子进程的脚本都在同一目录里$尾" }
    }
    else {
        $说 = @()
        foreach ($x in $缺) { $说 += "$($x.file):$($x.line) → $($x.text) 不在目录里" }
        foreach ($x in $缺成员) { $说 += "点名的成员缺位：$($x.text)" }
        $行 += [pscustomobject]@{ 判据 = 'closure'; 结果 = 'fail'; 命中 = @($缺 + $缺成员); 说明 = ($说 -join ' ;; ') }
    }
    foreach ($x in $外) {
        $行 += [pscustomobject]@{ 判据 = 'closure-outside'; 结果 = 'info'; 命中 = @($x); 说明 = "$($x.file):$($x.line) 引用了目录外的 $($x.text)" }
    }

    # ---- ③ 像素 API：只报不判 ----
    $像素 = 扫像素API $目录路径
    if ($像素.Count -eq 0) {
        $行 += [pscustomobject]@{ 判据 = 'pixel'; 结果 = 'info'; 命中 = @(); 说明 = '没扫到 PrintWindow / LockBits / GetPixel / CopyFromScreen' }
    }
    else {
        $按文件 = @($像素 | Group-Object file | ForEach-Object { "$($_.Name)(×$($_.Count))" })
        $行 += [pscustomobject]@{
            判据 = 'pixel'; 结果 = 'info'; 命中 = @($像素)
            说明 = "像素 API 出现 $($像素.Count) 处（**只报不判红**，硬规矩第 3 条）:" + ($按文件 -join ' ')
        }
    }

    # ---- 清单（E 那条「谁在位」的核对）：数量只报，点名的成员缺位已经算在 closure 里 ----
    $数字 = @($脚本名们 | Where-Object { $_ -like 'verify-*' }).Count
    $非数字 = @($脚本名们 | Where-Object { $_ -notlike 'verify-[0-9]*' -and $_ -like 'verify-*' })
    $行 += [pscustomobject]@{
        判据 = 'inventory'; 结果 = 'info'; 命中 = @()
        说明 = "脚本 $($脚本名们.Count) 个：verify-*.ps1 $数字 个（其中非数字命名 $($非数字.Count) 个：" +
               (($非数字 | ForEach-Object { $_ }) -join ' ') + "）"
    }

    return ,$行
}

function 判据行转JSON($行们) {
    # 三处（run / refs）共用一份投影 —— 同一个 run-all.json，schema 只有一种。
    return @($行们 | ForEach-Object {
        [ordered]@{
            check   = $_.判据
            verdict = $_.结果
            note    = $_.说明
            hits    = @($_.命中 | ForEach-Object {
                [ordered]@{ file = $_.file; line = $_.line; kind = $_.kind; text = $_.text }
            })
        }
    })
}

# =====================================================================
# 模式：-只查引用
# =====================================================================

if ($只查引用) {
    $脚本名们 = @($要跑 | ForEach-Object { $_.Name }) + @('uitest-lib.ps1', 'find-note.ps1', 'probe-41-dlg.ps1')
    $关门 = 出关门判据 $根目录 $脚本名们 $日志目录

    说 ''
    说 '========== 关门判据（只查引用，没跑任何脚本） =========='
    说 "目录：$根目录"
    foreach ($r in $关门) {
        $牌 = switch ($r.结果) { 'pass' { 'OK  ' } 'fail' { 'FAIL' } default { '--  ' } }
        说 "  $牌 [$($r.判据)] $($r.说明)"
    }

    if (-not (Test-Path -LiteralPath $日志目录)) { New-Item -ItemType Directory -Force -Path $日志目录 | Out-Null }
    写JSON (Join-Path $日志目录 'run-all.json') ([ordered]@{
        tool = 'tools/uitest/run-all.ps1'
        mode = 'refs'
        generatedAt = (Get-Date).ToString('o')
        dir = $根目录
        entries = @()
        instancesAfterRun = @()
        closure = 判据行转JSON $关门
        exitCode = $null
    })

    $红 = @($关门 | Where-Object { $_.结果 -eq 'fail' }).Count
    if ($红 -gt 0) { 说 ''; 说 "$红 条关门判据命中 —— 退出码 1"; exit 1 }
    说 ''
    说 '关门判据一条都没命中 —— 退出码 0'
    exit 0
}

# =====================================================================
# 模式：-只查日志（不跑脚本，只复核已有的 log 跑完了没有）
# =====================================================================

if ($只查日志) {
    $行 = @()
    $没有 = @()
    $不是Runner的 = @()
    if (Test-Path -LiteralPath $日志目录 -PathType Container) {
        $脚本名们 = @($要跑 | ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_.Name) })
        foreach ($f in @(Get-ChildItem -LiteralPath $日志目录 -Filter '*.log' -File | Sort-Object Name)) {
            $基名 = [System.IO.Path]::GetFileNameWithoutExtension($f.Name)
            if ($脚本名们 -notcontains $基名) { continue }   # 不是本目录脚本的 log（手工留下的、别人的），不判
            $全文 = 读文本 $f.FullName
            $行们 = @($全文 -split "\r?\n" | Where-Object { $_.Trim() -ne '' })
            $有开始 = @($行们 | Where-Object { $_ -like "$哨兵开始*" }).Count -gt 0
            $判定行 = @($行们 | Where-Object { $_ -like "$哨兵判定*" })
            if ($判定行.Count -gt 0) {
                $末 = $行们[$行们.Count - 1]
                if ($末 -notlike "$哨兵判定*") {
                    $行 += [pscustomobject]@{ script = $基名; state = $态断; note = '判定那行不在末尾 —— 这一份 log 被截断过（后面那些断言一条都没跑）' }
                    $没有 += $基名
                }
                else {
                    $态 = $态断
                    if ($判定行[$判定行.Count - 1] -match '状态=(\S+)') { $态 = $Matches[1] }
                    $行 += [pscustomobject]@{ script = $基名; state = $态; note = "末尾那句：$末" }
                    if ($态 -eq $态断 -or $态 -eq $态超) { $没有 += $基名 }
                }
            }
            elseif ($有开始) {
                $行 += [pscustomobject]@{ script = $基名; state = $态断; note = '有「开始」哨兵、没有「判定」哨兵 —— 这一趟没跑完（runner 或脚本被杀）' }
                $没有 += $基名
            }
            else {
                $行 += [pscustomobject]@{ script = $基名; state = 'not-runner'; note = '没有 runner 的哨兵 —— 这份 log 不是 runner 写的（手工重定向留下的），本工具不判它' }
                $不是Runner的 += $基名
            }
        }
    }

    说 ''
    说 '========== 只查日志（哨兵复核，没跑任何脚本） =========='
    说 "日志目录：$日志目录"
    if ($行.Count -eq 0) { 说 '  一条都没查到（目录里没有本目录脚本的 log）' }
    foreach ($r in $行) { 说 "  [$($r.state)] $($r.script).log —— $($r.note)" }
    if ($不是Runner的.Count -gt 0) { 说 "  （$($不是Runner的.Count) 份不是 runner 写的，跳过：$(($不是Runner的 | ForEach-Object { $_ }) -join '、')）" }

    if (-not (Test-Path -LiteralPath $日志目录)) { New-Item -ItemType Directory -Force -Path $日志目录 | Out-Null }
    写JSON (Join-Path $日志目录 'run-all.json') ([ordered]@{
        tool = 'tools/uitest/run-all.ps1'
        mode = 'audit'
        generatedAt = (Get-Date).ToString('o')
        dir = $根目录
        entries = @($行 | ForEach-Object {
            [ordered]@{ script = $_.script; state = $_.state; exitCode = $null; seconds = $null; log = $null; note = $_.note }
        })
        instancesAfterRun = @()
        closure = @()
        exitCode = $null
    })

    if ($没有.Count -gt 0) {
        说 ''
        说 "$($没有.Count) 条 log 没跑完 —— 「退出码非零」和「压根没跑完」是两件事，退出码 3"
        exit 3
    }
    说 ''
    说 '已有的 log 都带着判定哨兵 —— 退出码 0'
    exit 0
}

# =====================================================================
# 主流程
# =====================================================================

$pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
if ($null -eq $pwsh) {
    说 ''
    说 '!! 找不到 pwsh —— 那批脚本要用它起子进程，先装 PowerShell 7。'
    exit 2
}
$pwsh路径 = $pwsh.Source

if (-not (Test-Path -LiteralPath $日志目录)) { New-Item -ItemType Directory -Force -Path $日志目录 | Out-Null }
$JSON路径 = Join-Path $日志目录 'run-all.json'

# 上一趟的总表 —— 「改之前必须是红的」在报告上留下的痕迹（见抬头）。
$上一趟 = @{}
if (Test-Path -LiteralPath $JSON路径 -PathType Leaf) {
    try {
        $旧 = 读文本 $JSON路径 | ConvertFrom-Json
        foreach ($e in @($旧.entries)) {
            if ($null -ne $e -and $null -ne $e.script -and $null -ne $e.state) { $上一趟[[string]$e.script] = [string]$e.state }
        }
    }
    catch {
        # 上一份读不动就当没有 —— **不许**因此把这一趟的判定改掉（它跟这一趟的结果无关）。
        说 "（上一趟的总表读不动，忽略：$($_.Exception.Message)）"
    }
}

说 ''
说 '========== run-all：跑一遍全套 =========='
说 "目录  ：$根目录"
说 "脚本  ：$($要跑.Count) 条$(if ($只跑 -ne '*') { "（只跑='$只跑'）" })"
说 "日志  ：$日志目录"
说 '锁    ：本脚本自己不抢仓库锁 —— 跑它的人得在锁里跑（见抬头）。'
说 ''

# ---------- 跑前查一次实例 ----------
$前 = 列实例
if ($前.Count -gt 0) {
    if ($跳过实例检查) {
        说 "（-跳过实例检查：桌面上有 $($前.Count) 个 $实例名 在跑，这次不拦。跑后照查照报。）"
    }
    else {
        说 ''
        说 "!! 已经有 $实例名 在跑（PID $(($前 | ForEach-Object { $_.Id }) -join ', ')）—— 先自己关掉再跑。"
        说 '   本脚本**不替你关**（硬规矩第 1 条）：桌面上那个可能是你自己开着的窗口。'
        说 '   跑前查这一下，是为了别一上来就跟你的窗口抢前台。'
        exit 2
    }
}

$条目 = @()
$第几个 = 0
foreach ($f in $要跑) {
    $第几个++
    $名 = [System.IO.Path]::GetFileNameWithoutExtension($f.Name)
    $log = Join-Path $日志目录 ($名 + '.log')
    $开始 = "$哨兵开始 脚本=$名 时间=$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss')) 超时=$超时秒 秒"

    说 "[$第几个/$($要跑.Count)] $($f.Name)"

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $pwsh路径
    # ⚠️ 用 ArgumentList（.NET 会替我们转义）而不是把参数拼成一个字符串：
    #    这条命令行里有中文路径和引号，自己拼必然会在某台机器上拼错。
    foreach ($a in @('-NoProfile', '-File', $自身路径, '-内部跑一条', '-脚本', $f.FullName, '-日志', $log, '-开始行', $开始)) {
        $psi.ArgumentList.Add($a)
    }
    $psi.UseShellExecute = $false
    # 不新开控制台窗：一个跳出来的黑框会**抢走前台**，而那批脚本的清场闸门正是按前台判的
    # （抢走前台 = 后面所有断言悄悄变假绿）。
    $psi.CreateNoWindow = $true

    $钟 = [System.Diagnostics.Stopwatch]::StartNew()
    $p = [System.Diagnostics.Process]::Start($psi)
    $完了 = $p.WaitForExit($超时秒 * 1000)
    $钟.Stop()
    $秒 = [math]::Round($钟.Elapsed.TotalSeconds, 1)

    if (-not $完了) {
        # 卡死的脚本没有上限的话，runner 会**永远挂着** —— 那不是绿也不是红，是整套 harness 失效。
        try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
        # 杀完**等它真的没了**：`Stop-Process` 是异步的，不等的话下一句去追加 log 会撞上
        # 「文件正被另一个进程使用」（log 的句柄是那一层壳持有的，实测踩过一次）。
        try { [void]$p.WaitForExit(15000) } catch { }
        $码 = -1
        $状态 = $态超
        $说明 = "超过 $超时秒 秒没退出，按 PID $($p.Id) 杀掉"
        # 只杀这一条子进程（按 PID）。它自己拉起、又没被它自己收掉的 app **不去按名字杀一片**
        # （硬规矩第 1 条）—— 那几个会出现在跑后那一行里，那是必须看见的信息。
    }
    else {
        $码 = $p.ExitCode
        $全文 = 读文本 $log
        $行们 = @($全文 -split "\r?\n")
        $有止 = @($行们 | Where-Object { $_ -like "$哨兵止*" }).Count -gt 0
        $有断 = @($行们 | Where-Object { $_ -like "$哨兵断*" }).Count -gt 0
        # 「脚本自己的输出」= log 里除了 runner 那三行哨兵之外的东西
        $身上 = @($行们 | Where-Object {
            $_ -notlike "$哨兵开始*" -and $_ -notlike "$哨兵止*" -and
            $_ -notlike "$哨兵断*" -and $_ -notlike "$哨兵判定*" -and $_.Trim() -ne ''
        })
        $裁决 = 取裁决 $行们
        $不判理由 = 取不判据 $行们
        if (-not $有止) {
            $状态 = $态断
            $说明 = if ($有断) { '脚本自己抛了 —— 断点之后那些断言一条都没跑' }
                    else { 'log 里没有「止」那行哨兵 —— 脚本没跑到结尾（被杀 / 崩溃 / 断电）' }
        }
        elseif ($身上.Count -eq 0) {
            $状态 = $态断
            $说明 = "退出码 $码，但一个字节的输出都没有 —— 「不响的门」不是绿门（见抬头四态）"
        }
        elseif ($码 -eq 0) {
            # ⚠️ 修前这里就是 `$状态 = $态绿; $说明 = ''` —— 退出码 0 直接等于绿。
            #    于是「打印了 10 条红、末尾忘了 exit」的脚本在总表里是绿的（76 号实测）。
            #    现在拿**脚本自己的输出**复核三道（见抬头「态绿那三道自检」）。
            if ($null -ne $裁决 -and $null -eq $裁决.不过) {
                $状态 = $态红
                $说明 = "打出了裁决行，但里面没有「不过=N」（原话：「$($裁决.原文)」）—— 裁决策不出来，不敢当绿"
            }
            elseif ($null -ne $裁决 -and $裁决.不过 -gt 0) {
                $状态 = $态红
                $说明 = "脚本自己打出「不过=$($裁决.不过)」，退出码却是 0 —— 裁决没传出去（末尾缺 exit）"
            }
            elseif ($null -ne $裁决) {
                $状态 = $态绿
                $说明 = ''
            }
            elseif ($null -ne $不判理由) {
                $状态 = $态不判
                $说明 = "脚本声明这一趟没有判据（$不判理由）—— 不算绿：绿灯的意思是「有一批判据真的通过了」"
            }
            elseif (脚本有exit $f.FullName) {
                $状态 = $态绿
                $说明 = ''
            }
            else {
                $状态 = $态红
                $说明 = '退出码 0，但既没有「裁决」记号、也没有 exit —— 它红没红自己传不出来（补一行 `exit $fail` 就好了）'
            }
        }
        else {
            $状态 = $态红
            $说明 = "退出码 $码"
            if ($null -ne $裁决 -and $null -ne $裁决.不过) { $说明 = "$说明（脚本自己的裁决：不过=$($裁决.不过)）" }
        }
    }

    # runner 补的那一行：它回答的是「runner 收完这一条了吗」，跟「止/断」问的不是同一件事。
    $判定 = "$哨兵判定 状态=$状态 退出码=$码 耗时=$($秒)s 脚本=$名"
    $写上了 = $false
    for ($试 = 0; $试 -lt 20 -and -not $写上了; $试++) {
        try {
            [System.IO.File]::AppendAllText($log, $判定 + $换行, $UTF8)
            $写上了 = $true
        }
        catch {
            Start-Sleep -Milliseconds 250
        }
    }
    if (-not $写上了) {
        # 补不上判定那行，log 就会被 `-只查日志` 记成「没跑完」—— 但**不许**因此把「超时」
        # 改写成「没跑完」：那正是本票要分开的两个状态，合并掉等于这条判据没了。
        if ($状态 -eq $态超) {
            $说明 = "$说明（判定那行没补上：log 还被那个刚杀掉的进程占着）"
        }
        else {
            $状态 = $态断
            $说明 = '补判定那行失败（log 被别的进程占着）—— 这一条算没跑完'
        }
    }

    $上来 = if ($上一趟.ContainsKey($名)) { $上一趟[$名] } else { '' }
    $条目 += [pscustomobject]@{
        脚本 = $名
        状态 = $状态
        退出码 = $码
        耗时 = $秒
        上一趟 = $上来
        日志 = $log
        说明 = $说明
    }

    $汉字 = switch ($状态) { $态绿 { '绿' } $态红 { '红' } $态断 { '没跑完' } $态超 { '超时' } $态不判 { '不判据' } default { $状态 } }
    说 "       → $汉字（退出码 $码，$($秒)s）$(if ($说明 -ne '') { " —— $说明" })"
}

# ---------- 跑后查一次实例 ----------
$后 = 列实例

# ---------- 关门判据 ----------
$脚本名们 = @($要跑 | ForEach-Object { $_.Name })
$关门 = 出关门判据 $根目录 $脚本名们 $日志目录

# =====================================================================
# 总表
# =====================================================================

说 ''
说 '==================== 总表 ===================='
说 ("  {0,-16} {1,-8} {2,6} {3,8} {4,-8} {5}" -f '脚本', '状态', '退出码', '耗时', '上一趟', '日志')
foreach ($e in $条目) {
    $汉字 = switch ($e.状态) { $态绿 { '绿' } $态红 { '红' } $态断 { '没跑完' } $态超 { '超时' } $态不判 { '不判据' } default { $e.状态 } }
    $上汉字 = switch ($e.上一趟) { $态绿 { '绿' } $态红 { '红' } $态断 { '没跑完' } $态超 { '超时' } $态不判 { '不判据' } '' { '—' } default { $e.上一趟 } }
    说 ("  {0,-16} {1,-8} {2,6} {3,7}s {4,-8} {5}" -f $e.脚本, $汉字, $e.退出码, $e.耗时, $上汉字, $e.日志)
}

$红 = @($条目 | Where-Object { $_.状态 -eq $态红 })
$断 = @($条目 | Where-Object { $_.状态 -eq $态断 })
$超 = @($条目 | Where-Object { $_.状态 -eq $态超 })
$绿 = @($条目 | Where-Object { $_.状态 -eq $态绿 })
$不判 = @($条目 | Where-Object { $_.状态 -eq $态不判 })

说 ''
说 "合计：$($条目.Count) 条 —— 绿 $($绿.Count) / 红 $($红.Count) / 没跑完 $($断.Count) / 超时 $($超.Count) / 不判据 $($不判.Count)"
if ($红.Count -gt 0) { 说 "  红的那几条：$(($红 | ForEach-Object { $_.脚本 }) -join '、')" }
if ($断.Count -gt 0) { 说 "  没跑完的那几条（log 里的 OK 只覆盖到断点为止，**不是全过**）：$(($断 | ForEach-Object { $_.脚本 }) -join '、')" }
if ($超.Count -gt 0) { 说 "  超时的那几条：$(($超 | ForEach-Object { $_.脚本 }) -join '、')" }
if ($不判.Count -gt 0) {
    说 "  不判据的那几条（不算绿：它们没验任何东西；也**不是红**）：$(($不判 | ForEach-Object { $_.脚本 }) -join '、')"
}

说 ''
说 '---------------- 关门判据 ----------------'
foreach ($r in $关门) {
    $牌 = switch ($r.结果) { 'pass' { 'OK  ' } 'fail' { 'FAIL' } default { '--  ' } }
    说 "  $牌 [$($r.判据)] $($r.说明)"
}

说 ''
说 '---------------- 实例 ----------------'
if ($后.Count -eq 0) {
    说 "  跑完桌面上 0 个 $实例名 —— 收干净了"
}
else {
    说 "  跑完桌面上还有 $($后.Count) 个 $实例名（**必须看见**，不是内部细节）："
    foreach ($x in $后) {
        $起 = try { $x.StartTime.ToString('HH:mm:ss') } catch { '读不到' }
        说 "    PID $($x.Id) 起于 $起"
    }
    说 '  本脚本不按进程名杀一片（硬规矩第 1 条）—— 上面这几个请你自己按 PID 收。'
    说 "  ⚠️ 这**不影响本脚本的退出码**：列的是桌面上所有 $实例名，不是「这一趟留下的」"
    说 '     ——这一轮桌上同时有别人的实例来来去去（§5f），拿它判红等于让退出码看别人脸色。'
}

# =====================================================================
# JSON 总表 + 退出码
# =====================================================================

$关门红 = @($关门 | Where-Object { $_.结果 -eq 'fail' })
$总 = 0
$为什么 = '全绿'
if ($不判.Count -gt 0) { $为什么 = "没有红脚本，但有 $($不判.Count) 条声明不判据（不算绿）：$(($不判 | ForEach-Object { $_.脚本 }) -join '、')" }
if ($断.Count -gt 0 -or $超.Count -gt 0) {
    $总 = 3
    $为什么 = "有 $($断.Count) 条没跑完、$($超.Count) 条超时 —— 3 压过 1（「有几条红」这个数本身就不完整）"
}
elseif ($红.Count -gt 0 -or $关门红.Count -gt 0) {
    $总 = 1
    $为什么 = "脚本红 $($红.Count) 条，关门判据命中 $($关门红.Count) 条"
}
# ⚠️ 「不判据」**不进退出码**：它没验任何东西，谈不上过不过 —— 判红就等于凭空造出一条恒红判据
#    （「一条恒红的判据等于没有判据」）。它的全部意义是**不占绿灯**，在总表和 JSON 里单列一格。
# ⚠️ 「跑完还剩几个实例」**不进退出码**。`列实例` 列的是桌面上所有叫那**一个进程名**的进程，
#    不是「这一趟留下的」——这一轮桌上同时有别的 agent 的实例来来去去（§5f），
#    拿它判红等于让退出码看别人脸色。它是**必须看见的信息**（总表和 JSON 里都有），
#    但不是这一趟的结论。

$JSON条目 = @($条目 | ForEach-Object {
    [ordered]@{
        script = $_.脚本
        state = $_.状态
        exitCode = $_.退出码
        seconds = $_.耗时
        previousState = $_.上一趟
        log = $_.日志
        note = $_.说明
    }
})

写JSON $JSON路径 ([ordered]@{
    tool = 'tools/uitest/run-all.ps1'
    mode = 'run'
    generatedAt = (Get-Date).ToString('o')
    dir = $根目录
    timeoutSeconds = $超时秒
    filter = $只跑
    entries = $JSON条目
    instancesAfterRun = @($后 | ForEach-Object { [ordered]@{ pid = $_.Id; name = $_.ProcessName } })
    closure = 判据行转JSON $关门
    counts = [ordered]@{ green = $绿.Count; red = $红.Count; incomplete = $断.Count; timeout = $超.Count; noassert = $不判.Count }
    exitCode = $总
})

说 ''
说 "总退出码 $总 —— $为什么"
说 "总表（机器可读）落在 $JSON路径"
exit $总
