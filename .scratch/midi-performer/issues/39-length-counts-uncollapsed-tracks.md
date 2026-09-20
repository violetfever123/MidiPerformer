# 39: 整曲长度只按没折叠的轨算

**What to build:** 折叠几条轨之后，曲子**当场变短** —— 收起来的那条轨要是本来就比别的长，
多出来的那几小节**直接消失**，而不是留着一段「画得出来、放不出声」的地方让播放头空转。

用户原话（2026-09-20）：

> 例如共十小节 减掉末尾两个小节 我希望是直接消失 而不是空着小节继续播放 如果还有其他音轨（无折叠）就继续播放

问过他「整曲长度按哪些轨算」，两个选项（「只算没折叠的轨」/「所有轨都算」），
用户选**「只算没折叠的轨（推荐）」**。所以口径是：**长度 = 还听得见的那几条轨里最长的那个末尾**，
一条都没得听时退回整份谱面（见下）。

**这一票动的是「曲子多长」，四处各有一份自己的长度**，都得跟着同一份折叠名单走：

| 谁 | 原来的算法 | 现在 |
| --- | --- | --- |
| `SongWalker.TotalMusicSeconds` | `song.TotalSeconds`（整份谱面） | `TempoMap.SecondsAt(_endTick)`，`_endTick` 可搬 |
| `PreviewPlayback.TotalSeconds`（进度条分母） | 同上 | 跟着 walker |
| `PianoRollController.BarCount`（卷帘 / 导航条 / 位置读数 / 小节定位的上界） | `BarCount(song.EndTick)` | `BarCount(AudibleLength.EndTick(song, muted))` |
| 位置读数里的 **M** | 同上 | 同上 |

新文件 `MidiPerformer.Core/UseCases/Preview/AudibleLength.cs` 是这四处的**唯一口径**：

```csharp
public static long EndTick(Song song, IReadOnlySet<(int TrackIndex, int Channel)>? mutedTracks)
{
    long end = 0;
    foreach (var track in song.Tracks)
    {
        if (mutedTracks?.Contains((track.TrackIndex, track.Channel)) == true) continue;
        if (track.EndTick > end) end = track.EndTick;
    }
    return end > 0 ? end : song.EndTick;
}
```

**几处要说清楚的决定：**

- **长度是派生视图，绝不写回 `Song` / `Track`。** `.mproj` 走的是「仅构造函数参数」规则
  （`SongProjectFile.cs`），往 `Song` 加一个「长度」字段它会自己写进文件；而且长度是**此刻折叠状态的函数**，
  不是谱面的一部分 —— 存下来的话，下次打开一首曲子会带着上次的折叠长度。所以 `AudibleLength` 只有静态方法，
  一条都不存。
- **兜底是「退回整份谱面」，不是 0。** 一条没静音的轨都没有、或者留下的那条**本来就是空轨**，
  都是同一件「没有声音」的事 → `end > 0 ? end : song.EndTick`。
  让长度当场变 0 的话，卷帘缩成一小节、播放头被拉回开头、进度条分母变 0 —— 那是把一句「静音」
  翻译成了「曲子没了」。单测 `全收起来退回整份谱面而不是零` / `留下的那条是空轨时也退回整份谱面` 钉着。
- **认轨用身份 `(TrackIndex, Channel)`，不是下标。** 格式 0/1 的 MIDI 常把好几条轨各放一个轨块、
  声道号却都是 0（Carulli 就是 4 个轨块 4 个声道），只报轨块号分不开。这条口径和 `TrackLaneView.IsCollapsed`、
  `CollapsedFlags` 是同一把尺子。
- **`SongWalker.EndTick` 是一个「可以搬动的终点」。** `SetEndTick` **只**改 `TotalMusicSeconds` 与 `Finished`，
  不动积分、不动锚点 —— 改的是「曲子多长」，不是「现在放到哪了」，播放头一个 tick 都不该跳（单测
  `搬曲尾不动播放头但会当场算走到头` 量 `MusicNow`/`TickNow` 前后一模一样）。负数当 0（空曲就是 0）。
