using MidiPerformer.Adapters.Presenters;
using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Perform.Repertoire;
using MidiPerformer.Core.UseCases.Preview;

namespace MidiPerformer.Adapters.Controllers;

/// <summary>
/// 卷帘的脑子：像素 ⇄ (音高, tick) 的换算、命中判定、视图位置、选中。
///
/// **全程序只有一个 Controller**，因为它做真换算。其余「点按钮 → 调命令」是纯管道，
/// 位置由 Avalonia 的 code-behind 占着 —— 方法体是原样转发的类不该存在（见 spec「Controller」）。
///
/// 它**不认识 Avalonia**：视口是按控件的宽高现算的（<see cref="ViewportOf"/>），
/// 算出来的是一份纯数据。于是命中判定不用起窗口就能测，
/// 而「像素 → tick」与 Presenter 的「tick → 像素」都走同一个 <see cref="PianoRollGeometry"/>，
/// 不会各算各的。
///
/// 这一张是**只读**的：没有任何改谱面的方法，只有「看哪儿、选中谁、聚焦在哪条轨上」。
/// 编辑命令从它旁边过去，控制器只出三样东西给它们：
/// <list type="bullet">
/// <item><b>选中集</b>（<see cref="SelectedNotes"/> / <see cref="SetSelection"/>）——
/// 命令收的是 <see cref="NoteRef"/> 列表，界面得先能把它算出来。
/// <b>编辑之后不必重算</b>：坐标按身份寻址（见 <see cref="NoteRef"/>），改完把同一串原样
/// 交给新控制器就行。</item>
/// <item><b>区间查询</b>（<see cref="NotesInRange"/>）—— 「框住哪几个音」是几何问题，
/// 命令那边不该知道像素，也不该知道框。</item>
/// <item><b>带 ref 的命中判定</b>（<see cref="HitTestRef"/>）—— 点中的是谁，一步到位。</item>
/// </list>
///
/// 控制器自己不改谱面，是因为它是照着某一份 <see cref="Song"/> 建出来的一次性对象：
/// 命令换个引用就换了一份曲子，控制器跟着重建（见 <see cref="SetSelection"/>）。
/// 它要是自己也改，就得同时维护「手上这份曲子」和「界面那份」两个真相源。
/// </summary>
public sealed class PianoRollController
{
    private readonly Song _song;

    /// <summary>每条轨「按键盘定位的顺序」排好的音符身份（见 <see cref="BuildNavigationOrder"/>）。</summary>
    private readonly List<NoteId>[] _navigationOrder;

    private readonly List<bool>[] _inRange;
    private readonly (int Low, int High)[] _pitchRanges;
    private readonly int _ticksPerQuarterNote;

    /// <summary>选中的一组音。**顺序有意义**：最后一个就是主选中（见 <see cref="Selection"/>）。</summary>
    private readonly List<NoteRef> _selected = new();

    /// <summary><see cref="_selected"/> 的只读活视图，建一次就够 —— 见 <see cref="SelectedNotes"/>。</summary>
    private readonly IReadOnlyList<NoteRef> _selectedView;

    /// <summary>没有这条轨（曲子被删光了轨）时给的音域。空轨用的也是它。</summary>
    private static readonly (int Low, int High) EmptyPitchRange = PianoRollGeometry.FitPitchRange(60, 60);

    /// <summary>
    /// 按曲子的节拍网格建一个卷帘控制器。视图位置从曲子开头开始。
    /// </summary>
    /// <param name="mutedTracks">
    /// 此刻不收声的那几条轨（= 折叠起来的那几条），按 <c>(轨块号, 声道)</c> 给。
    /// **它决定整曲多少小节**：收起来的轨不算长度（见 <see cref="AudibleLength"/>），
    /// 不然「伴奏比主旋律长 8 小节」会让卷帘右侧多出一截什么也不出声的地方。
    ///
    /// 不给 = 一条都不静音，出来的就是从前那个「按整份谱面算」的数。
    /// **只在这里要一次**：之后名单变了走 <see cref="SetMutedTracks"/>，
    /// 控制器不另存一份 —— 折叠状态只有轨控件那一处（见 <c>MainWindow.CollapsedFlags</c>）。
    /// </param>
    public PianoRollController(Song song, IReadOnlySet<(int TrackIndex, int Channel)>? mutedTracks = null)
    {
        _song = song;
        TicksPerBar = PianoRollGeometry.BarTicks(song.TempoMap);
        _ticksPerQuarterNote = Math.Max(1, song.TempoMap.Division.TicksPerQuarterNote);
        BarCount = BarCountOf(mutedTracks);

        _selectedView = _selected.AsReadOnly();
        GridTicks = PianoRollGeometry.GridTicks(song.TempoMap);

        // 初始聚焦在第一条。窗口建完控制器之后会按折叠表现改一次（见 FirstExpanded），
        // 单独用控制器的地方（测试）拿到的就是这个 0
        FocusedTrack = 0;

        _inRange = new List<bool>[song.Tracks.Count];
        _pitchRanges = new (int, int)[song.Tracks.Count];
        // (0, 0) 是个合法音域，当不了「还没算」的哨兵 —— 显式填一个不可能的 Low
        for (int i = 0; i < _pitchRanges.Length; i++) _pitchRanges[i] = (-1, -1);
        BarNoteCounts = CountNotesPerBar();

        _navigationOrder = BuildNavigationOrder();
    }

    public Song Song => _song;

    /// <summary>一个小节多少 tick（按第一个拍号）。</summary>
    public long TicksPerBar { get; }

    /// <summary>
    /// 吸附网格多少 tick（一个十六分音符），构造时算一次。
    ///
    /// 曲子的分辨率在控制器活着的这一阵不会变（改分辨率要换一份 <see cref="Song"/>），
    /// 所以算一次就够，也免得每帧拖动的每次移动都过一个 <see cref="TempoMap"/>。
    /// </summary>
    public long GridTicks { get; }

