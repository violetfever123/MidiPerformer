# 12: 内置自检与发布产物

**What to build:** exe 开 `PublishTrimmed`，裁剪会改变行为，NUnit 跑的是没裁剪的构建，**证明不了发布产物**。所以照搬 midikey-player 的既有做法：exe 里内置一套自检。

- 环境变量触发，`Program.cs` 第一件事就检查，走这条路时**不建窗口、不注册热键、不碰按键与 MIDI 设备**
- 自检写 PASS / FAIL 报告到文件
- 外面用 `tools/run-selftest.ps1` 驱动，退出码 0 / 1 / 2 / 3
- 自检只覆盖几条事件表冒烟用例（全量在 NUnit 那边）
- 发布单文件自包含 exe，`win-x64`，反射相关的程序集用 `TrimmerRootAssembly` 钉住

**Blocked by:** 04

**Status:** done

- [x] exe 内置自检，环境变量触发
- [x] 自检不建窗口、不注册热键、不碰按键与 MIDI 设备
- [x] `Program.cs` 第一件事就检查自检标志
- [x] 自检写 PASS / FAIL 报告到文件
- [x] `tools/run-selftest.ps1` 退出码 0 / 1 / 2 / 3 语义正确
- [x] 自检覆盖几条事件表冒烟用例
- [x] 发布单文件自包含 exe，`PublishTrimmed` 开
- [x] `TrimmerRootAssembly` 钉住 Avalonia 与 DryWetMidi
- [x] 在**裁剪后的发布产物**上跑通自检
- [x] 自检报告路径不接受仓库内的路径

## 这十个叉和 09 那十四个不是一回事

09 的手势链没有 UI 自动化，只能靠手感样机，所以那张清单必须拆开读。
**这一张能真验** —— 从环境变量到退出码到发布产物，整条链都在无人值守的机器上跑得出来。
所以下面每一条都写了证据是什么；**只读了代码的，就不说成「验过」**。

| 清单项 | 证据 | 性质 |
| --- | --- | --- |
| 内置自检、环境变量触发 | 发布产物 + `MIDIPERFORMER_SELFTEST`，20 PASS / 退出码 0 | 真跑 |
| 不建窗口、不注册热键、不碰按键与 MIDI 设备 | `SelfTest/` 整个目录 grep 不出 `Window` / `Hotkey` / `InputSender` / `MidiIn\|MidiOut`；**唯一的 `Avalonia` 是钉住名单里的程序集名字符串**。加上 `Program.cs` 那条早退，结构上走不到窗口 | 读代码 |
| `Program.cs` 第一件事就检查 | 逐字读过，见下 | 读代码 |
| 写 PASS / FAIL 报告到文件 | 报告落在 `%TEMP%`，脚本读回来数出 20 / 0 | 真跑 |
| 退出码 0 / 1 / 2 / 3 语义正确 | 四个码各造一次，见下 | 真跑 |
| 覆盖几条事件表冒烟用例 | 报告自述「9 个手写用例；时序档位 稳健 / 标准 / 极限」 | 真跑 |
| 单文件自包含 `PublishTrimmed` | publish 目录只有一个 `.exe`（41 020 718 字节），报告自述「单文件发布：是」 | 真跑 |
| `TrimmerRootAssembly` 钉住 | 两头：csproj 里 11 个（读）+ 自检自己断言（真跑，PASS） | 两头 |
| 在裁剪后的产物上跑通 | 就是上面那条 20 PASS | 真跑 |
| 报告路径不接受仓库内路径 | 三条不同路径都撞出 3 —— **但守卫在脚本里，不在 exe 里**，见下 | 真跑 |

`Program.cs` 里那两行（逐字）：

```csharp
// 自检分支必须是**第一件事**：走这条路时不建窗口、不注册热键、不碰按键与 MIDI 设备，
// 只跑 Core 的纯函数，跑完直接 Environment.Exit，绝不落到 StartWithClassicDesktopLifetime。
if (PerformerSelfTest.Requested)
    Environment.Exit(PerformerSelfTest.Run());
```

### 退出码：四个码各造了一次