- **终点搬小之后当前位置可能落到曲子外面**，把播放头拉回范围内**是调用方的事** —— 积分器只管时间，不管屏幕。
  窗口那一句在 `MainWindow.OnLaneCollapseChanged` 里，判据 `!IsPlaying && HasSong && MusicSeconds > TotalSeconds`。
- **正在播的时候窗口不抢先处理。** 折叠一条正在听的轨是「接着放，只是那条不响」（17 号定的），
  但曲子变短之后演奏**可能当场就到底了** —— 下一次定时器那一帧就会看见 `Finished`、停钟、把视图对齐回小节线。
  窗口在这一支里只补**暂停 / 停止**那半边：播放头停在曲子外面时没人会去动它（积分只在帧里走），
  读数会一直显示「位置 120 / 35」这种句子。
- **`PianoRollController` 的构造函数多收一份折叠名单。** 它是「全程序唯一做真换算的控制器」，
  `BarCount` 决定 `TotalTicks`、导航条、卷帘小节线、`SeekBar` / `TickOfBarClamped` 的上界 ——
  长度不在这儿跟着走的话，读数说 35 小节而卷帘还画到 124。**它不另存一份折叠状态**：
  之后名单变了走 `SetMutedTracks`，状态只有轨控件那一处。
- **`SetMutedTracks` 里长度没变就提前返回。** 折叠一条本来就短的轨是常事（§2 量到的就是它），
  而 `CountNotesPerBar()` 是整曲扫一遍音符。`PianoRollControllerTests` 用
  `Assert.That(controller.BarNoteCounts, Is.SameAs(counts))`（**同一个数组**）钉住这个提前返回。
- **`PreviewPlayback.SetMutedTracks` 里 `SetEndTick` 放在 `if (!IsPlaying) return;` 之前** ——
  暂停 / 停止时按折叠，长度也得当场算对（不然按播放之前读数还是旧的）。
- **换曲子必须清掉折叠**：`SyncLanes` 里是 `new PianoRollController(song, rebuildAll ? null : MutedTracks())`。
  `rebuildAll` 那一支**不能**传名单 —— `MutedTracks()` 读的是**旧**那批 lanes，而且控件上的折叠状态
  说的是「这一首」里的那一条轨。换曲子照旧从头发（`focused = null`）。

**Blocked by:** 无

**Status:** done

- [x] 折叠让**整曲小节数**跟着变（这一票的正题）
      —— `verify-39.ps1` §4：Carulli（轨末 tick 178080 / 44640 / 178080 / 49680，3/4 拍、PPQ 480 ⇒ 一小节 1440 tick）
      折叠两条**长的**（轨1 + 轨3，都是 124 小节）之后，位置读数从 `1 / 124 小节` 变成 **`1 / 35 小节`**。
      **35 这个数同时挡掉两种错法**：按最短那条算会是 **31**、按整份谱面算是 **124**。
      §6 全部展开 → 回到 **124**。
- [x] 长度是「**剩下最长的**那条」，不是「第一条」也不是「最短的」
      —— §2：折叠两条**短的**（轨2 = 末尾 31 小节、轨4 = 35 小节）→ M **一动都不动**（两次都是 124）；
      §3：只折叠**长的之一**（轨1）→ M 还是 **124**（轨3 那条长的还在）；展开回去都回 124。
- [x] 一条都没得听时**退回整份谱面**，不是 0 也不是 1
      —— §5：四条全折叠 → M 回到 **124**（`AudibleLength` 的兜底）。单测那两条（全折叠 / 留下的轨是空轨）同向。
- [x] **暂停中的播放头被拉回新曲尾**
      —— §7：先「跳到 120 小节」（读数 `120 / 124`），再折叠轨1 + 轨3 →
      读数**逐字**变成 **`35 / 35 小节`**（`-ceq` 比的中文串）。这是 `OnLaneCollapseChanged` 里那句
      「拉回曲尾」在真机上**唯一**的证人。展开回来 M 回到 124 而播放头停在 35 上不动。