    /// <summary>
    /// 整曲多少小节。视图、导航条、键盘定位都以它为上界。
    ///
    /// **只算没被静音的轨**（见 <see cref="SetMutedTracks"/>），所以折叠一条轨会让它变小。
    /// </summary>
    public int BarCount { get; private set; }

    /// <summary>一屏跨多少 tick —— 固定 4 小节，界面上没有缩放入口。</summary>
    public long TicksVisible => TicksPerBar * PianoRollGeometry.BarsVisible;

    /// <summary>整曲 tick 跨度，按小节向上取整。</summary>
    public long TotalTicks => TicksPerBar * BarCount;

    /// <summary>
    /// 每小节的音符数（**多轨合计**，只数在 <see cref="BarCount"/> 之内的小节）。
    ///
    /// **导航条不再拿它画东西了**（22 号工单把那条从「全曲密度柱」改成「焦点轨的音符块」），
    /// 所以这里数的是「整首曲子哪儿热闹」，而不是「你手上这条轨哪儿密」—— 两件事，别混。
    /// 现在整条链上只有测试在读它（`PianoRollControllerTests`）；
    /// 导航条那份 scene 由 <c>PianoRollPresenter.BuildNav</c> 按焦点轨现算。
    ///
    /// 表长跟着 <see cref="BarCount"/> 走：曲子被静音名单缩短之后，落在新曲子外面的音
    /// 不进这张表（它们本来也画不出来）。
    /// </summary>
    public IReadOnlyList<int> BarNoteCounts { get; private set; }

    /// <summary>
    /// 上面那张表里最密的那个小节有多少个音。
    ///
    /// 从前导航条按它归一化柱高，那个用途跟着 22 号工单一起没了（见 <see cref="BarNoteCounts"/>），
    /// 现在同样只剩测试在读。
    /// </summary>
    public int MaxBarNoteCount { get; private set; }

    /// <summary>当前视图左边缘对应的 tick。**永远落在合法范围内**（见 <see cref="PianoRollGeometry.ClampViewStart"/>）。</summary>
    public long ViewStartTick { get; private set; }

    /// <summary>
    /// 视图左边缘落在第几小节（0 起）。
    ///
    /// 21 号工单之后**界面上没人用它**了：视图范围从前写成「第 a–b 小节 / 共 N」，那行文字被
    /// 缩略图上的视口框取代，位置读数报的也是**播放头**所在小节（不是视口起始）。
    /// 现在只剩测试在读它 —— 留着是因为它仍然是「视口左边缘」这件事唯一的读数。
    /// </summary>
    public int ViewStartBar => PianoRollGeometry.BarAtTick(ViewStartTick, TicksPerBar);

    /// <summary>
    /// 主选中（轨下标, 音的身份）。读数条与滚动定位只认这一个；没选中是 null。
    ///
    /// **它永远是 <see cref="SelectedNotes"/> 里的某一个**（最后加进去的那个），不是另存的第二份状态 ——
    /// 所以不存在「主选中指着一个已经不在选中集里的音」这种两张嘴对不上的情况。
    /// 取最后一个，是因为它正好是用户最后点的那一个，也就是他此刻在看的那一个。
    ///
    /// 音那一半是**身份**不是下标（见 <see cref="NoteRef"/>）：读数条、键盘定位要的都是
    /// 「哪一个音」，而那个音在一次编辑之后照样是它，下标却可能换了地方。
    /// </summary>
    public (int Track, NoteId Note)? Selection
        => _selected.Count > 0 ? (_selected[^1].Track, _selected[^1].Id) : null;

    /// <summary>
    /// 选中的一组音。09 起选中不再是一个音：框选一组、一组一起挪，命令那边要的就是这一串
    /// （<c>MoveNotes</c> / <c>DeleteNotes</c> 收的都是 <see cref="NoteRef"/> 列表）。
    ///
    /// 返回的是**活视图**，不是每次调用现拷一份：一次重画要读它好几遍，拷出来就是白扔的分配；
    /// 而 <c>ReadOnlyCollection</c> 让调用方拿不到那个可变列表，改不了这里面的东西。
    /// </summary>
    public IReadOnlyList<NoteRef> SelectedNotes => _selectedView;

    /// <summary>某个 tick 落在第几小节（**1 起**）。走带条那个「位置」用它。</summary>
    public int BarOfTick(long tick) => PianoRollGeometry.BarAtTick(tick, TicksPerBar) + 1;

    /// <summary>
    /// 清空再选一个（在卷帘上点某个音用它）。
    ///
    /// 认不出的 ref 会被丢掉，于是等于清空 —— <b>不抛</b>：
    /// 曲子刚被换掉、轨刚被删掉的时候会碰上，那不是错误，是「它不在了」。
    /// </summary>
    public void SelectOnly(NoteRef note)
    {
        _selected.Clear();
        if (IsValidNote(note)) _selected.Add(note);
    }

