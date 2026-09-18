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

**Status:** ready-for-agent

- [ ] 四个工程建好，依赖方向与 spec 一致
- [ ] Core 引用不到 Avalonia、引用不到任何 Win32 API（编译器报错即为通过）
- [ ] `Core/UseCases/Perform/Repertoire/` 里的移植文件与原版逐行一致（差异只在命名空间）
- [ ] 移植文件所在的目录里没有单独的 README，也没有为「移植」多出来的文件夹
- [ ] `PlaybackEngine` 本体没有搬进来（线程、暂停恢复、实时移调都不在）
- [ ] 原版 `harmonica-auto-player` 工作区干净，没有任何改动
- [ ] 对拍通过：手写边界用例（同刻起音 / 连奏重叠 / 跨八度 / 升半音 / 最高两个音 / 超范围 / 空轨），两边事件表逐条相等
- [ ] 对拍通过：随机生成语料，两边事件表逐条相等
- [ ] 三档 `InputTiming`（稳健 / 标准 / 极限）各跑一遍
- [ ] 对拍全程不发任何按键
- [ ] 本切片的对拍是**移植保真度**（手造音符）；**真实 MIDI 语料的全链对拍留到 02**