- [x] 试听那一头：长度当场重算，**不动播放头**
      —— `PreviewPlaybackMuteTests`「长度也跟着折叠走」3 条：折叠最长的轨 `TotalSeconds` **6.0 → 2.0 → 6.0**；
      `Load` 时就按名单算；正放着时折叠 → `MusicSeconds 2.5 > TotalSeconds 2.0` 且 `IsPlaying` 仍为 true
      （窗口那颗判据赖以成立的状态本身被量到了，不是从结果倒推）。
      `SongWalkerTests`「曲尾是可以搬动的」4 条：默认 = 整份谱面；`SetEndTick(1920)` 后 `MusicNow`/`TickNow` 不动、
      `Finished` 当场为 true、`TotalMusicSeconds` = 2.0；搬回去时长回来；负数当 0。
- [x] 卷帘控制器那一头：`BarCount` 跟着折叠走，**选中与焦点轨不动**，长度没变就不重算
      —— `PianoRollControllerTests`「折叠让整曲小节数跟着变」6 条：建控制器时带名单 / 换名单 /
      折叠短的那条时那张表**是同一个对象**（`Is.SameAs`）/ 曲子变短后视图位置拉回曲子里面 /
      折叠不动选中与焦点轨 / 缩短后密度表只数曲子之内的小节。
- [x] `AudibleLength` 本身
      —— `AudibleLengthTests` **9 条**：不静音 = `song.EndTick`（null 与空名单都是）；收起最长的退回剩下最长的；
      收起短的**不变**；全收 / 留下的轨是空的 → 退回整份谱面；没有轨 → 0；
      **同一个轨块上的两条轨按声道分开**；名单里对不上的身份不影响任何东西；秒数走曲子自己那张速度表。
      用例的曲子是手搭的**三条不等长**的轨（末尾 1 / 3 / 2 小节）—— 等长的曲子测不出「按哪条算」，
      只能测出「随便挑了一条」。
- [x] 全套绿
      —— `dotnet build MidiPerformer.slnx` **0 警告 / 0 错误**；`dotnet test` **1670 通过 / 0 失败 / 0 跳过（35 秒）**
      （36 号那轮是 1647，这一票新增 **23 条**：`AudibleLengthTests` 9 + 改动那三个文件里 14）；
      `verify-39.ps1` **36 条 OK / 0 FAIL / 全过**，**连跑两遍**都一样（log 在 `.scratch/verify-39.log`）。

## 哪些是测出来的、哪些只是写出来了

**测出来的**（真机、真窗口、UIA 读控件 + 合成鼠标点那颗折叠开关；log 在 `.scratch/verify-39.log`）：

- **屏幕上那唯一一面**：位置读数 `Format.Position(BarOfTick(playhead), controller.BarCount)` 里的 **M**
  —— 124 → 35 → 124 是一条真读出来的数字。主窗上没有时长读数（进度条在演奏器窗里），所以 M 是这一票
  在全程序里唯一看得见的落点；§4 挑的 **35** 是**能区分两种错法**的数（31 = 按最短算、124 = 按整份谱面算）。
- **折叠是真的用鼠标点出来的**（`清场` + 移到停车点 + 点 + 之后断言开关上的字翻面），
  不是调 `SetMutedTracks` 设进去的 —— 那样量不到「点那颗开关会不会走到这条链」。
- **§7 那条拉回**是 `120 / 124` → `35 / 35` 两个读数逐字比出来的。

**只是写出来了、没测到**：

- **卷帘右侧那截有没有真的少画**：没扫像素。量到的是读数（= `BarCount`）和它背后的 `TotalTicks` 已经是新的；
  屏幕上皮马尔会不会照着新的小节数重画，这一票没验（36 号那套像素扫描是现成的，但这一票没花那个时间）。
- **试听那头真出声的时长**：winmm 出声在脚本里断言不了。单测量到的是 `SongWalker.TotalMusicSeconds`
  与 `PreviewPlayback.TotalSeconds` 这两个数，不是耳朵。