    /// <summary>
    /// 整体替换选中集：框选、以及**每次编辑之后把选中集放回新控制器上**都用它。
    ///
    /// 编辑之后**不必重新认音**：坐标按身份寻址（见 <see cref="NoteRef"/>），
    /// 换了一份 <see cref="Song"/> 之后同一串 ref 指的仍然是同一批音，原样交回来就行 ——
    /// 从前这里要「拿新曲子重新算一遍」，那是下标寻址逼出来的（31 号工单删掉了那套镜像）。
    /// 这里唯一要做的判断是**这一串在新曲子上还成立吗**：不成立的丢掉，见下。
    ///
    /// 认不出的 ref 丢掉、重复的 ref 只留一个。后者不是洁癖：这一串是要原样交给 <c>MoveNotes</c> 的，
    /// 同一个音出现两次就会被挪两倍距离 —— 界面上「选中了两次」没有任何意义，不该被翻译成「动了两次」。
    /// 顺序原样保留，于是主选中仍然是传进来的最后一个。
    ///
    /// <b>代价写清楚：身份只在**一份曲子**里有意义。</b>两条读取路径都是从 1 开始连号发的
    /// （见 <see cref="NoteIdentity"/>），所以换一首曲子之后旧坐标「碰巧撞上一个号」是常事 ——
    /// 那样它不会被丢掉，而是**静默地指到另一个音上**。这条规矩因此是：换曲子时不许把旧坐标带过来
    /// （窗口那一路是重建控制器，选中集本来就是空的，见 <c>MainWindow.LoadSong</c>）；
    /// 而「同一首曲子的两个版本之间」带过来是安全的 —— 编辑命令只改内容不换号。
    /// </summary>
    public void SetSelection(IReadOnlyList<NoteRef> notes)
    {
        _selected.Clear();
        foreach (var note in notes)
        {
            if (!IsValidNote(note) || _selected.Contains(note)) continue;
            _selected.Add(note);
        }
    }

    /// <summary>
    /// 加一个进来（Shift 点选）。已经在里面就什么都不做 —— 否则主选中会被同一个音顶掉，
    /// 而用户按 Shift 点一个已经选中的音，意思是「把它也留着」，不是「把主选中挪到它身上」。
    /// </summary>
    public void ExtendSelection(NoteRef note)
    {
        if (!IsValidNote(note) || _selected.Contains(note)) return;
        _selected.Add(note);
    }

    /// <summary>清空选中集（主选中跟着变 null —— 它是这一串的尾巴）。</summary>
    public void ClearSelection() => _selected.Clear();

    /// <summary>
    /// 这个音在不在选中集里。线性扫一遍，没建哈希表：框选出来的一次也就几十个
    /// （一屏之内的音，再多也得先滚过去才框得到），扫一遍比建表快，
    /// 而且**顺序得留着**（主选中 = 最后一个），哈希集正好把顺序丢了。
    /// </summary>
    public bool IsSelected(NoteRef note) => _selected.Contains(note);

    /// <summary>这个 ref 指向的音在不在这份曲子里。选中集里的每个口子都要过它一道。</summary>
    private bool IsValidNote(NoteRef note) =>
        note.Track >= 0 && note.Track < _song.Tracks.Count
        && IndexOfId(_song.Tracks[note.Track], note.Id) >= 0;

    /// <summary>
    /// 这条轨上身份是 <paramref name="id"/> 的音在数组里的位置；没有就是 -1。
    ///
    /// 线性扫一遍：一条轨最多几千个音，而过这道口的都是用户点一下才走一次的地方
    /// （选中一个音、看一眼某个音的描述）。建索引表就得跟着音符数组一起维护，
    /// 而数组每次编辑都换一份 —— 一张会过期的表比一次线性扫危险得多。
    /// <b>位置只在控制器内部用</b>（要读那个音的内容、要算它在屏幕上的块），对外一律是身份。
    /// </summary>
    private int IndexOfId(Track track, NoteId id)
    {
        for (int i = 0; i < track.Notes.Count; i++)
            if (track.Notes[i].Id == id) return i;

        return -1;
    }

    // ==================== 聚焦轨 ====================

    /// <summary>
    /// 聚焦轨：轨道头高亮的那一条，Ctrl+↑/↓ 走的也是它。**是个下标。**
    ///
    /// <b>它不改变听到什么。</b>出不出声由折叠决定（收起来的轨在试听里不响，见
    /// <c>PreviewMixer</c> 的 mutedTracks），聚焦只管「手现在搭在哪条轨上」——
    /// 高亮它、把它滚进视野，别的轨照常响、照常显示。这是刻意的：
    /// 「我想改这条」和「我不想听这条」是两个意思，绑在一起的话，为了改一条轨就得先把它静音。
    ///
    /// 存的是下标，而**下标在一次删轨之后会整体前移** —— 所以窗口在换控制器之后
    /// 不能照着旧下标放回来，得先换算成轨的身份再换回来（见 <c>MainWindow.SyncLanes</c>）。
    /// 和折叠是同一条规矩，理由也一样：按下标带会把高亮挪到别人身上。
    /// </summary>
    public int FocusedTrack { get; private set; }

    /// <summary>
    /// 把聚焦挪到第 <paramref name="trackIndex"/> 条轨上。越界夹进范围，不抛。
    ///
    /// 两条路走它：窗口换完控制器把聚焦放回原来那条轨上（那条轨可能刚好被删掉了，
    /// 越界不是错误，是「它不在了」—— 和 <see cref="SetSelection"/> 对认不出的坐标是同一条规矩），
    /// 以及卷帘上点了一下、焦点跟着手走（见 <c>PianoRollLane.OnPointerPressed</c>）。
    /// 一条轨都没有时落在 0 —— 那时候没轨可指，但读数总得有个值。
    ///
    /// <b>返回焦点是不是真的挪了。</b>「点音符 → 焦点跟随」那条路拿它决定要不要喊一声：
    /// 点在自己已经聚焦的那条轨上是常事（连着点几个音），每次都喊的话，
    /// 收到的那一头会把整窗重画一遍，白画。
    /// </summary>
    public bool SetFocusedTrack(int trackIndex)
    {
        int target = _song.Tracks.Count == 0
            ? 0
            : Math.Clamp(trackIndex, 0, _song.Tracks.Count - 1);

        if (target == FocusedTrack) return false;

        FocusedTrack = target;
        return true;
    }