| 想验的码 | 怎么造的 | 结果 |
| --- | --- | --- |
| 0 | 发布产物正常跑一遍 | 20 PASS / 0 FAIL，**0** |
| 1 | 把 `SongProject` 写工程那行的时值改成写 `0L`，编译 Debug，用 `-ExePath` 指过去 | 18 PASS / **2 FAIL**，**1** |
| 2 | `-ExePath C:\nope\MidiPerformer.exe` | **2** |
| 3 | `-ReportPath` 指到仓库里的一个文件 / 指到仓库根 / 用一个不写报告的 exe | 三条都是 **3** |

那个 1 是**真的把自检弄坏**验出来的，不是看代码推的 —— 报告里那两条 FAIL 是
「轨0 第0个音的时值 存的是「480」读回来是「0」」。验完从备份还原，`git status` 只剩脚本一处改动。

## 收尾核清单时找到并修掉的一处：退出码会漏出约定之外的码

脚本里原本有**两处**把子进程的退出码原样转发：

```powershell
if ($code -ne 0) { exit $code }   # 报告缺失那条
...
exit $code                        # 自检失败那条
```

而本脚本对外承诺的只有 `0 / 1 / 2 / 3` 四个 —— **其中 2 已经被「找不到 exe」占着**。
exe 明明找到了、只是崩了，CI 看到 2 会朝反方向查（去查路径，而不是去看它为什么崩）。

**怎么撞出来的**：造「报告没写出来」那条分支时，手边没有不写报告的 exe，
就拿 `where.exe` 当 `-ExePath`。它自己 `exit 2`，脚本原样吐了个 `2` —— 本该是 3。

修法：两处都**只放行 1**（文档里约定好的一条，「自检失败」），别的非零码并成 3 / 1，
原始码仍打在控制台上，没丢。改完同一条用例重跑，退出码变成 **3**。
文件头的 NOTES 也跟着写准了：`3` 那条现在明说「自检崩了（返回别的非零码）也走这条」。

**这条值得记的是它的来路**：清单上写的是「退出码语义正确」，而这句话对**正常路径**完全成立 ——
四个码都真的对。它错在一条**谁都没想到去走的分支**上，是造用例时顺手一试撞出来的。
换句话说：清单能核到「我想到的那几条是对的」，核不到「我没想到的那条也是对的」。

## 如实记三条边角（都没改，因为改了不如说清楚）

1. **「报告路径不许落仓库」这条守卫在脚本里，不在 exe 里。**
   手工设 `MIDIPERFORMER_SELFTEST=<仓库里的路径>` 再直接双击 exe，照样会写进仓库。
   脚本这条路（目前唯一在用的那条）是拦住的，三条路径都试过。清单上那句话对脚本成立、对 exe 不成立。
2. **「报告里有 FAIL、但进程返回 0」这条分支按构造不可达。**
   自检只要写了 FAIL 就返回 1（`return _failed == 0 ? 0 : 1`），报告写不出去也照样返回。
   所以那条 `exit 1` 是纯防御，没有用例能走到 —— 留着是因为**脚本不该假设 exe 永远守规矩**。