- **正在播的时候折叠「下一帧自己停」**：定时器那一帧推不动（真 `DispatcherTimer`），
  量到的是**状态**（`MusicSeconds > TotalSeconds` 且 `IsPlaying` 仍 true），不是「它真的停了」。
- **演奏器窗里进度条的分母**：那扇窗这一票没开。它读的是同一个 `TotalSeconds`。

## 给下一个人的坑

1. **别把长度写回 `Song` / `Track`。** `.mproj` 只写构造函数参数，加一个真字段它会自己进文件；
   而且长度是折叠状态的函数 —— 存下来就会「下次打开带着上次的折叠长度」。`AudibleLength` 一条都不存。
2. **兜底那句 `end > 0 ? end : song.EndTick` 别改成 0。** 症状是「全折叠之后卷帘缩成一小节、
   播放头跳回开头」—— 看着像 UI 抽风，其实是长度算成了「曲子没了」。
3. **两份名单必须是同一份。** `_playback.SetMutedTracks` 与 `_controller.SetMutedTracks` 都在
   `MainWindow.OnLaneCollapseChanged` 里从**同一个** `MutedTracks()` 现取。只改一处的话，
   读数说 35 小节而试听放到 124 小节（或者反过来）。
4. **`SyncLanes` 里那个 `rebuildAll ? null : MutedTracks()` 不是顺手写的**：`MutedTracks()` 读的是**旧**那批 lanes，
   换曲子那一支传名单会把上一首的折叠带到新曲子上。`PianoRollControllerTests.建控制器时就能带上折叠名单`
   是这条的另一半（同曲重建时名单要带上）。
5. **`PreviewPlayback.SetMutedTracks` 里 `walker.SetEndTick(...)` 必须在 `if (!IsPlaying) return;` 之前** ——
   放到后面的话，暂停中按折叠时长度不更新，按播放之前读数是旧的。
6. **`SetMutedTracks` 里那句提前返回（长度没变就什么都不做）别删。** `CountNotesPerBar()` 是整曲扫一遍音符，
   而「折叠一条短的轨」是常事。`Is.SameAs` 那条断言红了就是它被删了。
7. **`SongWalker.SetEndTick` 不许碰 `_musicNow` / `_anchorPhysical`。** 它改的是「曲子多长」，不是「现在放到哪了」。
   谁顺手在里面 `Seek` 一下，`搬曲尾不动播放头但会当场算走到头` 会红。
8. **`verify-39.ps1` 的窗口是开满工作区的，这不是随手写的。** 这台机器 3072x1920、任务栏从 y=1824 起。
   照 36 号那样摆 2360x1480 的话，**第 4 条轨的折叠开关落在窗口底边外面**（探针量到 y=1893，窗口底 1780），
   `点` 会在那儿报「清不干净：压着 Chrome」—— 看着像前台问题，其实是几何问题。
   verify-39 于是开满工作区，并且每次点之前对那颗开关先 `ScrollItemPattern.ScrollIntoView()`（卷帘那一片是 `LanesScroll`）。
9. **折叠开关是一颗按钮两句话**（收着时写「展开」）。`数轨` 那种只数 `'折叠'` 的写法在折叠之后会**少数**；
   verify-39 的 `折叠按钮` 两个名字都算，`折` 还会断言点完那颗的字翻面了（顺带证明点中的是这一条轨）。
10. **`ScrollIntoView` 会挪动别的开关**（把第 4 条滚进来之后，第 1 条跑到 y=-209）。所以每次点之前都要
    重新滚、重新取矩形 —— 不能先取一圈矩形再逐个点。
11. **`verify-39.ps1` 一次跑约 3 分钟**（18 次「点」每次都要清场 + 挪光标 + 停 500ms）。
    红了先看是哪一节：§0/§1 红 = 起窗或载曲出了问题；§2~§6 里任何一条红都先怀疑「点中的不是那条轨」
    （看紧邻的那句 `开关翻面了` 是否 OK）。
