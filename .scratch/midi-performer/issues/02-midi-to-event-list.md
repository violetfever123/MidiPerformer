# 02: 从 MIDI 到事件表

**What to build:** 数据模型落地——`Song`、`Track`、`Note`、`TempoMap`。**音符只存 tick**，`Note` 上没有秒这个字段；`TempoMap` 单独持有速度事件表，负责 tick ⇄ 秒 的换算。

`Core/UseCases/Project/SongProject` 用 DryWetMidi 读标准 MIDI 文件，保留 tick 精度、轨数、每轨音色与轨名。DryWetMidi 只许住在这一个文件里，不许漏进 `Song`。

`Core/UseCases/Perform/Repertoire/RepertoireToSeconds` 是边界：用 `TempoMap` 把选中轨的音符 tick → 秒，**只算这一次**，之后接上 01 移植好的 `EventBuilder`。

`Core/UseCases/Timeline/SongWalker` 也在这一块建起来：音乐时间 ⇄ 物理时间的积分——事件表用的是**音乐时间（秒，与速度无关）**，由它按速度把物理流逝时间积成音乐时间，变速和跳转都归它管。它只依赖 `Song` + `TempoMap`，是纯数学，**试听和演奏共用**。所以放在 `Timeline/` 而不是 `Perform/`——留在 `Perform/` 里会让试听反过来依赖演奏。03 和 06 都直接用，谁也不等谁。

验证方式：一首真实的 MIDI 进去，一串带时间戳的按键序列出来。到这里「任意 MIDI → 正确的事件表」这条链是通的，但还没有真的发出去。

**把 01 的对拍升级到全链** —— 这是整个测试方案里最值钱的断言。同一个 `.mid` 文件，一边走原版（它自己的 MIDI 读取器 → 它自己的映射 → 它自己的事件表），一边走我们（DryWetMidi → tick 模型 → `TempoMap` → `RepertoireToSeconds` → 我们那份逐字相同的映射 → 我们那份逐字相同的事件表）。

**后半段两边各跑各的，代码逐字相同但没有一行是共用的**（01 已经证明两者等价）。这样比才有意义：如果两边真的共用后半段，比的就不是移植，而是接线。**这条全链对拍新覆盖的是前半段** —— 那才是可能出岔子的地方：PPQ 读错、变速处理错、轨选择错，全都会在这里露出来。

**Blocked by:** 01

**Status:** done

- [x] 导入真实 MIDI 后 tick 精确、轨数正确、每轨音色与轨名保留
- [x] SMF 格式 0 / 1 / 2 都能读
- [x] 坏文件、空文件、零音符文件不崩
- [x] `Note` 上没有秒字段（tick 是唯一的时值表示）
- [x] tick → 秒 → tick 往返恒等
- [x] 变速曲目在变速点**前后**的事件时间都正确（语料必须含变速曲目）
- [x] 一首真实多轨 MIDI 能产出事件表
- [x] `SongWalker`：音乐时间 ⇄ 物理时间在任意速度下往返恒等
- [x] `SongWalker`：变速曲目在变速点两侧积分连续，不跳变
- [x] `SongWalker`：能从任意位置起播（跳转）
- [x] DryWetMidi 的类型没有出现在 `Song` / `Track` / `Note` / `TempoMap` 的公开签名里
- [x] **全链对拍通过**：同一个 `.mid` 文件进两边（原版整条链 vs 我们整条链），事件表逐条相等
- [x] 全链对拍语料覆盖：变速曲目、多轨曲目、含升半音与跨八度的曲目、格式 0 / 1 / 2

## 验收记录

`dotnet test MidiPerformer.slnx` → **826 通过 / 0 失败**。

| 测试 | 条数 | 管什么 |
|---|---|---|
| `TempoMapTests` | 161 | tick ⇄ 秒 |
| `SongProjectReadTests` | 285 | MIDI 文件 → `Song` |
| `SongWalkerTests` | 31 | 音乐时间 ⇄ 物理时间 |
| `TrackTests` | 6 | 轨的值相等语义 |
| `FullChainParityTests` | 127 | 全链对拍 |
| 01 的移植保真 + 事件表对拍 | 225 | 后半段逐字等价 |

### 语料

借的是并排 `drywetmidi` 仓库的 `Resources/MIDI files/Valid`——**63 个真文件，格式 0/1/2 齐全，
分辨率 30–24576，23 首带变速**。依赖方式是并排目录，和 `PortFidelityTests` 依赖原版是同一种做法；
找不到直接红，不静默跳过（`MidiCorpus.AssertCorpusPresent`）。

语料本身的坑：那批 "Valid" 是「**能解析**」的意思，不是「有音乐」。里面混着几个空壳
（`xavier_rudd-no_woman_no_cry.mid` 是 14 字节、`tracks=0` 的光头文件；`12str.mid` 一个 NoteOn 都没有）。
所以「每个文件都得有音符」是错的断言——真正该盯的是**语料整体不能是空的**，
单列一条 `语料整体上有料` 数总轨数和总音符数，逐条对拍的测试里再逐个防「两边都空」的假绿。

### 两条关键设计决定

**1. 一条 `Track` = 轨块 × 声道。** 不是「一个轨块一条轨」。语料实测：63 个文件里 **32 个
存在「一个轨块装着多个有音符的声道」**（全部格式 0 和格式 2 都是）。按轨块切的话，格式 0 的整首曲子
会变成一条复音轨，04 的「下拉框只列单声部轨」就没法做，编辑器的「删掉一个声部」也失去意义。
何况 MIDI 的音色切换本来就是**声道事件**，「一条轨一个音色」只有按声道切才成立。
这个粒度和原版 `MidiCandidate` 一致，于是两边的轨列表天然一一对应，对拍可以按下标逐条比。