3. **`CustomPath` 遇到不带分隔符的值会静默改道。**
   `MIDIPERFORMER_SELFTEST=report.txt` 没有 `\` 也没有 `/`，于是被判成「不是路径」，
   报告落到 `%TEMP%\` 下那个固定名字里，**不报错、不提示**。脚本永远传绝对路径，所以碰不到；
   但手工试的时候会以为「我指定的路径没生效」。

另外：Git Bash 里跑出来的中文是乱码（控制台代码页），**报告文件本身是好的**（UTF-8，`grep` 正常）。

和 04 的分工照旧：`MidiPerformer.Core` **故意不在**钉住名单里 —— 它没有反射，
钉住反而会掩盖「裁剪把它裁没了」这类问题。`System.Text.Json` 那几条 IL2026/IL2075
是**预期的分析告警**，靠钉住名单兜住；`SongProject` 的工程读写是唯一走反射的地方，
而 NUnit 跑的是没裁剪的构建、**证明不了**它 —— 那个自检里的工程往返用例就是为这个补的。

## 交付物真的被人用了

发布目录里多出一个 `songs\*.mproj` —— **是程序跑起来之后存下的工程**，
`App.axaml.cs` 里那句 `Path.Combine(AppContext.BaseDirectory, "songs")` 是整个产品里唯一一次决定曲库位置的地方。
这是 12 号想要的那个产物在最粗一层上的证据：exe 能起来、能导入真曲子、能把工程存回去。

**给下次发布的人一条**：核这一轮时 `tools/publish.ps1` 失败过一次（`GenerateBundle` MSB4018），
原因是那个 exe **正被一个活着的进程占着**。失败没有伤到磁盘上那一份 ——
大小和 mtime 都没变，自检照样 20 PASS，`songs\` 里的工程 md5 前后相同。
**发布前先让程序退出**，否则就是这个报错。

全量测试：**1486 条全绿、52 秒**（`dotnet test MidiPerformer.Tests/MidiPerformer.Tests.csproj`）。
本切片改的是脚本与自检，没有动 C# 逻辑。

## 发布记录

**2026-09-20 全量发布**（31 张工单收口之后，`main` @ `3b1cd42`）：

| 项 | 数 |
| --- | --- |
| `tools/publish.ps1` | 退出码 **0**，publish 目录里只有那一个 exe |
| 产物 | `MidiPerformer.App\bin\Release\net8.0\win-x64\publish\MidiPerformer.exe`，**41 057 582 字节**（39.2 MB） |
| `tools/run-selftest.ps1` | **20 PASS / 0 FAIL**，耗时 0.6 秒，退出码 **0**（「结果：全部通过」） |
| 报告里自述的运行时 | 8.0.31；**单文件发布：是** |

和 12 号那一版（41 020 718 字节）比 **+36 864 字节**（+0.09%）—— 中间 19 张工单动过
UI、用例层、实体层，裁剪产物基本没动，说明这堆改动没把新的反射面引进发布产物。

发布前先确认**没有活着的 `MidiPerformer` 进程**（上面那条 `GenerateBundle` MSB4018 就是它）。
裁剪分析照旧刷 IL2026/IL2075 告警（Avalonia 绑定、`SongProject` 的 JSON 反射），
**是预期的**，靠 `TrimmerRootAssembly` 那 11 个钉住名单兜住 —— 自检里「钉住的程序集都还在」
和「DryWetMidi 的类型与公开方法没被裁掉」两条 PASS 就是它的证据。
发布只写 `bin/`、`obj/` 和运行时的 `songs\`，**工作区里没有一个被改动的文件**。

**2026-09-20 第二次发布**（32、33 两张工单收口之后，`main` @ `a0cbba3`）：

| 项 | 数 |
| --- | --- |
| `tools/publish.ps1` | 退出码 **0**，publish 目录里只有那一个 exe |
| 产物 | `MidiPerformer.App\bin\Release\net8.0\win-x64\publish\MidiPerformer.exe`，**41 053 486 字节**（39.2 MB），mtime 11:28:20 |
| `tools/run-selftest.ps1` | **20 PASS / 0 FAIL**，耗时 0.9 秒，退出码 **0**（「结果：全部通过」） |
| 报告里自述的运行时 | 8.0.31；**单文件发布：是** |
| 工作区 | `git status --porcelain -uno` **空** —— 发布没碰任何受版本控制的文件 |

和上一版（41 057 582 字节）比 **−4 096 字节**（−0.01%）。这一轮只动了两处 UI
（32 号：曲库行里的曲名不可编辑；33 号：走带条的 `■ 停止` → `↻ 重头播放`），
裁剪产物基本没动，说明这两张工单没把新的反射面引进发布产物。

发布前先关掉了 app（这次是 `verify-20` 收尾留下的那个实例，PID 22292）——
第一次发布栽的 `GenerateBundle` MSB4018 就是活进程占着 exe。
自检报告落在 `%TEMP%\midiperformer-selftest-20260920-112826.txt`。

**2026-09-20 第三次发布**（34 号工单收口之后，`main` @ `5b77e9c`）：

| 项 | 数 |
| --- | --- |
| `tools/publish.ps1` | 退出码 **0**，publish 目录里只有那一个 exe（文件数 **1**） |
| 产物 | `MidiPerformer.App\bin\Release\net8.0\win-x64\publish\MidiPerformer.exe`，**41 053 486 字节**（39.2 MB），mtime 11:43:03 |
| `tools/run-selftest.ps1` | **20 PASS / 0 FAIL**，耗时 0.9 秒，退出码 **0**（「结果：全部通过」） |
| 报告里自述的运行时 | 8.0.31；**单文件发布：是** |
| 工作区 | `git status --porcelain -uno` **空** —— 发布没碰任何受版本控制的文件 |

和上一版（41 053 486 字节）**逐字节同大小**。这一轮只动了 `OnJumpKeyDown` 里的一句
`ReleaseEditFocus()` —— 一个已经存在的方法调用，没有新的类型、没有新的反射面，
裁剪产物大小不变是合理的（**不是**「发布没生效」：`verify-34.ps1` 量的是 Debug 构建，
发布这一份是按 `main @ 5b77e9c` 重新打的，mtime 11:43:03 就是证据）。
`songs\` 下 5 个曲库文件照旧是运行时数据，没动过。

发布前先确认**没有活着的 `MidiPerformer` 进程**（三支验证脚本都会交代自己起的实例，
`verify-20` 那个是 `verify-33` 起手时收掉的），确认命令读回 **0**。
裁剪分析照旧刷 IL2026/IL2075 告警，是预期的那一批（Avalonia 绑定、`SongProject` 的 JSON 反射）。

**2026-09-20 第四次发布**（35、36、37 三张工单收口之后，`main` @ `90122fb`）：

| 项 | 数 |
| --- | --- |
| `tools/publish.ps1` | 退出码 **0**，publish 目录里只有那一个 exe |
| 产物 | `MidiPerformer.App\bin\Release\net8.0\win-x64\publish\MidiPerformer.exe`，**41 053 486 字节**（39.2 MB），mtime 12:42:35 |
| `tools/run-selftest.ps1` | **20 PASS / 0 FAIL**，耗时 0.8 秒，退出码 **0**（「结果：全部通过」） |
| 报告里自述的运行时 | 8.0.31；**单文件发布：是** |
| 报告 | `%TEMP%\midiperformer-selftest-20260920-124242.txt` |
| 工作区 | `git status --porcelain -uno` **空** —— 发布没碰任何受版本控制的文件 |

和上一版（41 053 486 字节）**逐字节同大小**（连着两轮同大小了）。这一轮三张工单都是
**界面文案 + 一个键盘动作**：35 号加了 `Shift + 空格`（新方法 `BackOneBarAndPlay`，
但它是 `Core` 里已有类型的又一次调用）、36 号把提示行拆成两层常量 + Esc 放开选中
（`ClearSelection()` 也是已有的）、37 号只改了常量里的字。没有新的类型、没有新的反射面 ——
裁剪器能看见的那张图没变，产物大小不变是合理的，**不是**「发布没生效」：
`verify-*` 量的是 Debug 构建，这一份是按 `main @ 90122fb` 重新打的，mtime 12:42:35 就是证据。
`songs\` 下 5 个曲库文件照旧是运行时数据，没动过。

发布前先确认没有活着的 `MidiPerformer` 进程（读回 **0** —— 上一轮 `verify-20` 那个实例
已经在它自己收尾时关掉了）。

**2026-09-20 第五次发布**（38、39 两张工单收口之后，`main` @ `53a7831`）：

| 项 | 数 |
| --- | --- |
| `tools/publish.ps1` | 退出码 **0**，publish 目录里只有那一个 exe（顶层文件数 **1**） |
| 产物 | `MidiPerformer.App\bin\Release\net8.0\win-x64\publish\MidiPerformer.exe`，**41 057 582 字节**（39.2 MB），mtime 15:50:56 |
| `tools/run-selftest.ps1` | **20 PASS / 0 FAIL**，耗时 0.8 秒，退出码 **0**（「结果：全部通过」） |
| 报告里自述的运行时 | 8.0.31；**单文件发布：是** |
| 报告 | `%TEMP%\midiperformer-selftest-20260920-155107.txt`（4 253 字节） |
| 工作区 | `git status --porcelain -uno` **空** —— 发布没碰任何受版本控制的文件 |

和上一版（41 053 486 字节）差 **+4 096 字节**（+0.01%，正好一个页面）—— **和第一次发布那一版
逐字节同大小**（41 057 582）。这一轮动的是**界面层**：38 号把「抽掉一段」的两个小节号框换成
在卷帘上拖一段（`PianoRollLane`、`TrackLaneView`、`PianoRollPresenter`、`Format`、
`PianoRollGeometry` 五个文件），39 号加了 `AudibleLength`（`Core` 里一个静态方法）。
新加的成员都是被直接调用的，没有多出反射入口，所以裁剪器能看见的那张图基本没变 ——
差一个页面的量级是合理的。`Core` 那座程序集照旧**故意不在**钉住名单里（见上文与 04 号的分工）。

发布前的进程检查读回 **0**（38 号那张票的验证脚本自己起、自己收，没留下实例）。
裁剪分析刷的还是预期的那一批 IL2026/IL2075（Avalonia 绑定、`SongProject` 与 `Converters`
的 JSON 反射）。

**一条调用姿势上的坑（不是发布脚本的问题）**：从 Git Bash 里调这个脚本时，
`pwsh -NoProfile -File tools/publish.ps1 *>&1 | tail -25` 这一句会被 **bash** 先接管 ——
`*>&1` 里的 `*` 是个 glob，展开成当前目录那一串**目录名**（`MidiPerformer.Adapters` …）
当位置参数塞给脚本，于是报 `找不到接受实际参数 'MidiPerformer.Adapters' 的位置形式参数`，
而管道最后那个 `$?` 还是 `tail` 的 0 —— **看着像发布过了，其实一行都没跑**。
`*>&1` 是 PowerShell 的写法，要留在 pwsh 那边：`pwsh -NoProfile -Command "& pwsh -NoProfile
-File tools/publish.ps1"`。（清场：那一次没有产生任何产物，publish 目录的 mtime 没动。）