    /// <summary>
    /// 聚焦往上 / 往下走一条（<paramref name="delta"/> = ±1），<b>跳过收起来的那些</b>：
    /// 收起来的轨卷帘是藏着的，把高亮挪过去等于挪到一个看不见的地方。
    ///
    /// 这个方向上一条能落的轨都没有就**原地不动**（返回当前这一条）—— 包括走到头
    /// （和 <see cref="MoveSelection"/> 一样**不绕回去**：绕回去会让人以为自己按错了方向）。
    ///
    /// 折叠表由调用方给，控制器不存它：折叠是控件上的状态，只有控件那一处
    /// （见 <c>MainWindow.MutedTracks</c> 里为什么不在窗口里另存一份）。
    /// 表比轨数短时，缺的那些当没收起来。
    /// </summary>
    public int MoveFocusedTrack(int delta, IReadOnlyList<bool> collapsed)
    {
        if (delta == 0) return FocusedTrack;

        int step = Math.Sign(delta);
        for (int candidate = FocusedTrack + step;
             candidate >= 0 && candidate < _song.Tracks.Count;
             candidate += step)
        {
            if (candidate < collapsed.Count && collapsed[candidate]) continue;
            return FocusedTrack = candidate;
        }

        return FocusedTrack;
    }

    /// <summary>
    /// 第一条没收起来的轨，**全收起来时给 0**。
    ///
    /// 建控制器时的初始聚焦走它。换一首曲子时折叠本来就是空的，所以照常是 0；
    /// 它管的是另外两种情形：「折叠状态被留到了新控制器上」（于是第一条恰好是收起来的），
    /// 以及「一条轨都没展开」。后者给 0 是个明确的落点 —— 高亮总得落在某一条上，
    /// 一条都不落的话，用户按 Ctrl+↑ 会以为快捷键坏了。
    /// </summary>
    public static int FirstExpanded(IReadOnlyList<bool> collapsed)
    {
        for (int i = 0; i < collapsed.Count; i++)
            if (!collapsed[i]) return i;

        return 0;
    }

    // ==================== 区间查询 ====================

    /// <summary>
    /// 某条轨里**与这段区间相交**的音，按音符数组的顺序（也就是时间顺序）给出来。
    ///
    /// 判的是**相交**，不是「起点落在区间里」。用户框一段，要的是把盖住这段的音一起处理
    /// （最要紧的是空白处横拖删音），而一个从框左边伸进来、一直盖到框中间的长音，
    /// 只判起点的话会原地留着 —— 那恰恰是他框住它想干掉的东西。
    ///
    /// 区间取**左闭右开** <c>[startTick, endTick)</c>，右边缘跟着 <see cref="Note.EndTick"/>
    /// 的「不含」走：一个音「在 960 结束」的意思是它占 <c>[480, 960)</c>，
    /// 和一条从 960 起的框一丝都不重叠。这个取法还有个好处 —— 相邻的框不重不漏，
    /// <c>[a,b)</c> 加 <c>[b,c)</c> 正好是 <c>[a,c)</c>，将来要把一次长拖切成几段也算得对。
    ///
    /// <c>endTick &lt;= startTick</c> 是**空区间**，按集合的定义就该什么都不相交 ——
    /// 这不是特例，是兜底：空白处横拖删音时，「点了空白但没拖」是一次零长度的手势，
    /// 要是让它把光标底下那个音算进来，一次误点就删掉了一个音。
    ///
    /// 越界的轨下标返回空集而不抛，理由同 <see cref="SetSelection"/>：那不是错误，是「它不在了」。
    /// </summary>
    /// <param name="trackIndex">轨下标（0 起）。</param>
    /// <param name="startTick">区间起点（含）。</param>
    /// <param name="endTick">区间终点（不含）。</param>
    public IReadOnlyList<NoteRef> NotesInRange(int trackIndex, long startTick, long endTick)
    {
        if (trackIndex < 0 || trackIndex >= _song.Tracks.Count) return Array.Empty<NoteRef>();
        if (endTick <= startTick) return Array.Empty<NoteRef>();

        var notes = _song.Tracks[trackIndex].Notes;
        var hits = new List<NoteRef>();
        for (int i = 0; i < notes.Count; i++)
        {
            var note = notes[i];
            // 音符数组按起点升序（Track.Notes 的承诺，命中判定也吃这条），
            // 所以起点已经够到右边缘之后，后面全是更靠后的，直接收工
            if (note.StartTick >= endTick) break;
            if (note.EndTick <= startTick) continue;
            // 这里用下标只是因为**正在遍历数组**：出这道门的坐标是身份，不是位置
            hits.Add(new NoteRef(trackIndex, note.Id));
        }

        return hits;
    }

    // ==================== 静音 / 长度 ====================

    /// <summary>
    /// 换一份「哪几条轨不发声」的名单。折叠 / 展开一条轨走这条。
    ///
    /// **换的是整曲多少小节**，因为长度只按听得见的轨算（见 <see cref="AudibleLength"/>）。
    /// 于是三样派生读数一起重算：<see cref="BarCount"/>、<see cref="BarNoteCounts"/>、
    /// 以及跟着 <see cref="BarCount"/> 走的 <see cref="TotalTicks"/>（导航条和卷帘都读它）；
    /// 视图位置再钳一次 —— 曲子短了之后，原来停在末尾的视口会落到曲子外面。
    ///
    /// **选中与焦点轨不动**：它们是「哪些音」「哪条轨」，不是「曲子多长」。
    /// 展开回来时长度自然长回去，两边都不欠谁一次重算。
    ///
    /// 长度没变就**什么都不做** —— 这是常事（折叠一条本来就短的轨），
    /// 而 <see cref="CountNotesPerBar"/> 是整曲扫一遍音符，没必要为同一份数据白扫一次。
    /// </summary>
    public void SetMutedTracks(IReadOnlySet<(int TrackIndex, int Channel)>? mutedTracks)
    {
        int barCount = BarCountOf(mutedTracks);
        if (barCount == BarCount) return;

        BarCount = barCount;
        BarNoteCounts = CountNotesPerBar();
        SetViewStart(ViewStartTick);
    }

