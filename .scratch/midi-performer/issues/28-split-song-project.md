# 28: SongProject 拆成四份（读 / 写 / 工程文件 / 转换器）

**What to build:** 一个 1084 行、一个类干四件事的静态类，拆成四个单职责文件。**零行为变化。**

**几处要说清楚的决定：**

- 现在这一个静态类里塞了**四件互不相干的事**：
  1. **SMF 读取**（约 330 行）：从字节流解出 `Song`，含跨 chunk 的修补、文本编码嗅探、
     轨道命名、tempo map 解析
  2. **SMF 写出**（约 250 行）：把 `Song` 编回字节流，含 clamp 和写出前的校验
  3. **工程文件 JSON**（约 400 行）：存盘 / 读盘 / 只读文件头 / 向后兼容的版本处理
  4. **三个嵌套 JSON 转换器**（约 200 行）：`TimeDivision` / `Note` / `TempoMap`
- 拆成四个类：**`MidiReader`**（含字节层修补那几个私有方法）、
  **`MidiWriter`**（含校验和两个 clamp）、**`SongProjectFile`**（工程 JSON）、
  **`Converters`**（三个 `JsonConverter`）。
- **最有力的证据**：测试**早就按这三个边界分成三个文件**了
  （`SongProjectReadTests` / `SongProjectWriteTests` / `SongProjectFileTests`）。
  测试**早就知道**这是三件事，只有生产代码糊在一起。切完是一对一映射，不是重新划分。
- **纯搬移，零行为变化。** 现有测试就是安全网 —— 这条工单**不需要写新测试**，
  它要的是「搬完之后旧测试一条不改地全绿」。
- **不要顺手改任何逻辑。** 搬家途中看到可疑的地方（重复、可以简化的分支、命名不一致）
  **记下来另开工单**，这次只搬。
- 公开 API 尽量保持调用点少改；如果某个方法原本因为「都在一个类里」而 `internal`/`private`，
  现在要跨类了，就顺势提升可见性 —— 这是搬家的必要代价，不是设计动作。

**Blocked by:** 27（**排期决定，不是技术依赖** —— 先把全部 UI 工单做完，再动重构）

**Status:** done

- [x] 原来的四类职责各自落在独立的文件里，每个文件只有一个职责
- [x] 公开行为**一个字节都没变**，全套测试绿（旧测试**一条没改**）
- [x] 三个测试文件仍然分别对着新的三个类型
- [x] 没有类型还引用着已经不存在的旧类型名
- [x] 编译 0 警告 0 错误

> **排期上的一句交代**：这条写着「Blocked by 27」，而 27 还没做（它要等 18–26 全做完）。
> 那个依赖标的是**排期**（「先把 UI 工单做完再动重构」），不是技术依赖。
> 按「尽可能并行」的要求提前开了它，跑在独立 worktree 里。
>
> **顺带一个收益**：它改的 `MainWindow.axaml.cs` / `MainWindow.axaml` / `PianoRollController.cs` /
> `SongLibraryPanel.axaml.cs` 正是 19–25 要动的文件。**在 19 之前合进来，19–25 就是照新名字改**；
> 要是压到它们后面，那四处的改名就会变成一串冲突。所以这条早做不只是「能并行」，是**顺序上更省事**。

## 哪些是测出来的、哪些只是写出来了

**结果**：1084 行的 `SongProject.cs` → 四个文件（`MidiReader` 332 / `MidiWriter` 261 /
`SongProjectFile` 326 / `Converters` 221，共 1140 行）。方法名一个没改，改的只是**归哪个类**：
`SongProject.Read` → `MidiReader.Read`、`SongProject.Write` → `MidiWriter.Write`、
`SongProject.WriteProject` / `ReadProject` / `LoadProject` / `SaveProject` / `TryReadProjectHeader`
→ `SongProjectFile` 上同名。`ProjectHeader` 跟着 `SongProjectFile.cs` 走。
三个嵌套的私有 `JsonConverter` 从「私有」变成 `internal static class Converters` ——
那是搬家的必要代价，写在那个文件头上。

**机器测过的（全套 1570 条全绿，前后一条不多一条不少）：**

- **「零行为变化」这句话有证据，不是声明**：把这次改动的测试部分逐行过了一遍，
  **测试方法的签名一条没加、没删、没改**（`git show` 出来的增删行里没有任何 `public void`）；
  排除掉纯改名的行之后，剩下的改动**只有两行注释**。也就是说所谓「旧测试是安全网」
  不是「测试没红」这种弱证据 —— 断言那部分**根本没被碰过**。
- `模型的公开签名里没有DryWetMidi()` 这条反射测试照旧绿：DryWetMidi 现在住在
  `MidiReader` / `MidiWriter` 两个文件里（原来是「唯一一个文件」，spec 那句要改，见下），
  但模型那一侧一个字都没漏进去。

**没验的，按风险排 —— 这次搬家途中看见、但按工单要求没顺手改的东西：**

- **`docs/spec-演奏器.md` 现在和代码对不上了**，五处：第 183 / 185 / 211 / 422 / 446 行仍写着
  `UseCases/Project/SongProject`，而且第 183 和第 422 行那句「**唯一**允许出现 DryWetMidi 的文件」
  **现在是假的**（是两个文件）。这属于 27（文档同步），已经记在那边的清单里。
- **`MidiReader` / `MidiWriter` 这两个名字和 `Melanchall.DryWetMidi.Core` 里的同名类型撞了。**
  `SongProjectReadTests.cs` 现在得加 `using` 别名才分得清说的是哪一边。
  这是选名字时知道的代价，不是失误 —— 但下一个人第一次读那个文件时会先愣一下。
- **`if (notes.Length == 0) continue;`** 那一句是死的（搬家途中看出来的）。
- **`settings` 参数没人用。**
- **`TryTrimToCompleteChunks` 里 `head[10]` / `head[11]` 的假设**（假定 SMF 头一定是那个长度）
  没有当场确认过。

  ——后面这三条都是**行为可疑**而不是**搬家搬错**，工单明说了「只搬，可疑的记下来另开」，
  所以没动。要不要开票、算不算问题，等你定。
