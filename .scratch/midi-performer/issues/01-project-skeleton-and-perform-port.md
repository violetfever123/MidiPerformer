# 01: 工程骨架 + 演奏逻辑移植 + 对拍

**What to build:** 建立四个工程（Core / Adapters / App / Tests）并把依赖方向钉死——Core **只引用 BCL + DryWetMidi**，编译器挡住任何往 Core 里塞 Avalonia 或 Win32 的尝试。

把 `harmonica-auto-player@a14335c` 的演奏逻辑**逐字移植进 `Core/UseCases/Perform/Repertoire/`**，除命名空间外不改一行。它就是用例层的一部分——不单独成工程、不单独起名字、不写额外的 README。搬进来之后它就是我们的代码了，和原版仓库再没有编译期关系。

搬进来的是这些类型：

- `InputTiming` · `InputTimingProbe`（来自 `Engine/InputTiming.cs`）
- `NoteMapper` · `MappedNote` · `MappingResult` · `Slot`（来自 `Engine/NoteMapper.cs`）
- `Music`（来自 `Midi/MidiModels.cs`）
- `EventBuilder`（原 `BuildSchedule`）· `PhysicalEvent` · `ModState` · 四个 `K_*` 常量（从 `Engine/PlaybackEngine.cs` **只切出这几块**）
- `PlayKeys`——键位常量。这一个是我们自己抽出来的文件，不是移植

**`PlaybackEngine` 本体不搬**——它那套线程、暂停恢复、播放中实时移调是给「设备实时输入」和「播放中换谱」用的，我们不需要。派发由我们自己的 `Dispatcher` 负责。

**原版仓库一行都不改。** 对拍不用 `InternalsVisibleTo`、不动任何可见性——原版已经导出了公开的 `BuildScheduleForTest`，直接用它。

对拍测试直接 `ProjectReference` 引用原版工程。**这一块的语料是手工构造的音符，不是 MIDI 文件**——它验的是**移植的保真度**（切出来的那几块有没有切错），不是 MIDI 解析。

真正有价值的**全链对拍**（同一个 `.mid` 文件进两边）要等 02 把前半段建好，见 02。

**Blocked by:** None (can start immediately)

**Status:** done

- [x] 四个工程建好，依赖方向与 spec 一致
- [x] Core 引用不到 Avalonia、引用不到任何 Win32 API（编译器报错即为通过）
- [x] `Core/UseCases/Perform/Repertoire/` 里的移植文件与原版逐行一致（差异只在命名空间）
- [x] 移植文件所在的目录里没有单独的 README，也没有为「移植」多出来的文件夹
- [x] `PlaybackEngine` 本体没有搬进来（线程、暂停恢复、实时移调都不在）
- [x] 原版 `harmonica-auto-player` 工作区干净，没有任何改动
- [x] 对拍通过：手写边界用例（同刻起音 / 连奏重叠 / 跨八度 / 升半音 / 最高两个音 / 超范围 / 空轨），两边事件表逐条相等
- [x] 对拍通过：随机生成语料，两边事件表逐条相等
- [x] 三档 `InputTiming`（稳健 / 标准 / 极限）各跑一遍
- [x] 对拍全程不发任何按键
- [x] 本切片的对拍是**移植保真度**（手造音符）；**真实 MIDI 语料的全链对拍留到 02**

## 验收记录

`dotnet test MidiPerformer.slnx` → **225 通过 / 0 失败**（4 条逐行保真 + 21 条手写边界 + 200 条随机语料），每条都跑三档时序。

**原版一行都没改，而且不用改**：`BuildScheduleForTest` 本来就公开，只是裹在 `#if HARP_TEST` 里。
测试工程用 `AdditionalProperties="DefineConstants=HARP_TEST"` 给那一个工程单独定义宏，
我们自己的 `DefineConstants` 不受影响，也不需要 `InternalsVisibleTo`。

**有意偏离逐字移植的地方，只有四处**（`PortFidelityTests.Normalize` 逐条列着，多一处就红）：

1. `PhysicalEvent` / `ModState` / 四个 `K_*` 从 `private` 改 `public` —— 派发方要看得见。
2. `BuildSchedule` → `Build`（方法名与可见性）。
3. `NoteMapper` 的 `Keys` / `TopKey` 两行改为指向 `PlayKeys.cs` —— 键位表抽出去单独成文件，见下。
4. `#if HARP_TEST` / `#endif` 两行没搬，`TraceSink` 改为常开（真机验证要能随时开追踪）。

另外 `RawNote` 也一并搬了：`NoteMapper.Map` 的签名里就有它，原版它和 `Music` 同在 `Midi/MidiModels.cs`，
所以按 spec 的目录结构落在 `Music.cs` 里。

**两个测试互为补充，都验过有牙**：

- 把 `ModLeadMs` 从 40.0 改成 45.0 → 对拍红 198 条。
- 把 `MergeVoicesByPriority` 的 `eps` 从 0.025 改成 0.030（对拍照不到的行为）→ 逐行保真红。

**边界要说清楚**：Core 的 `net8.0` TFM 拦住的是 Avalonia、WinForms、WPF 这些**框架提供的** Windows API
（已用探针文件验证：`CS0246` / `CS0234`）。手写的 `[DllImport("user32.dll")]` 编译期拦不住 ——
P/Invoke 本身不需要引用。要机器强制得另加分析器，01 没做。

**对拍覆盖不到的一条**：原版 `BuildScheduleForTest` 把 `startMods` 写死成 `ModState.None`，
所以「起点已按着修饰键」「起点残留音键」这两条分支比不到（要覆盖就得调 `Play`，那会真发按键）。
这两条待 04 用假 sink 直接测 `EventBuilder.Build`。