    private int BarCountOf(IReadOnlySet<(int TrackIndex, int Channel)>? mutedTracks) =>
        PianoRollGeometry.BarCount(AudibleLength.EndTick(_song, mutedTracks), TicksPerBar);

    // ==================== 视图位置 ====================

    /// <summary>把视图挪到某个 tick（自动夹在合法范围内）。</summary>
    public void SetViewStart(long tick) =>
        ViewStartTick = PianoRollGeometry.ClampViewStart(tick, TotalTicks, TicksPerBar);

    /// <summary>
    /// 跳到第 <paramref name="bar"/> 小节（**1 起**）。导航条、跳跃输入框走它。
    /// 越界的小节号夹到首尾，不报错 —— 用户输 999 的意思就是「去最后」。
    /// </summary>
    public void SeekBar(int bar) =>
        SetViewStart(PianoRollGeometry.TickOfBar(Math.Clamp(bar, 1, BarCount) - 1, TicksPerBar));

    /// <summary>跳到第 <paramref name="barZeroBased"/> 小节（0 起）。导航条拖出来的是这个。</summary>
    public void SeekBarZeroBased(int barZeroBased) => SeekBar(barZeroBased + 1);

    /// <summary>
    /// 第 <paramref name="barZeroBased"/> 小节的起始 tick，越界的小节号夹到首尾。
    /// 导航条和跳跃输入框都要「先拿到落点 tick，再把播放头也搬过去」，所以单独开一个。
    /// </summary>
    public long TickOfBarClamped(int barZeroBased) =>
        PianoRollGeometry.TickOfBar(Math.Clamp(barZeroBased, 0, BarCount - 1), TicksPerBar);

    /// <summary>
    /// 把这一小节摆到屏幕中间。
    ///
    /// 拖完导航条用它：光把小节对齐到左边缘的话，播放头一松手就贴在屏幕边上看不清（标注 3）。
    /// 摆到中间还有个好处 —— 播放中松手时红线本来就是往三分之一处走的，中转一下不别扭。
    /// </summary>
    public void CenterOnBar(int barZeroBased) =>
        SetViewStart(TickOfBarClamped(barZeroBased) - TicksVisible / 2);

    /// <summary>
    /// 播放时跟着播放头滚：把播放头放在屏幕偏左约三分之一处（留前瞻，wireframe 标注 4）。
    ///
    /// **只往前走，不回头。** 播放头退回三分之一线左边时视图不动 ——
    /// 否则每次都去追它，画面会来回蹭。
    /// </summary>
    /// <param name="playheadTick">播放头位置。</param>
    /// <param name="fraction">播放头停在屏幕的哪个位置，0.32 ≈ 偏左三分之一。</param>
    public void Follow(long playheadTick, double fraction)
    {
        double target = playheadTick - TicksVisible * fraction;
        if (target <= ViewStartTick) return;
        SetViewStart((long)Math.Round(target));
    }

    /// <summary>
    /// 把视图左边缘对齐到小节线。
    ///
    /// 停止播放时用它：你按停止就是要改东西，不该让你面对半截小节（wireframe 标注 4）。
    /// </summary>
    public void SnapViewToBar() =>
        SetViewStart(PianoRollGeometry.SnapToBar(ViewStartTick, TicksPerBar));

    // ==================== 几何 ====================

    /// <summary>某条轨在这个尺寸下的一屏几何。音域按这条轨自适应。</summary>
    public PianoRollGeometry.Viewport ViewportOf(int trackIndex, double width, double height)
    {
        var (low, high) = PitchRangeOf(trackIndex);
        return new PianoRollGeometry.Viewport(width, height, ViewStartTick, TicksPerBar, low, high);
    }

    /// <summary>
    /// 这条轨显示用的音高范围（含余量），只算一次。
    ///
    /// 越界的轨下标给一个中性音域而不抛：删光所有轨之后 <see cref="Song.Tracks"/> 是空的，
    /// 而界面手里那张刚建出来的控制器这一帧还会被问一次（重画那条轨），
    /// 那不是错误，是「谱面空了」。空轨与无轨给的是同一个音域 —— 两边都只是「没什么可显示」。
    /// </summary>
    public (int Low, int High) PitchRangeOf(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= _song.Tracks.Count) return EmptyPitchRange;
        if (_pitchRanges[trackIndex].Low >= 0) return _pitchRanges[trackIndex];

        var track = _song.Tracks[trackIndex];
        if (track.Notes.Count == 0)
        {
            _pitchRanges[trackIndex] = EmptyPitchRange;
            return _pitchRanges[trackIndex];
        }

        int min = int.MaxValue, max = int.MinValue;
        foreach (var note in track.Notes)
        {
            int pitch = note.Pitch + track.Transpose;
            if (pitch < min) min = pitch;
            if (pitch > max) max = pitch;
        }

