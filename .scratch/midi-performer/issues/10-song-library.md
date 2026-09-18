# 10: 曲库与存档

**What to build:** 让曲子能存下来、找得回来。

- **歌曲库**（`Adapters/Gateways/SongLibrary`，纯目录操作、没有端口）：存在 exe 旁边的 `.\songs\`，文件名即曲名。侧栏列表显示所有导入过的曲子，能删、能改名。
- **导入**：拖文件进窗口就能导入，导入时可以命名。
- **存档**（`Core/UseCases/Project/SongProject`，S1 缝的另一半）：`.mproj`，JSON，带 `Version` 字段管迁移。**不做 DTO 层**——只有一个消费者的文件格式，多一层映射是纯仪式，实体直接序列化。MIDI 的导入导出也归它（见 02 / 11）
- 列表里能一眼看出哪首改过、哪首没动过。

**Blocked by:** 07

**Status:** done

两条线都落了：存档 + 曲库 + 侧栏面板（本切片自己验的），以及**接线**（面板挂进 `MainWindow`、
组装点建曲库、拖放导入、保存 / 另存为 / 改名 / 删除全串上，见「接线（后来补的）」）。
接线是主协调者做的 —— 面板和窗口的代码分开写的，接起来这一步没有别的归属。
所有条目现在都打勾了，**唯一没验的还是「要人眼看的」那一节**：面板真跑起来长什么样。

- [x] 歌曲库存成 `.\songs\`（exe 旁边），文件名即曲名 —— 目录是**注入**的（本类不猜自己在哪），`.\songs\` 这个默认值由组装点给（`App.axaml.cs` 唯一那一行 `Path.Combine`）
- [x] 拖文件进窗口即可导入 —— `MainWindow.AllowImportByDrop` / `FirstMidiPath`，只认 `.mid` / `.midi`
- [x] 导入时可以命名 —— `ImportFile` 里串上了；**取消命名不算失败**：曲子照常载入，只是不进曲库
- [x] 侧栏列表显示所有导入过的曲子 —— `BuildLibraryPanel` 塞进 `LibraryHost`
- [x] 能从列表里删除曲子 —— 面板问「真要删？」，盘上的活是 `OnDeleteRequested`；删掉的正好是当前这首时**不清空窗口**，只脱钩（见下）
- [x] 能改名 —— `OnRenameRequested` / `RenameTo`；不在曲库里的曲子改的是**标题**（还没进曲库，没有文件名可改）
- [x] 保存编辑成 `.mproj`（JSON，带 `Version` 字段）
- [x] 存了一半的曲子能读回来接着改
- [x] 列表里能看出哪首改过、哪首没动过 —— 面板那格小字（「改过 / 没动过 / 读不出来」）
- [x] 实体直接序列化，没有单独的 DTO 层
- [x] `.mproj` 读不回来时不崩，给出明确提示
- [x] 外观一律取自 **03** 的令牌，本切片不新增颜色值

## 落了什么

### 存档那半：`Core/UseCases/Project/SongProject.cs`

`WriteProject` / `ReadProject` / `SaveProject` / `LoadProject` / `TryReadProjectHeader`，
加上 `ProjectHeader(int Version, string Name, bool Edited, string? ImportedFrom)` 和 `ProjectVersion = 1`。
**没有 DTO**：`Song` / `Track` / `Note` / `TempoMap` 直接序列化，`Core/Model/*` 一个字节都没动
（连 `[JsonIgnore]` 都没加 —— 不是靠属性过滤，是靠 `JsonTypeInfo` 的 `Modifiers` 在写之前把
「构造器不收的成员」整个摘掉，那条注释里写了为什么）。

`Edited` 是**粘的**：改过就一直改过。语义是「这份工程进过编辑」，不是「现在和刚导入时长得一样」——
后者要求把原样留着比对，而用户要的是「哪些是我动过的」，不是「哪些我动完又undo回去了」。

读坏文件一律是中文 `InvalidDataException`，永远不崩；`TryReadProjectHeader` 只读文件头不碰谱面
（列表为了显示四个字去反序列化整首谱面是最不该干的事），读不出来返回 `null` 而不是抛。

### 曲库：`Adapters/Gateways/SongLibrary.cs`

纯目录操作，不认识 JSON（「读出来的是原样的文本」有测试钉着）。`Names` / `Contains` / `PathOf` /
`Read` / `Write` / `Delete` / `Rename` + 静态的 `Sanitize` / `IsUsableName`。
写 = 覆盖（那是保存），改名撞名 = 报错（那是「把 A 叫成 B」，顺手覆盖掉 B 等于悄悄删了它）。
`.\songs\` 这个位置**不在这个类里**：路径一律由组装点给，类不猜自己在哪。

### 界面：`Views/SongLibraryPanel.axaml(.cs)` + `Views/Dialogs.cs`

面板自己不动盘：点开 / 改名 / 删除都只是喊一声（`OpenRequested` / `RenameRequested` / `DeleteRequested`），
因为这三件事都会反过来影响窗口手上的状态（删掉的正好是当前这首怎么办）。唯一留在面板里的是
「真要删？」那句问话 —— 它得有个 `Window` 当爹才好居中，而窗口是面板这层拿得到、控制器拿不到的。

行是代码摆的（房子在 .axaml，家具在 .cs）：点击是**选中**，双击 / 回车才是打开
（上下键浏览时每挪一格就重载整首曲子的话，连看一眼下一首叫什么都不行）。
wireframe 里删除是悬停才显形的，这里改成**一直在、只是很淡**：这条切片没有能自动点一遍界面的测试，
一个平时看不见的按钮写错了没人会发现。

### 踩到的坑（都是 STJ 的，都不是猜的，是撞上去的）

1. **反序列化构造器的参数必须和属性同名同类型**。`TempoMap` 的构造器收 `IEnumerable<T>`、
   属性是 `IReadOnlyList<T>` → `InvalidOperationException: Each parameter ... must bind to an
   object property or field`，而且**只在读的时候炸**（写是好的，所以第一版测试绿了一半）。
   修法是给 `TempoMap` 写个 `JsonConverter`，而不是把模型的签名改成 `IReadOnlyList` ——
   模型收 `IEnumerable` 是为了调用方能传数组、能传 LINQ，**文件格式不该反过来规定模型的签名**。
2. **`ShouldSerialize = false` 挡不住 getter**。STJ 先 `Get(obj)` 再问 `ShouldSerialize`，
   于是 `Song.TotalSeconds`（超长 tick 上会抛「时间跨度太大」）在**写**的时候就把
   `WriteProject` 打炸了。修法是把这些成员从 `info.Properties` 里**摘掉**，不是让它们别序列化。
   这条是工单点名的「超长 tick」用例抓出来的 —— 先自己验证，别猜。
3. **`JsonElement.TryGetInt32` 遇到字符串元素是抛，不是返回 false**。它一抛，STJ 就把转换器那句
   中文理由换成它自己的英文套话。所以每个取值前都得先看 `ValueKind == Number` ——
   `ReadVersion` 那儿也补了，不然 `TryReadProjectHeader` 会在坏文件上抛而不是返回 null。
4. **.NET 8 的 STJ 会静默给缺失的值类型构造器参数填默认值**（少个 `Velocity` 就悄悄变成 0），
   既不报错也不出声。所以 `Note` 单独有个 `JsonConverter`：四个字段一个不能少，
   而且 pitch / velocity 卡 0..127、tick 不许为负 —— 手改坏的文件给一句中文提示，不是静默载入一首力度全是 0 的曲子。

### 没能自动验的部分（老实话）

`SongLibraryPanel` 和 `Dialogs` **没有被眼睛看过**：这个仓库没有能点界面的测试底座。
接线之后能自动验的已经验了（见上：带内容起得来、坏文件不掀桌子），但**长相和手感只能靠人**：

- 行的 hover / 选中态。**类都查过了，都在**：`song-name` / `song-meta` / `song-meta.bad` / `empty-hint` /
  `row-action` / `row-action.del` 在 `SongLibraryPanel.axaml`，`danger` 和 `ListBoxItem:pointerover` /
  `:selected` 在 `Controls.axaml`（没定义也不报错，只是没样式，所以这条得靠人查 —— 查了，没事）。
  唯一一处**可能**看着不对的：「×」那颗按钮同时挂着 `danger` 和 `row-action`，
  而 `row-action` 把 `BorderThickness` 设成了 0 —— 于是 `danger` 那个悬停变 warn 色的**边框**是死的，
  只剩背景变 `TokenWarnSoft`。字形本身仍是 `row-action` 的 `TokenInkFaint`（`danger` 不管 Foreground）。
  warn-soft 底 + 淡字够不够醒目，得眼睛说了算。
- 窄侧栏（212px）下长曲名的省略号；行里「改名」+「×」两颗按钮挤不挤
- 对话框在暗色主题下的样子、标题栏、按钮顺序
- 曲名框回车那条路（改名的确认、非法名字的报错、焦点有没有跑掉）
- 拖放：把一个 `.mid` 拖进窗口，以及拖一个非 MIDI 文件（应当什么都不发生）

### 接线（后来补的）

面板和窗口是**两个人分开写的**（面板那条线不许碰 `MainWindow`，不然两条线改同一个文件），
所以接线这一步单独落在这儿。改动只在三个文件：`App.axaml.cs`、`MainWindow.axaml(.cs)`。

1. **建曲库**：`App.axaml.cs` 里 `new SongLibrary(Path.Combine(AppContext.BaseDirectory, "songs"))`。
   `.\songs\` 这个位置**全程序只出现这一次** —— 测试塞临时目录靠的就是这个。
2. **挂侧栏**：`MainWindow.BuildLibraryPanel` → `LibraryHost`（XAML 只有个空 `ContentControl`：
   面板要曲库和取色桥两个构造参数，XAML 只能调无参构造）。行的布局改成 `ColumnDefinitions="212,*"`
   —— 212 是从 wireframe 的 `.split` 抄的。
3. **拖放导入**：`AllowImportByDrop` + `FirstMidiPath`。用 11.3 的新 API
   （`e.DataTransfer` / `DataFormat.File` / `TryGetFiles()`），老 API 全是 `[Obsolete]`，
   基线 0 警告不能破。`ImportFile` 是菜单和拖放**共用**的一条路。
4. **保存 / 另存为**：`SaveTo` 一处落盘，两颗按钮只是入口不同。tooltip 里「归 10」那句摘了。
5. **曲名框**：不再是只读装饰，回车 = 改名（进了曲库的）/ 存进去（没进的）。
   输入不合法就中文报错并回退到原名字，不留一个改了一半的状态。

**三个当时要拍板的地方**（都挑了「不悄悄毁掉用户东西」那一边）：

- **删掉的正好是当前这首**：窗口**不清空**，只把 `_currentName` 置空让它脱钩，并说一句
  「手上这份还在，按「保存」可以再存回去」。清空等于把用户没存过的编辑悄悄扔了 ——
  他只是想从列表里删掉一首曲子，不是想放弃手上的改动。
- **导入时取消命名**：不算失败。曲子照常载入能看能听，只是不进曲库（`_currentName = null`），
  之后按「保存」或曲名框回车再存。把「我不想现在起名」当成「我不想导入」太粗暴了。
- **`Edited` 的粘法**：`ApplySong` 里置 `true`，**永不写回 `false`**（除了重新载入一首曲子）。
  和上面「存档那半」的语义说明是同一条。

### 接线这一步验到了什么

- **`.mproj` 往返在真文件上对过**，不只是单元测试：另外起了个一次性脚手架（住在 `%TEMP%`，不进仓库）
  往曲库目录里种了四份工程 —— 两轨带变速跨拍号的、单轨默认速度的、一份**截断的坏 JSON**、
  一份**手改成 `Version: 9999` 的**。种完立刻 `LoadProject` 读回来逐字段比：轨数、音符数组、
  变速表、`Transpose` 全等。
- **版本守卫是真的**：`WriteProject` 无视 `header.Version`、一律盖 `ProjectVersion`（写出去只有这一种格式，
  照着调用方手里那个数写等于让文件谎报自己是另一种格式）；把盘上那个数手改成 9999 之后，
  `TryReadProjectHeader` 返回 `null`（那一行 `version > ProjectVersion → null`）。
- **坏文件不掀桌子**：截断的那份返回 `null` 而不是抛，列表照常显示剩下的。
- **带内容的窗口真起得来**：把四份工程摆进 exe 旁边的 `songs\` 再启动，
  进程活着 10 秒、stderr 一个字都没有。这条是有分量的 —— 列表是 `MainWindow` 构造器里就摆好的，
  `AddRow` 要是在坏文件上抛了，窗口当场就死了。

### 小账

- 「N 首」「改过」「没动过」「读不出来」这四句静态文案住在 `SongLibraryPanel.axaml.cs` 里，
  本该进 `Format`（本仓库「文案归 Format」的规矩）—— 但 `Format.cs` 不在本切片的文件清单里，没动。
  合并时想挪就挪，一行的事。
- 新增测试 **238 条**（`SongProjectFileTests` 192 + `SongLibraryTests` 46），
  全量 **1385 条全绿 / 33 秒**（接线前基线 1083）。接线之后**又自己跑了一遍全量**：
  还是 1385 / 0 失败（接线没加测试，也没弄坏别的）。
- 接线那三个文件（`App.axaml.cs` / `MainWindow.axaml` / `MainWindow.axaml.cs`）**没有新增测试** ——
  它们全是「把已有的东西接起来」，而这个仓库没有能点界面的测试底座。
  所以上面那节「接线这一步验到了什么」是**跑出来的**，不是读代码读出来的。

## 复查（对着整份 diff 又过了一遍，改在这之后）

派了一个专门挑错的复查，专门往「坏文件 / 手滑 / 半截状态」上戳。查出四条真的，都改了；
改完全量 **1389 条全绿**（原 1385 + 新增 4 条），0 警告。

### 1. 接线时弄丢了一层 catch —— 超长 tick 的曲子会把进程带走（**这条是我自己弄出来的**）

`ImportFile` 把 `LoadSong` 从 `try` 里挪出来了。原来那句是
`LoadSong(SongProject.Read(path), …)`，**整个在 try 里面**，而那个 catch 的过滤器里
明明白白列着 `InvalidOperationException` —— 那正是 `TempoMap.SecondsAt` 在 tick 大到
换算出装不下的微秒数时抛的东西。拆完以后：

- 导入这条路：`LoadSong` 没人挡了，异常从 `async void` 处理器里冒出去，`App.axaml.cs`
  和 `Program.cs` 都没有 dispatcher 级兜底 → **进程死**，用户看到的是一句中文错误变成整个程序消失。
- 从曲库点开那条路：`catch (InvalidDataException)` 只认这一种，**从来没挡过**这一条。

而且这不是假想：`SongProjectFileTests.超长tick往返一位不差` 用的 `StartTick = 9_000_000_000_000_000`
就在抛的范围内，而 `WriteProject` 不校验（JSON 里没有「装不下」的值）。也就是说这种工程
**存得下、读得回、列在曲库里**，只在装进窗口那一刻炸。

**改法**：在 `LoadSong` 开头验一道 —— 它是每首曲子进窗口的**唯一**入口（打开文件 / 拖放 / 曲库点开），
一处挡下三条路一起安全，而且不会**装到一半**（卷帘已经是新的、试听还是旧的，比压根不装更糟）。

判据落成了 `Song.TryMeasure`（`Core/Model/Song.cs`），放在 Core 而不是 App 里，就是为了**能测** ——
App 这层没有测试底座。它有 4 条新测试（`Tests/Model/SongTests.cs`）。

**那个判据有个坑，值得单独说**：最省事的写法是读 `Song.TotalSeconds`，但那走 `Song.EndTick`，
而 `Note.EndTick` 是 `StartTick + LengthTicks` —— 两个大数一加会**溢出成负数**，
`Math.Max` 就把这个音当「比 0 还小」忽略掉了，判据看着一切正常、试听那边照样抛。
`音符终点溢出成负数时也量不出来` 这条测试先把「`EndTick` 确实是 0」这个前提断言出来，
再断言 `TryMeasure` 说 false —— 免得将来有人把它「优化」回 `TotalSeconds`。

### 2. 另存为 / 导入起名会**悄悄覆盖掉另一首曲子**

`SongLibrary.Write` 是覆盖语义（那正是「保存」）。但「另存为…」和「导入时起名」不是保存：
名字都是**问出来**的，而那个对话框不带列表也没有补全，手打出一个已有的名字就会
拿手上这份把另一首曲子换掉，而且是覆盖写，撤不回来。导入那条更糟 —— 对话框里
**预填的就是 `Path.GetFileNameWithoutExtension(path)`**，所以拖一个 `起风了.mid` 进去、
按一下回车，曲库里原来那首「起风了」就没了。

「改名」那条路是拦着的（`Rename` 撞名直接抛），没道理「另存为」反而更松。

**改法**：加了 `AskNameForSaveAsync`，问完名字**撞名时先问一句**（`Dialogs.ConfirmAsync` 现成的）。
放行两种：名字就是当前这首（那是保存自己），或者曲库里没有。用户说不覆盖就带着刚打的名字
再问一次，而不是把他退回工具栏。曲名框那条路同理，但它是回车就走的，不适合弹框 ——
直接报一句「已经有一首了，换个名字；要覆盖请用另存为」。

### 3. 导入时起的名没同步到 `_title`

`ImportFile` 里自己写了一遍「写文件 + 摆状态」，和 `SaveTo` 是两份几乎一样的代码 ——
结果这份少写了一行 `_title = name`。手动改的话，症状是：导入 `track01.mid`、起名「起风了」之后，
把曲名框清空回车 → 报错并把框退回 `_title`，而 `_title` 还是 `"track01"` →
再回车就真的把曲库里那首「起风了」改名成 `track01` 了。

**改法**：`ImportFile` 不再自己写，改走 `SaveTo`（那一刻 `_edited` 是 false、`_importedFrom` 是原路径，
写出来的头一模一样）。写两遍的东西早晚会不一样，所以删掉一份。

### 4. 从曲库点开一首要**把整个曲库读两遍**

`LoadSong` 里有一次 `RefreshLibrary(null)`，`OnOpenLibrarySong` 紧接着又 `RefreshLibrary(name)` ——
每次都把曲库里每一首的工程文件重新打开解析一遍（`TryReadProjectHeader` 走的是
`JsonDocument.Parse(stream)`，是**整份**解析，只是不建对象）。200 首的曲库上就是白白卡一下。

**改法**：第二处只 `MarkCurrent`（列表内容这一路根本没变，我们只是读了一个文件）。

### 顺手改的两条小账

- **`只改大小写不算撞名` 这条测试原本验不出东西**：它断言的 `Names().Count == 1` /
  `Read("SONG")` / `Contains("song")` 在 Windows 上**改名没生效时也全都成立**
  （系统不分大小写，`File.Exists("song.mproj")` 照样 true）。补上 `Names()` 等于 `["SONG"]`
  之后才真的钉住「盘上的写法变了」。补完**是绿的** —— 说明 `File.Move` 的纯大小写改名在这台机器上是真生效的。
- **保留设备名漏了 `CONIN$` / `CONOUT$`**，补进 `ReservedNames`。