**2. `TempoMap` 是照 DryWetMidi 的浮点路径复刻的，不是自己推的公式。** 对拍要求两边的秒数
**逐位相同**，而原版走的就是 DryWetMidi。所以累积段「先乘后除」、末段「先除后乘」这两处看着像笔误的
形状差异是**故意的**，最后一步也走 `new TimeSpan(...).TotalSeconds` 这条 BCL 路径而不是自己写 `/1e6`。
`与DryWetMidi逐位一致` 用 `BitConverter.DoubleToInt64Bits` 比，63 个文件全部逐位相同。

### 测试有牙（都实测过）

- 把 `PulsesPerQuarter` 乘 2（PPQ 读错）→ 全链对拍红 **63 条**。
- 把 `RepertoireToSeconds` 的 20ms 下限改成 40ms → 全链对拍红（语料里确实有 20ms 以下的音）。
- 把四处边界守卫各自停用（`TempoMap` 的秒数/tick 越界、`SongWalker` 的坏倍速、`Track` 的值相等）
  → 红 **11 条**，逐条落在对应的测试上。

### code review 补的漏（都已修 + 有测试）

跑完 `/code-review` 又揪出六处，其中三处是**真的会出到用户面前**的：

1. **文件头残缺的文件会漏出英文异常。** `NotEnoughBytesPolicy.Ignore` 会让 `MidiFile.Read`
   **正常返回**、只是 `TimeDivision` 是 null，不自己查的话后面 `GetTempoMap()` 抛的是
   DryWetMidi 的 `ArgumentNullException("timeDivision")`——一句英文。现在把「抛异常」和
   「TimeDivision 是 null」合并成同一个判断，两条路都换成中文错误。
2. **分辨率是 0 的文件漏出英文异常。** 同上，换成中文的 `InvalidDataException`（PPQ 与 SMPTE 各一条）。
3. **音色只在「有音符的那个轨块」里找，漏掉了指挥轨。** 音色切换是**声道事件**，格式 1 常见把它
   集中在轨块 0。实测语料 **724 条轨里有 101 条（14%）** 是这个样子（`Fight6.mid` 声道 6：
   音符在轨块 1，音色在 0），这些轨的音色全都会退化成 0 号大钢琴。改成整份文件扫。
4. **`Track` 的相等比的是列表引用。** record 自动生成的相等对 `IReadOnlyList<Note>` 用引用比较，
   于是两份内容完全一样的轨也不相等——S1 缝的「导出 → 再导入，两个 `Song` 逐字段相等」
   即使实现全对也会红，而且红得莫名其妙。现在 `Track` 是**值**相等（逐个比音符），
   `Song` 保持**引用**相等（撤销装饰器拿它当「改没改」的判据）——两处不一致是故意的，代码里注了。
5. **`TempoMap` 丢了 DryWetMidi 的越界检查。** 原版有 `"Time span is too big."` 这道闸，
   少了它一个被改坏的 tick 会静默算出 long 溢出后的垃圾值。现在按 `!(x < max)` 的形状补回来
   （写成 `x >= max` 会把 NaN 放过去），`TickAt` 另外先拦非有限数——`double.Parse("NaN")` 是会成功的，
   界面上的倍速输入框真能递进来，而 `(long)NaN` 在 C# 里是未定义值（实测 −8854437155380584 这种垃圾）。
6. **`SongWalker.Clamp` 放行 NaN。** 原来写的是 `speed <= 0 ? 1.0 : speed`，而 `NaN <= 0` 是 **false**，
   NaN 一路进来会让 `MusicNow` 变成 NaN、`Finished` 永远 false——播放器卡死且不自愈。
   改成 `double.IsFinite(speed) && speed > 0`。

### TDD 抓到的真 bug

`BeatsPerMinuteAt` 复用了 `SecondsAt` 那把「严格早于」的二分查找，导致变速点上报的是**上一个**速度。
两者必须松一格：换算累计值时该 tick 之前的段用旧速度（`<`），
而「此刻速度是多少」该答新速度（`<=`）。修法是把三处查找收敛到一个带 `strict` 参数的 `LastAt<T>`。
`取某tick处的速度` 这条测试先红后绿。

### 两处刻意的取舍 / 没盖到的地方

- **SMPTE**：DryWetMidi 的换算器对 SMPTE 直接抛异常，所以**逐位对拍不可能**，全链对拍也覆盖不到。
  但读到 SMPTE 文件不该崩，所以按帧简单换算实现了，用 `SMPTE按帧换算与速度表无关` 和
  `SMPTE的文件能读出来` 测。另有一处已知偏差：格式号 29 按标准是 29.97（drop-frame），
  模型存整数帧率表达不了，当 29 用，这种文件算出来的时间会偏长约 **3.3%**。不修的理由是没语料也没参照。
- **`AdvanceTo` 不拦非单调的物理时刻**：传一个比上次早的时刻会让播放头倒退，这里**故意不**静默容忍——
  真出现就是 03 的时钟坏了，藏起来反而难查。前提写在方法注释里。
- **`.rmi`（RIFF 壳）**：成功路径和「RIFF 里没有 MThd」的报错路径都补了测试；
  但真实的 `.rmi` 样本没有，壳子是测试现搭的。