        _pitchRanges[trackIndex] = PianoRollGeometry.FitPitchRange(min, max);
        return _pitchRanges[trackIndex];
    }

    /// <summary>
    /// 这条轨每个音「游戏弹不弹得出来」。下标与 <c>Track.Notes</c> 逐个对应。
    ///
    /// 判据**就是 <c>NoteMapper</c> 那一套**：基准八度取
    /// <c>AutoBaseOctave(该轨移调后的音高)</c>，然后看 <c>MappedNote.InRange</c>。
    /// 不另立一套规矩 —— 卷帘上灰掉的音，必须正好是演奏时会跳过的音，两边是同一个判断。
    /// 这个标记只影响画法（画成灰的），别的什么都不影响。
    /// </summary>
    public IReadOnlyList<bool> InRangeFlagsOf(int trackIndex)
    {
        // 越界的轨下标给空表：删光所有轨之后还会被问一次，见 PitchRangeOf 的说明。
        // 空表不会让调用方越界 —— 用它的地方（命中判定）本来就是 `i < flags.Count && flags[i]` 地取
        if (trackIndex < 0 || trackIndex >= _song.Tracks.Count) return Array.Empty<bool>();
        if (_inRange[trackIndex] is { } cached) return cached;

        var track = _song.Tracks[trackIndex];
        var pitches = new List<int>(track.Notes.Count);
        var raws = new List<RawNote>(track.Notes.Count);
        foreach (var note in track.Notes)
        {
            int pitch = note.Pitch + track.Transpose;
            pitches.Add(pitch);
            // 判定只看音高，时间和力度不参与 —— 所以这两个字段空着
            raws.Add(new RawNote { Pitch = pitch });
        }

        int baseOctave = NoteMapper.AutoBaseOctave(pitches);
        // 音高已经移调过了，这里再传一次 transpose 就变成移两遍
        var mapped = NoteMapper.Map(raws, transpose: 0, manualBaseOctave: baseOctave);

        var flags = new List<bool>(mapped.Notes.Count);
        foreach (var note in mapped.Notes) flags.Add(note.InRange);

        _inRange[trackIndex] = flags;
        return flags;
    }

    /// <summary>
    /// 命中判定：这根轨的这个点上有没有音，命中的是头、尾还是身体；
    /// <paramref name="note"/> 是命中那个音的身份坐标（<see cref="NoteRef"/>）。
    /// 没命中返回 <see cref="PianoRollGeometry.RollHit.None"/>。
    ///
    /// 09 的编辑命令收的都是 ref（<c>MoveNotes</c> / <c>SetNoteSpan</c> / <c>DeleteNotes</c>），
    /// 让界面自己拿下标拼一个出来，等于把「下标是从哪个数组来的」这件事又抄一遍。
    ///
    /// <b>从前这里还有一对方法</b>：一个给下标、一个给 ref，共用同一段循环，
    /// 各自的注释都写着「两者必须给同一个答案」。31 号工单把给下标的那一个删了 ——
    /// 坐标改成按身份寻址之后，「这个下标」在编辑之后就不再指着同一个音了，
    /// 留着它就等于留着一张嘴在说另一种地址（而且只有测试在用）。只剩一扇门，就没有「两处要对齐」这件事。
    ///
    /// 没命中时 <paramref name="note"/> 是 <c>(-1, None)</c>：一个**明确不存在的**坐标。
    /// 不返回 <c>default</c>，是因为 <c>default(NoteRef)</c> 正好是 <c>(0, 0)</c> ——
    /// 0 是 <see cref="NoteId.None"/>，看着也不像真的，但轨那一半的 0 是条真轨，
    /// 谁忘了判就会默默选中第一条轨上那个「没有身份」的位置。
    ///
    /// **多个音在屏幕上叠着的时候，给的是数组里靠前的那个**（也就是起点更早的那个）。
    /// 这不是「对」，只是这趟循环的顺序：按数组序碰到第一个命中的就返回。
    /// 要改成「取视觉上压在最上面的」，得先想清楚「画在最上面的」在同一个音高上
    /// 是不是真的更符合直觉（数组序至少是确定的、和导出的事件顺序一致）。
    /// </summary>
    public PianoRollGeometry.RollHit HitTestRef(
        int trackIndex, in PianoRollGeometry.Viewport viewport, double x, double y, out NoteRef note)
    {
        note = new NoteRef(-1, NoteId.None);
        // 越界的轨下标当没命中：删光所有轨之后还会被问一次，见 PitchRangeOf 的说明
        if (trackIndex < 0 || trackIndex >= _song.Tracks.Count) return PianoRollGeometry.RollHit.None;

        var track = _song.Tracks[trackIndex];
        var flags = InRangeFlagsOf(trackIndex);

        for (int i = 0; i < track.Notes.Count; i++)
        {
            var model = track.Notes[i];
            // 先用 tick 粗筛：一屏最多几十个音，但一首曲子可能上万，一个一个建块太浪费
            if (model.StartTick > viewport.ViewStartTick + viewport.TicksVisible) break;
            if (model.EndTick < viewport.ViewStartTick) continue;

            // 这里按下标建块只是因为**正在遍历数组**：出的那门是身份，不是位置
            var box = PianoRollGeometry.BoxOf(
                viewport, model.Id, model.StartTick, model.LengthTicks, model.Pitch + track.Transpose,
                i < flags.Count && flags[i]);

            var hit = PianoRollGeometry.HitTest(box, x, y);
            if (hit == PianoRollGeometry.RollHit.None) continue;

            note = new NoteRef(trackIndex, model.Id);
            return hit;
        }

        return PianoRollGeometry.RollHit.None;
    }

    // ==================== 读数条 / 键盘定位 ====================

    /// <summary>读数条要的那几个数：某个音符在谱面上的位置。</summary>
    /// <param name="Track">轨下标（0 起）。</param>
    /// <param name="Note">音的身份（<see cref="Note.Id"/>）。<b>不是下标</b> —— 读数条要问的是
    /// 「我手上这个音现在什么样」，而一个音在一次编辑之后可能已经换了位置。</param>
    /// <param name="Pitch">**移调之后**的音高 —— 和卷帘上看到、耳朵听到的是同一个。</param>
    /// <param name="Bar">第几小节（1 起）。</param>
    /// <param name="BeatInBar">小节内第几拍（1 起，带小数）。</param>
    /// <param name="LengthBeats">时值，单位拍。</param>
    public readonly record struct NoteInfo(
        int Track, NoteId Note, int Pitch, int Bar, double BeatInBar, double LengthBeats);

    /// <summary>
    /// 把某个音符描述出来。认不出来返回 null —— 轨下标越界、或者这条轨上没有这个号
    /// （悬停时音符刚被删掉、轨刚被换掉就会碰上）。
    /// </summary>
    public NoteInfo? Describe(int trackIndex, NoteId noteId)
    {
        if (trackIndex < 0 || trackIndex >= _song.Tracks.Count) return null;
        var track = _song.Tracks[trackIndex];
        int at = IndexOfId(track, noteId);
        if (at < 0) return null;

        var note = track.Notes[at];
        long barTicks = TicksPerBar;
        long intoBar = note.StartTick - PianoRollGeometry.TickOfBar(
            PianoRollGeometry.BarAtTick(note.StartTick, barTicks), barTicks);

        return new NoteInfo(
            trackIndex,
            note.Id,
            note.Pitch + track.Transpose,
            PianoRollGeometry.BarAtTick(note.StartTick, barTicks) + 1,
            Format.Beats(intoBar, _ticksPerQuarterNote) + 1,
            Format.Beats(note.LengthTicks, _ticksPerQuarterNote));
    }

    /// <summary>当前选中的音符描述。没选中是 null（选中集里的坐标认不出来时也是）。</summary>
    public NoteInfo? DescribeSelection() =>
        Selection is { } s ? Describe(s.Track, s.Note) : null;

    /// <summary>
    /// 在**焦点轨**的音符之间前后跳一个（<paramref name="delta"/> = ±1）。
    ///
    /// 这一张只定位，不移动 —— 方向键选中它、把视图带过去，
    /// 到这条轨的头尾就停住（不绕回去：绕回去会让人以为自己按错了方向）。
    /// 返回新的选中项；这条轨一个音都没有时返回 null。
    ///
    /// <b>只在焦点轨里走</b>（<see cref="FocusedTrack"/>，Ctrl+↑/↓ 换的那一条）。
    /// 07 那版是在**所有轨**的音符之间跳，一趟走下来会莫名其妙换到别的轨上；
    /// 而「上一个 / 下一个」这种手势说的是「在这一条轨上」，跨轨是另一件事。
    ///
    /// 顺序见 <see cref="BuildNavigationOrder"/>（时间优先，同刻从上到下）。
    ///
    /// <b>焦点轨和选中集可以不一致</b>：Ctrl+↑/↓ 换轨**不动选中集**（那是刻意的，
    /// 有测试钉着），于是会出现焦点在轨 1、选中的音在轨 3。那时候从**这条轨的开头重新起算**
    /// —— 两个方向都落在这一轨的第一个音上（Ctrl+← 和 Ctrl+→ 都读作「回到这条轨的开头」）。
    /// 这和「一个音都没选中」时是同一支，不是特例。
    ///
    /// 横向上视图跟着走（左边缘对齐到它所在的小节）；纵向上哪条轨归调用方
    /// （窗口那边 <c>Reveal</c> 把落点滚进视野）—— 这里已经保证落点就在焦点轨上了。
    ///
    /// **跳到哪儿就是只选中那一个**：这是「换个音看看」，不是「再加上一个」。
    /// 手上的一串选中（框选出来的一组）在这里被收掉，否则主选中换了地方、
    /// 选中集还是旧的那一组，两边指的就不是一回事了。
    /// </summary>
    public NoteInfo? MoveSelection(int delta)
    {
        if (delta == 0) return DescribeSelection();
        if (FocusedTrack < 0 || FocusedTrack >= _navigationOrder.Length) return null;

        var order = _navigationOrder[FocusedTrack];
        if (order.Count == 0) return null;

        // 「此刻在哪」只认**焦点轨上的**那个选中音。选中音在别的轨上时当没选中（current = -1），
        // 于是 next 落在第一个音上：-1 + 1 = 0 正好是它，-1 - 1 = -2 被下面那道夹取拉回 0。
        // 比的是**身份**（NoteRef 里那一半就是它），所以选中那个音后来被挪到数组别处、
        // 或者它前面插进来一个新音，这里照样认得它。
        int current = Selection is { } s && s.Track == FocusedTrack
            ? order.IndexOf(s.Note)
            : -1;

        int next = Math.Clamp(current + delta, 0, order.Count - 1);
        // 真正站在这一轨的某个音上、而且已经在头 / 尾 —— 原地不动。
        // 这一句不能只看 next == current：current 是 -1 时 next 也会等于 current（空不了，
        // 但 order.Count 为 1 时 next = 0 ≠ -1），要紧的是别把「刚从别的轨过来」当成「到头了」
        if (next == current && current >= 0) return DescribeSelection();

        var id = order[next];
        SelectOnly(new NoteRef(FocusedTrack, id));

        // 定位表就是照着这条轨的音符建的，所以这个号一定找得到 —— 控制器活着的这一阵
        // 手上这份曲子不会换（见类注释：命令换个引用就重建控制器）
        var track = _song.Tracks[FocusedTrack];
        long startTick = track.Notes[IndexOfId(track, id)].StartTick;
        // 视图左边缘对齐到这个音所在的小节 —— 横向「滚进视野」就是这一步
        SetViewStart(PianoRollGeometry.TickOfBar(
            PianoRollGeometry.BarAtTick(startTick, TicksPerBar), TicksPerBar));

        return DescribeSelection();
    }

    /// <summary>
    /// 把一组音的**整组位移**夹到合法范围内，返回真正能生效的那个增量。
    ///
    /// <b>这是全程序唯一一份「整组一起夹」的算法。</b>三条路都走它：
    /// <c>SongEditor.MoveNotes</c> 是规矩的出处，界面拖动那一路拿它算预览
    /// （预览画到命令去不了的地方，松手那一下整块会跳回来一次），
    /// 窗口的方向键微调拿它算「挪完的新位置」—— 微调没有拖动那个「先夹再发」的前置，
    /// 不在这儿夹一次的话，顶到边界那一下算出来的新位置是不存在的。
    /// （从前这里跟着一句「就会把选中集弄丢」—— 那是按值认音那套镜像的毛病，它已经删了。
    /// 夹取这一步本身照旧要，理由换成上面那条：预览 / 微调算出来的位置得真能落下去。）
    ///
    /// 规矩和命令那边一模一样：时间不挪到 0 之前、音高出不了 0..127，
    /// 两头都按**整组最小的那个可挪量**缩 —— 形状保住，
    /// 不是把每个音各自夹回去压成一摞（那样相对位置就没了）。
    ///
    /// 认不出的 <see cref="NoteRef"/>（轨下标越界，或者这条轨上没有这个号）当它不存在；
    /// 一个都认不出时原样返回 —— 空组本来就没得夹。
    /// </summary>
    public (long DeltaTicks, int DeltaPitch) ClampMoveDelta(
        IReadOnlyList<NoteRef> notes, long deltaTicks, int deltaPitch)
    {
        long minStart = long.MaxValue;
        int minPitch = int.MaxValue, maxPitch = int.MinValue;

        foreach (var reference in notes)
        {
            if (!IsValidNote(reference)) continue;
            var track = _song.Tracks[reference.Track];
            var note = track.Notes[IndexOfId(track, reference.Id)];
            if (note.StartTick < minStart) minStart = note.StartTick;
            if (note.Pitch < minPitch) minPitch = note.Pitch;
            if (note.Pitch > maxPitch) maxPitch = note.Pitch;
        }

        if (minStart == long.MaxValue) return (deltaTicks, deltaPitch);

        // 这里要的是「最大能挪多远」（一个增量），命令那边要的是「挪完之后落在哪个 tick」
        // （一个位置）—— 问的不是同一件事，所以这边比门槛、那边做饱和加法。
        //
        // 单说溢出：SaturatingAdd 本身**不会**绕圈（它就是为这个写的），
        // 这个式子里唯一会绕的是 -minStart，而那只在 minStart == long.MinValue 时发生
        //（绕回自己，比较恒为假，这道夹取整个失效）。真要有那么一个音，得先有一份
        // StartTick 是 long.MinValue 的谱面 —— 两条导入路径都给不出来：工程文件那条
        // 显式拒负数（Converters.NoteConverter），MIDI 那条的 tick 是从非负增量累加出来的。
        // 所以这里不为它加一道分支：挡的是一个谁也造不出来的值。
        if (deltaTicks < -minStart) deltaTicks = -minStart;
        if (deltaPitch < -minPitch) deltaPitch = -minPitch;
        if (deltaPitch > 127 - maxPitch) deltaPitch = 127 - maxPitch;

        return (deltaTicks, deltaPitch);
    }

    // ==================== 内部 ====================

    /// <summary>
    /// 每条轨按**定位顺序**排好的音符身份，<see cref="MoveSelection"/> 走它。
    ///
    /// 顺序是「时间优先」：起点升序，**同一个起点上按音高降序**。降序不是随便挑的 ——
    /// 卷帘上高音画在上头，降序读出来才是「先上到下、再左到右」，
    /// 和眼睛扫一行谱的方向一致（升序会从下往上走）。
    ///
    /// <b>表里装的是 <see cref="NoteId"/>，不是 <see cref="Note"/>。</b>看着多绕一手，
    /// 但换成音符本身会踩一个很隐蔽的坑：<see cref="Note.Equals"/> 刻意不比 <see cref="Note.Id"/>
    /// （见那边的说明，S1 缝要的逐字段相等），于是 <c>order.IndexOf(某个音)</c> 会把
    /// **内容恰好相同的另一个音**认成当前这个 —— 两个音都是 C4、都从 480 起、都一拍长的时候，
    /// 按方向键会从第二个音跳回第一个音上。这正是这张工单要消灭的那类错认。
    ///
    /// <b>一条轨一张表，不是一条大表。</b>定位只在**焦点轨**里走、不跨轨（见
    /// <see cref="MoveSelection"/>），所以要的是「这条轨的下一个音」，
    /// 「所有轨的下一个音」是另一个问题（换轨是 Ctrl+↑/↓）。
    ///
    /// 顺序必须是**确定的**，否则同一份谱子按两次方向键走到的地方可能不一样：
    /// <c>OrderBy</c> / <c>ThenByDescending</c> 是稳定排序，而输入本来就是音符数组的次序，
    /// 于是完全重复的音（同刻、同音高）之间也按原次序站定 —— 这是最后那一级的兜底，
    /// 不用再补一个「然后按下标」。
    ///
    /// <see cref="Track.Notes"/> 本来就承诺起点升序，所以「起点」那一级看着是白排的；
    /// 留着它是因为**这条顺序的依据是音符自己、不是数组碰巧怎么排的** ——
    /// 拿掉它，这里的正确性就变成依赖别人一条不变量，而那正是最难发现的那类回归。
    /// </summary>
    private List<NoteId>[] BuildNavigationOrder()
    {
        var order = new List<NoteId>[_song.Tracks.Count];

        for (int t = 0; t < _song.Tracks.Count; t++)
        {
            order[t] = _song.Tracks[t].Notes
                .OrderBy(n => n.StartTick)
                .ThenByDescending(n => n.Pitch)
                .Select(n => n.Id)
                .ToList();
        }

        return order;
    }

    private int[] CountNotesPerBar()
    {
        var counts = new int[BarCount];
        foreach (var track in _song.Tracks)
        {
            foreach (var note in track.Notes)
            {
                int bar = PianoRollGeometry.BarAtTick(note.StartTick, TicksPerBar);
                if (bar >= 0 && bar < counts.Length) counts[bar]++;
            }
        }

        int max = 0;
        foreach (int count in counts)
            if (count > max) max = count;
        MaxBarNoteCount = max;

        return counts;
    }
}