**2026-09-20 第六次发布**（40 号工单收口之后，`main` @ `9ed0d4f`）：

| 项 | 数 |
| --- | --- |
| `tools/publish.ps1` | 退出码 **0**，publish 目录里只有那一个 exe（顶层文件数 **1**） |
| 产物 | `MidiPerformer.App\bin\Release\net8.0\win-x64\publish\MidiPerformer.exe`，**41 061 678 字节**（39.2 MB），mtime 17:13:34 |
| `tools/run-selftest.ps1` | **20 PASS / 0 FAIL**，耗时 0.5 秒，退出码 **0**（「结果：全部通过」） |
| 报告里自述的运行时 | 8.0.31；**单文件发布：是** |
| 报告 | `%TEMP%\midiperformer-selftest-20260920-171408.txt`（4 253 字节） |
| 工作区 | `git status --porcelain -uno` **空** —— 发布与自检都只写 bin/obj 与运行时的 songs\，工作区零改动 |

和上一版（41 057 582 字节）差 **+4 096 字节**（+0.01%，又是正好一个页面）。这一轮动的是
**界面层**：40 号把曲库从侧栏整块搬进一个独立的模态窗（新文件 `SongLibraryWindow.axaml`
+ `.axaml.cs`），工具栏那一排从「文件 菜单 + 右上角一颗演奏器」改成四样并排
（文件 / 歌曲库 / 操作 / 演奏），`SongLibraryPanel` 本身几乎没改（只去掉右边那条分隔线）。
新加的只有一个窗口类型和一段 `Button.menubar` 样式，XAML 是编译进程序集的
（`avares://MidiPerformer/Views/SongLibraryWindow.axaml`，无参构造那条 AVLN3001 已经消掉），
没有多出反射入口 —— 裁剪器能看见的那张图基本没变，差一个页面是合理的量级。
`songs\` 下照旧是运行时数据（这一份发布目录里 7 个文件），发布脚本没把它算进「单文件」那条判断，
也没动过它。

发布前的进程检查读回 **0**（40 号那张票的 `verify-40` 自己起、自己收，没留下实例）。
裁剪分析刷的还是预期的那一批（数出来了：**36 条** —— 33 条 `IL2026` + 1 条 `IL2075`
+ 2 条 `IL2104`），来源和历次一样：Avalonia 的绑定与 `Avalonia.DesignerSupport`、
`Melanchall.DryWetMidi`，以及 `Core\UseCases\Project\Converters.cs` 里那几个
`JsonSerializer` 反射重载。**这一轮没有新增的警告面** —— 40 号碰的全是 XAML 和视图层代码，
警告里点到名的文件只有 `MidiPerformer.App.cs`、`Converters.cs`、`SongProjectFile.cs` 三个，
一条也没落在 40 号改过的那些文件上。


---

**2026-09-20 第七次发布**（精简打包体积，`main` @ `2316e62` 之后）：

| 项 | 数 |
| --- | --- |
| `tools/publish.ps1` | 退出码 **0**，publish 目录里只有那一个 exe（顶层文件数 **1**） |
| 产物 | `MidiPerformer.App\bin\Release\net8.0\win-x64\publish\MidiPerformer.exe`，**22 107 792 字节**（21.1 MB），mtime 17:51:42 |
| `tools/run-selftest.ps1` | **20 PASS / 0 FAIL**，耗时 0.9 秒，退出码 **0**（「结果：全部通过」） |
| 报告里自述的运行时 | 8.0.31；**单文件发布：是** |
| 报告 | `%TEMP%\midiperformer-selftest-20260920-175147.txt` |
| 裁剪分析 | **36 条**，和上一轮逐条同源（33 条 `IL2026` + 1 条 `IL2075` + 2 条 `IL2104`） |
| 工作区 | `git status --porcelain -uno` 只有本轮的 csproj 一条（见下） |

和第六次发布的 41 061 678 字节比，**少了 18 953 886 字节（−46.2%）** —— 体积砍掉将近一半，
没有换掉任何功能，也没动一行界面代码。

### 砍在哪：一次干净构建一次的实测

「干净」= 每次先删 `obj` 再 publish（不删 `obj` 的话，只改 `-p:` 开关**增量构建不会重跑裁剪器**，
量出来的数字会一模一样，第一轮扫描就栽在这上面）。

| 变体 | 配置 | 字节 | 说明 |
| --- | --- | --- | --- |
| A | 不压缩（第六次发布的配置） | 41 401 646 | 基线 |
| B | A + `EnableCompressionInSingleFile` | 22 273 293 | **省 46.2%**，一行开关 |
| C | B + 全套功能开关（含 `NullabilityInfoContextSupport=false`） | 22 069 140 | 再省 0.4% |
| D | C + `TrimMode=full` | 21 744 915 | **自检直接红**，见下 |
| E | C + `UseSystemResourceKeys=true` | 21 823 952 | 只省 245 KB，代价见下 |
| **F** | **B + 除 `NullabilityInfoContextSupport` 外的那几个开关** | **22 107 792** | **← 采用** |

A 与第六次发布交付的那个（41 061 678）差 339 968 字节：同一套开关的两次干净构建之间
本来就有这个量级的抖动（单文件打包的拼接顺序会变），所以下面只比同一批测量内部的大小。

**「压缩会让启动变慢」这个理由被实测推翻了。** 这条理由原先写在 csproj 的注释里，是上一版
不开压缩的依据。实测（裸 exe 启动到窗口出现，各 5 次）：不压缩中位 **412 ms**、
压缩后中位 **402 ms** —— 压缩反而略快，因为读 22 MB 再解压比读 41 MB 更省 I/O。
`IncludeNativeLibrariesForSelfExtract` 本来就开着，自解压那一层早就存在，压缩并没有新增一层。
注释已经按实测改掉，并把三个**有意没开**的开关连同理由一起写在旁边（都在 csproj 里）。

**为什么没开 `TrimMode=full`**：能再省 36 万字节，但自检 **17 PASS / 1 FAIL** ——
`System.Text.Json` 报「`Song` 的反序列化构造器参数名被裁掉了，考虑改用源生成序列化器」。
要吃下这 36 万，得先把 `SongProjectFile` 换成源生成序列化器，那是另一张工单的活。

**为什么没开 `NullabilityInfoContextSupport=false`**：它只值 **38 652 字节（0.17%）**，
而 Avalonia 的绑定引擎会用到 `NullabilityInfoContext`。为一个四舍五入都不见的零头
去赌绑定引擎，不值 —— 这一条是整个扫描里唯一一处「体积换风险」，选择是不换。

**为什么没开 `UseSystemResourceKeys`**：只省 245 KB，代价是 .NET 的异常文本变成资源键
（`Arg_...` 这种），而曲库的 JSON 报错是**直接显示给用户看**的（`ShowError`）。

### 在跑着的应用里验过（自检看不见的那一半）

自检**不建窗口**，所以裁剪裁坏 XAML / 绑定 / JSON 反序列化这类事它一条也测不出来 ——
这一轮真开了窗口，在**发布产物**上走了一遍：

- 曲库窗口列出的 **8 首**都在，中文名没乱码，每首都是「没动过」；
- 双击载入**重新导入**的那一份（`I Really Want To Stay At Your House …`）：钢琴卷帘画出来了，
  2 轨 368 音 / 249 音，48 小节，128 拍/分；
- 双击载入**原样恢复**的那一份 `Carulli_Duetto_No2_Op4`：4 轨（guitar 1605 音 / guitar 27 音 /
  violin 640 音 / violin 16 音），124 小节，50 拍/分 —— 和文件内容逐项对上。
  这一份是**旧格式**（没有音符 `Id` 字段），能载进来 = 向后兼容成立。

顺带记一条踩坑：曲库那个模态窗口**不认注入的鼠标点击**（`SetCursorPos`+`mouse_event` 点不动，
先抢前台、设 TOPMOST、`SetActiveWindow` 都不行，`WindowFromPoint` 却明说那一点上挨着的就是它），
但**投递 `WM_LBUTTONDOWN`/`WM_LBUTTONUP`（PostMessage，客户区坐标）就认**。
主窗口的按钮注入点击是好使的，只有模态窗这样。脚本见 `.scratch/verify-lib-ui.ps1`。

### ⚠️ 本轮出过一次事故：曲库被清空，已恢复

按上面那张表做「干净构建」时，`rm -rf MidiPerformer.App/bin` **连发布目录下的 `songs\` 一起删了** ——
曲库住在 exe 旁边（`App.axaml.cs` 那句 `Path.Combine(AppContext.BaseDirectory, "songs")`），
`bin` 一删，7 首 `.mproj` 就没了。回收站是空的（`rm` 是 unlink）、没有卷影副本/还原点、没有 OneDrive。
**恢复情况**：

- `Carulli_Duetto_No2_Op4.mproj`、`cargo.mproj`：`%TEMP%` 里有两份验证时留下的副本，
  **md5 逐字节相同**，原样放回（两份曲库都放了）；
- 另外 5 首（`（三角洲适配）勾指起誓`、`I Really Want To Stay At Your House …`、`侏儒之歌 - 罗大佑`、
  `兰花草`、`弱水三千 DJ版 调教用`）+ `pink_floyd-time`：从源 `.mid` **重新导入**。
  走的是应用自己那条链（`MidiReader.Read` → `WriteProject`），验过：拿 `Carulli` 回放，
  除新增的 `Id` 字段外内容逐字段相同（2288 个音、速度表都一致）—— 也就是说重新导入得到的就是
  应用今天会写出的那份；
- **恢复不了的**：如果哪一首在应用里改过又存过，那些改动没了（重新导入是干净的源版本）。
  存下来的文件当时都是 `Edited: false`，所以这一条按「没有」计，但话得说清楚。
- 备份另存了一份在仓库外：`C:\Users\cao17\Desktop\midiplayer\曲库备份-20260920\`（8 个文件，约 3 MB）。

顺带一条教训：**这个仓库里不能对 `MidiPerformer.App\bin` 整个 `rm -rf`** ——
发布形态的曲库就住在那底下。要强制重裁只删 `obj` 就够了（本轮后半程就是这么做的）。
