# 02: 从 MIDI 到事件表

**What to build:** 数据模型落地——`Song`、`Track`、`Note`、`TempoMap`。**音符只存 tick**，`Note` 上没有秒这个字段；`TempoMap` 单独持有速度事件表，负责 tick ⇄ 秒 的换算。

`Core/UseCases/Project/SongProject` 用 DryWetMidi 读标准 MIDI 文件，保留 tick 精度、轨数、每轨音色与轨名。DryWetMidi 只许住在这一个文件里，不许漏进 `Song`。

`Core/UseCases/Perform/Repertoire/RepertoireToSeconds` 是边界：用 `TempoMap` 把选中轨的音符 tick → 秒，**只算这一次**，之后接上 01 移植好的 `EventBuilder`。

`Core/UseCases/Timeline/SongWalker` 也在这一块建起来：音乐时间 ⇄ 物理时间的积分——事件表用的是**音乐时间（秒，与速度无关）**，由它按速度把物理流逝时间积成音乐时间，变速和跳转都归它管。它只依赖 `Song` + `TempoMap`，是纯数学，**试听和演奏共用**。所以放在 `Timeline/` 而不是 `Perform/`——留在 `Perform/` 里会让试听反过来依赖演奏。03 和 06 都直接用，谁也不等谁。

验证方式：一首真实的 MIDI 进去，一串带时间戳的按键序列出来。到这里「任意 MIDI → 正确的事件表」这条链是通的，但还没有真的发出去。

**把 01 的对拍升级到全链** —— 这是整个测试方案里最值钱的断言。同一个 `.mid` 文件，一边走原版（它自己的 MIDI 读取器 → 它自己的映射 → 它自己的事件表），一边走我们（DryWetMidi → tick 模型 → `TempoMap` → `RepertoireToSeconds` → 我们那份逐字相同的映射 → 我们那份逐字相同的事件表）。

**后半段两边各跑各的，代码逐字相同但没有一行是共用的**（01 已经证明两者等价）。这样比才有意义：如果两边真的共用后半段，比的就不是移植，而是接线。**这条全链对拍新覆盖的是前半段** —— 那才是可能出岔子的地方：PPQ 读错、变速处理错、轨选择错，全都会在这里露出来。

**Blocked by:** 01

**Status:** ready-for-agent

- [ ] 导入真实 MIDI 后 tick 精确、轨数正确、每轨音色与轨名保留
- [ ] SMF 格式 0 / 1 / 2 都能读
- [ ] 坏文件、空文件、零音符文件不崩
- [ ] `Note` 上没有秒字段（tick 是唯一的时值表示）
- [ ] tick → 秒 → tick 往返恒等
- [ ] 变速曲目在变速点**前后**的事件时间都正确（语料必须含变速曲目）
- [ ] 一首真实多轨 MIDI 能产出事件表
- [ ] `SongWalker`：音乐时间 ⇄ 物理时间在任意速度下往返恒等
- [ ] `SongWalker`：变速曲目在变速点两侧积分连续，不跳变
- [ ] `SongWalker`：能从任意位置起播（跳转）
- [ ] DryWetMidi 的类型没有出现在 `Song` / `Track` / `Note` / `TempoMap` 的公开签名里
- [ ] **全链对拍通过**：同一个 `.mid` 文件进两边（原版整条链 vs 我们整条链），事件表逐条相等
- [ ] 全链对拍语料覆盖：变速曲目、多轨曲目、含升半音与跨八度的曲目、格式 0 / 1 / 2
