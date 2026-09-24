# MidiPerformer

把 MIDI 曲子在自己电脑上「弹」出来：按谱面合成键盘事件发给正在运行的游戏或软件。
Windows 桌面应用，C# + Avalonia，附钢琴卷帘编辑器与曲库。

## 它做什么

- **演奏** — 读一份 MIDI，把音符编成带时间戳的按键序列按点发出去（`SendInput`）。
  起跑前倒计时几秒，留给用户切到游戏窗口。开跑前预检三件事，任一不满足就不发一个音：
  进程是管理员（否则按键会被 UIPI 挡在前台窗口外）、输入法不在中文态（否则按键被输入法吃掉）、
  选中的轨是单声部（口琴同时只响一个音）。演奏中有覆盖窗显示进度，另有看门狗兜底。
  头一件不用读者操心：程序启动时会问一次要不要以管理员身份重启，**可以拒绝**；
  拒绝之后编辑照常，演奏会被预检挡下。
- **编辑** — 卷帘上改音高 / 起点 / 时值，框选、剪切、删除、撤销重做，改 BPM、整体移调，多轨折叠与静音。
- **曲库** — 标准 MIDI 文件（`.mid`）存在 **exe 旁边的 `songs\` 目录**；按保存直接落成一个 `.mid`，能拷给别人、能用别的软件打开。可导入 / 导出 MIDI。
- **试听** — 走 Windows 的 MIDI 输出设备（winmm）播放当前谱面。

## 环境

Windows 10 / 11 x64。构建需要 .NET 8 SDK；发布的单文件 exe 自带运行时。
演奏需要在管理员权限下运行 —— 不用手动提权：程序启动时会问一次要不要以管理员身份重启，
**可以拒绝**；拒绝之后编辑照常，演奏会被预检挡下。

## 构建与运行

```
dotnet build MidiPerformer.slnx
dotnet run --project MidiPerformer.App
```

曲库目录是 **exe 所在目录下的 `songs\`**（调试时即 `MidiPerformer.App\bin\...\songs\`）。
它不进仓库，删 `bin` 之前先把它挪出来。
曲库目录里还有一个 `songs\.work\` 子目录，装的是程序给每首曲子存的缓存（`.mproj`）：它跟着曲子一起写、
**不是你的曲子本身**，整个删掉也没关系 —— 只是移调、音符被删光的那条轨这些信息会跟着没掉。

## 测试

```
dotnet test MidiPerformer.slnx
```

跑全需要两个**并排目录**的仓库 —— 与本仓库放在同一个父目录下：

| 依赖 | 用途 | 不在会怎样 |
| --- | --- | --- |
| [drywetmidi](https://github.com/melanchall/drywetmidi)（`Resources/MIDI files/Valid`） | 真实 MIDI 语料：63 首，格式 0/1/2 与变速都有 | 语料相关的测试直接红，不静默跳过 |
| harmonica-auto-player（`HarpAutoPlayer`） | 对拍参照物：同一首曲子喂给两边，比操作逻辑是否一致 | 4 个对拍文件不参与编译，其余照跑（编译时打印一行提示） |
| `docs\wireframe.html` | 视觉令牌的对照物：令牌与它的 `:root` 逐条对应 | 令牌对账那 1 个文件不参与编译，其余照跑（编译时打印一行提示） |

后两项是本仓库自己的东西，只是不进仓库（`docs\` 是设计文档与原型；对拍那份是另一个仓库），
所以少了它们仓库照样编译、照样跑测试。

## 发布

```
pwsh tools/publish.ps1
pwsh tools/run-selftest.ps1
```

`publish.ps1` 出 win-x64 自包含单文件 exe（裁剪 + 压缩），落在
`MidiPerformer.App\bin\Release\net8.0\win-x64\publish\`。
`run-selftest.ps1` 用 `MIDIPERFORMER_SELFTEST` 让程序走内置自检 ——
不建窗口、不注册热键、不碰按键与 MIDI 设备，跑完出一份报告。
自检不覆盖对拍，对拍只在测试工程里跑。

## 结构

分层，依赖一律朝内。

| 工程 | 里面是什么 |
| --- | --- |
| `MidiPerformer.Core` | 模型、用例、端口。只依赖 BCL 与 DryWetMidi，不认识 Avalonia 与 Win32 |
| `MidiPerformer.Adapters` | 控制器、呈现器、网关（`SendInput` / 低层钩子 / winmm / 文件系统） |
| `MidiPerformer.App` | Avalonia 界面、主题、内置自检 |
| `MidiPerformer.Tests` | NUnit 测试，含与原版 harmonica-auto-player 的对拍 |
| `tools` | 发布、自检、图标生成脚本 |

## 许可

MIT，见 [LICENSE](LICENSE)。
