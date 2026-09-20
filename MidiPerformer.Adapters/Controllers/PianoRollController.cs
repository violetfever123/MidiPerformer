using MidiPerformer.Adapters.Presenters;
using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Perform.Repertoire;
using MidiPerformer.Core.UseCases.Preview;

namespace MidiPerformer.Adapters.Controllers;

/// <summary>
/// 卷帘的控制器：像素 ⇄ (音高, tick) 换算、命中判定、视图位置、选中。
/// 它不认识 Avalonia，视口由调用方按控件宽高现算（<see cref="ViewportOf"/>），算出来的是一份纯数据，
/// 于是命中判定不用起窗口就能测。这一张是只读的：只有「看哪儿、选中谁、聚焦在哪条轨上」。
/// </summary>
public sealed class PianoRollController
{
    private readonly Song _song;

    /// <summary>每条轨「按键盘定位的顺序」排好的音符身份（见 <see cref="BuildNavigationOrder"/>）。</summary>
    private readonly List<NoteId>[] _navigationOrder;

    private readonly List<bool>[] _inRange;
    private readonly (int Low, int High)[] _pitchRanges;
    private readonly int _ticksPerQuarterNote;

    /// <summary>选中的一组音。顺序有意义：最后一个就是主选中（见 <see cref="Selection"/>）。</summary>
    private readonly List<NoteRef> _selected = new();

    /// <summary><see cref="_selected"/> 的只读活视图，建一次就够。</summary>
    private readonly IReadOnlyList<NoteRef> _selectedView;

    /// <summary>没有这条轨（曲子被删光了轨）时给的音域。空轨用的也是它。</summary>
    private static readonly (int Low, int High) EmptyPitchRange = PianoRollGeometry.FitPitchRange(60, 60);

    /// <summary>按曲子的节拍网格建一个卷帘控制器。视图位置从曲子开头开始。</summary>
    /// <param name="mutedTracks">
    /// 此刻不收声的那几条轨（= 折叠起来的那几条），按 <c>(轨块号, 声道)</c> 给。
    /// 它决定整曲多少小节：收起来的轨不算长度（见 <see cref="AudibleLength"/>）。
    /// 不给 = 一条都不静音。之后名单变了走 <see cref="SetMutedTracks"/>，控制器不另存一份。
    /// </param>
    public PianoRollController(Song song, IReadOnlySet<(int TrackIndex, int Channel)>? mutedTracks = null)
    {
        _song = song;
        TicksPerBar = PianoRollGeometry.BarTicks(song.TempoMap);
        _ticksPerQuarterNote = Math.Max(1, song.TempoMap.Division.TicksPerQuarterNote);
        BarCount = BarCountOf(mutedTracks);

        _selectedView = _selected.AsReadOnly();
        GridTicks = PianoRollGeometry.GridTicks(song.TempoMap);

        // 初始聚焦在第一条；窗口建完控制器之后会按折叠表现改一次（见 FirstExpanded）
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

    /// <summary>吸附网格多少 tick（一个十六分音符），构造时算一次。</summary>
    public long GridTicks { get; }

    /// <summary>整曲多少小节，只算没被静音的轨（见 <see cref="SetMutedTracks"/>）。视图、导航条、键盘定位都以它为上界。</summary>
    public int BarCount { get; private set; }

    /// <summary>一屏跨多少 tick —— 固定 4 小节，界面上没有缩放入口。</summary>
    public long TicksVisible => TicksPerBar * PianoRollGeometry.BarsVisible;

    /// <summary>整曲 tick 跨度，按小节向上取整。</summary>
    public long TotalTicks => TicksPerBar * BarCount;

    /// <summary>
    /// 每小节的音符数（多轨合计，只数在 <see cref="BarCount"/> 之内的小节）。目前只有测试在读它。
    /// 表长跟着 <see cref="BarCount"/> 走：曲子被静音名单缩短之后，落在新曲子外面的音不进这张表。
    /// </summary>
    public IReadOnlyList<int> BarNoteCounts { get; private set; }

    /// <summary>上面那张表里最密的那个小节有多少个音。目前只有测试在读它。</summary>
    public int MaxBarNoteCount { get; private set; }

    /// <summary>当前视图左边缘对应的 tick。永远落在合法范围内（见 <see cref="PianoRollGeometry.ClampViewStart"/>）。</summary>
    public long ViewStartTick { get; private set; }

    /// <summary>视图左边缘落在第几小节（0 起）。目前只有测试在读它。</summary>
    public int ViewStartBar => PianoRollGeometry.BarAtTick(ViewStartTick, TicksPerBar);

    /// <summary>
    /// 主选中（轨下标, 音的身份）。读数条与滚动定位只认这一个；没选中是 null。
    /// 它永远是 <see cref="SelectedNotes"/> 里最后加进去的那一个（用户最后点的那一个），不是另存的第二份状态。
    /// 音那一半是身份不是下标（见 <see cref="NoteRef"/>）。
    /// </summary>
    public (int Track, NoteId Note)? Selection
        => _selected.Count > 0 ? (_selected[^1].Track, _selected[^1].Id) : null;

    /// <summary>
    /// 选中的一组音：框选一组、一组一起挪，命令那边收的就是这一串。
    /// 返回的是活视图，不是每次调用现拷一份 —— 一次重画要读它好几遍。
    /// </summary>
    public IReadOnlyList<NoteRef> SelectedNotes => _selectedView;

    /// <summary>某个 tick 落在第几小节（1 起）。走带条那个「位置」用它。</summary>
    public int BarOfTick(long tick) => PianoRollGeometry.BarAtTick(tick, TicksPerBar) + 1;

    /// <summary>
    /// 清空再选一个（在卷帘上点某个音用它）。
    /// 认不出的 ref 会被丢掉，于是等于清空 —— 不抛：曲子刚被换掉、轨刚被删掉时会碰上。
    /// </summary>
    public void SelectOnly(NoteRef note)
    {
        _selected.Clear();
        if (IsValidNote(note)) _selected.Add(note);
    }

    /// <summary>
    /// 整体替换选中集：框选、以及每次编辑之后把选中集放回新控制器上都用它。
    /// 认不出的 ref 丢掉、重复的只留一个（这一串要原样交给 <c>MoveNotes</c>，同一个音出现两次会被挪两倍距离）。
    /// 顺序原样保留，于是主选中仍然是传进来的最后一个。
    /// 注意：身份只在一份曲子里有意义，换一首曲子时不许把旧坐标带过来。
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

    /// <summary>加一个进来（Shift 点选）。已经在里面就什么都不做，否则主选中会被同一个音顶掉。</summary>
    public void ExtendSelection(NoteRef note)
    {
        if (!IsValidNote(note) || _selected.Contains(note)) return;
        _selected.Add(note);
    }

    /// <summary>清空选中集（主选中跟着变 null —— 它是这一串的尾巴）。</summary>
    public void ClearSelection() => _selected.Clear();

    /// <summary>这个音在不在选中集里。线性扫一遍，没建哈希表：框选一次也就几十个，而且顺序得留着。</summary>
    public bool IsSelected(NoteRef note) => _selected.Contains(note);

    /// <summary>这个 ref 指向的音在不在这份曲子里。选中集里的每个口子都要过它一道。</summary>
    private bool IsValidNote(NoteRef note) =>
        note.Track >= 0 && note.Track < _song.Tracks.Count
        && IndexOfId(_song.Tracks[note.Track], note.Id) >= 0;

    /// <summary>
    /// 这条轨上身份是 <paramref name="id"/> 的音在数组里的位置；没有就是 -1。
    /// 线性扫一遍就够了；位置只在控制器内部用，对外一律是身份。
    /// </summary>
    private int IndexOfId(Track track, NoteId id)
    {
        for (int i = 0; i < track.Notes.Count; i++)
            if (track.Notes[i].Id == id) return i;

        return -1;
    }

    // ==================== 聚焦轨 ====================

    /// <summary>
    /// 聚焦轨：轨道头高亮的那一条，Ctrl+↑/↓ 走的也是它。是个下标。
    /// 它不改变听到什么 —— 出不出声由折叠决定，聚焦只管「手现在搭在哪条轨上」。
    /// 下标在一次删轨之后会整体前移，所以换控制器之后不能照着旧下标放回来，得先换算成轨的身份。
    /// </summary>
    public int FocusedTrack { get; private set; }

    /// <summary>
    /// 把聚焦挪到第 <paramref name="trackIndex"/> 条轨上。越界夹进范围，不抛；一条轨都没有时落在 0。
    /// 返回焦点是不是真的挪了 —— 「点音符 → 焦点跟随」那条路拿它决定要不要喊一声重画。
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
    /// 聚焦往上 / 往下走一条（<paramref name="delta"/> = ±1），跳过收起来的那些：
    /// 收起来的轨卷帘是藏着的，把高亮挪过去等于挪到一个看不见的地方。
    /// 这个方向上一条能落的轨都没有就原地不动（走到头也不绕回去）。
    /// 折叠表由调用方给，控制器不存它；表比轨数短时，缺的那些当没收起来。
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

    /// <summary>第一条没收起来的轨，全收起来时给 0。建控制器时的初始聚焦走它。</summary>
    public static int FirstExpanded(IReadOnlyList<bool> collapsed)
    {
        for (int i = 0; i < collapsed.Count; i++)
            if (!collapsed[i]) return i;

        return 0;
    }

    // ==================== 区间查询 ====================

    /// <summary>
    /// 某条轨里与区间 <c>[startTick, endTick)</c> 相交的音，按音符数组的顺序（也就是时间顺序）给出来。
    /// 判的是相交，不是「起点落在区间里」：一个从框左边伸进来、盖到框中间的长音也该被框住。
    /// 空区间（<c>endTick &lt;= startTick</c>）什么都不相交；越界的轨下标返回空集而不抛。
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
            // 音符数组按起点升序，所以起点已经够到右边缘之后，后面全是更靠后的，直接收工
            if (note.StartTick >= endTick) break;
            if (note.EndTick <= startTick) continue;
            // 这里用下标只是因为正在遍历数组：出这道门的坐标是身份，不是位置
            hits.Add(new NoteRef(trackIndex, note.Id));
        }

        return hits;
    }

    /// <summary>
    /// 本轨上选中的那些音整体盖住的那一段时间，两端吸到格线（给「抽掉一段」预填用）。
    /// 只认本轨的音：选中集是全局的，而抽掉一段一次只抽一条轨。
    /// 返回 null 有两种情形：本轨一个音都没选中，或者选中的音短到吸完只剩零宽（比一格还短的音）。
    /// </summary>
    public (long Start, long End)? SelectionSpan(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= _song.Tracks.Count) return null;

        long minStart = long.MaxValue, maxEnd = long.MinValue;
        foreach (var reference in _selected)
        {
            if (reference.Track != trackIndex || !IsValidNote(reference)) continue;
            var track = _song.Tracks[reference.Track];
            var note = track.Notes[IndexOfId(track, reference.Id)];
            if (note.StartTick < minStart) minStart = note.StartTick;
            if (note.EndTick > maxEnd) maxEnd = note.EndTick;
        }

        if (minStart == long.MaxValue) return null;
        return PianoRollGeometry.SpanOf(minStart, maxEnd, GridTicks);
    }

    // ==================== 静音 / 长度 ====================
    /// <summary>
    /// 换一份「哪几条轨不发声」的名单。折叠 / 展开一条轨走这条。
    /// 长度只按听得见的轨算（见 <see cref="AudibleLength"/>），所以要重算 <see cref="BarCount"/>、
    /// <see cref="BarNoteCounts"/> 和跟着走的 <see cref="TotalTicks"/>，并把视图位置再钳一次。
    /// 选中与焦点轨不动。长度没变就什么都不做。
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

    /// <summary>跳到第 <paramref name="bar"/> 小节（1 起）。导航条、跳跃输入框走它。越界的小节号夹到首尾，不报错。</summary>
    public void SeekBar(int bar) =>
        SetViewStart(PianoRollGeometry.TickOfBar(Math.Clamp(bar, 1, BarCount) - 1, TicksPerBar));

    /// <summary>跳到第 <paramref name="barZeroBased"/> 小节（0 起）。导航条拖出来的是这个。</summary>
    public void SeekBarZeroBased(int barZeroBased) => SeekBar(barZeroBased + 1);

    /// <summary>第 <paramref name="barZeroBased"/> 小节的起始 tick，越界的小节号夹到首尾。</summary>
    public long TickOfBarClamped(int barZeroBased) =>
        PianoRollGeometry.TickOfBar(Math.Clamp(barZeroBased, 0, BarCount - 1), TicksPerBar);

    /// <summary>把这一小节摆到屏幕中间（拖完导航条用它：对齐左边缘的话播放头松手就贴在屏幕边上）。</summary>
    public void CenterOnBar(int barZeroBased) =>
        SetViewStart(TickOfBarClamped(barZeroBased) - TicksVisible / 2);

    /// <summary>
    /// 播放时跟着播放头滚：把播放头放在屏幕偏左约三分之一处（留前瞻）。
    /// 只往前走，不回头 —— 否则每次都去追它，画面会来回蹭。
    /// </summary>
    /// <param name="playheadTick">播放头位置。</param>
    /// <param name="fraction">播放头停在屏幕的哪个位置，0.32 ≈ 偏左三分之一。</param>
    public void Follow(long playheadTick, double fraction)
    {
        double target = playheadTick - TicksVisible * fraction;
        if (target <= ViewStartTick) return;
        SetViewStart((long)Math.Round(target));
    }

    /// <summary>把视图左边缘对齐到小节线（停止播放时用它，免得你面对半截小节）。</summary>
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
    /// 越界的轨下标给一个中性音域而不抛：谱面空了之后界面这一帧还会被问一次。
    /// 空轨与无轨给的是同一个音域 —— 两边都只是「没什么可显示」。
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
    /// 判据就是 <c>NoteMapper</c> 那一套（基准八度取 <c>AutoBaseOctave(该轨移调后的音高)</c>，
    /// 然后看 <c>MappedNote.InRange</c>）—— 两边必须是同一个判断。这个标记只影响画法。
    /// </summary>
    public IReadOnlyList<bool> InRangeFlagsOf(int trackIndex)
    {
        // 越界的轨下标给空表；用它的地方（命中判定）本来就是 `i < flags.Count && flags[i]` 地取
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
    /// 没命中返回 <see cref="PianoRollGeometry.RollHit.None"/>，此时 <paramref name="note"/> 是
    /// <c>(-1, None)</c> —— 一个明确不存在的坐标，不返回 <c>default</c>（那正好是 <c>(0, 0)</c>，
    /// 0 是 <see cref="NoteId.None"/>，但轨那一半的 0 是条真轨）。
    /// 多个音在屏幕上叠着的时候，给的是数组里靠前的那个（也就是起点更早的那个）。
    /// </summary>
    public PianoRollGeometry.RollHit HitTestRef(
        int trackIndex, in PianoRollGeometry.Viewport viewport, double x, double y, out NoteRef note)
    {
        note = new NoteRef(-1, NoteId.None);
        // 越界的轨下标当没命中：谱面空了之后还会被问一次
        if (trackIndex < 0 || trackIndex >= _song.Tracks.Count) return PianoRollGeometry.RollHit.None;

        var track = _song.Tracks[trackIndex];
        var flags = InRangeFlagsOf(trackIndex);

        for (int i = 0; i < track.Notes.Count; i++)
        {
            var model = track.Notes[i];
            // 先用 tick 粗筛：一屏最多几十个音，但一首曲子可能上万，一个一个建块太浪费
            if (model.StartTick > viewport.ViewStartTick + viewport.TicksVisible) break;
            if (model.EndTick < viewport.ViewStartTick) continue;

            // 这里按下标建块只是因为正在遍历数组：出的那门是身份，不是位置
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
    /// <param name="Note">音的身份（<see cref="Note.Id"/>），不是下标 —— 一个音在一次编辑之后可能已经换了位置。</param>
    /// <param name="Pitch">移调之后的音高 —— 和卷帘上看到、耳朵听到的是同一个。</param>
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
    /// 在焦点轨的音符之间前后跳一个（<paramref name="delta"/> = ±1）。这一张只定位，不移动。
    /// 到这条轨的头尾就停住（不绕回去）。返回新的选中项；这条轨一个音都没有时返回 null。
    /// 只在 <see cref="FocusedTrack"/> 里走，不跨轨；顺序见 <see cref="BuildNavigationOrder"/>。
    /// 焦点轨和选中集可以不一致，那时从这条轨的开头重新起算；
    /// 跳到哪儿就是只选中那一个，手上的一串选中在这里被收掉。
    /// 横向上视图跟着走（左边缘对齐到它所在的小节）；纵向上哪条轨归调用方。
    /// </summary>
    public NoteInfo? MoveSelection(int delta)
    {
        if (delta == 0) return DescribeSelection();
        if (FocusedTrack < 0 || FocusedTrack >= _navigationOrder.Length) return null;

        var order = _navigationOrder[FocusedTrack];
        if (order.Count == 0) return null;

        // 「此刻在哪」只认焦点轨上的那个选中音；选中音在别的轨上时当没选中（current = -1），
        // 于是 next 落在第一个音上（-1 + 1 = 0，-1 - 1 被下面的夹取拉回 0）。比的是身份，不是位置。
        int current = Selection is { } s && s.Track == FocusedTrack
            ? order.IndexOf(s.Note)
            : -1;

        int next = Math.Clamp(current + delta, 0, order.Count - 1);
        // 真正站在这一轨的某个音上、而且已经在头 / 尾 —— 原地不动。
        // 不能只看 next == current：current 为 -1（刚从别的轨过来）时也算等于，那不是「到头了」
        if (next == current && current >= 0) return DescribeSelection();

        var id = order[next];
        SelectOnly(new NoteRef(FocusedTrack, id));

        // 定位表就是照着这条轨的音符建的，所以这个号一定找得到
        var track = _song.Tracks[FocusedTrack];
        long startTick = track.Notes[IndexOfId(track, id)].StartTick;
        // 视图左边缘对齐到这个音所在的小节 —— 横向「滚进视野」就是这一步
        SetViewStart(PianoRollGeometry.TickOfBar(
            PianoRollGeometry.BarAtTick(startTick, TicksPerBar), TicksPerBar));

        return DescribeSelection();
    }

    /// <summary>
    /// 把一组音的整组位移夹到合法范围内，返回真正能生效的那个增量。
    /// 规矩和命令那边一模一样：时间不挪到 0 之前、音高出不了 0..127，两头都按整组最小的那个可挪量缩
    /// —— 形状保住，不是把每个音各自夹回去压成一摞。认不出的 <see cref="NoteRef"/> 当它不存在，
    /// 一个都认不出时原样返回。
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

        // 单说溢出：这个式子里唯一会绕的是 -minStart，而 StartTick 为 long.MinValue 的谱面
        // 两条导入路径都给不出来（工程文件那条拒负数，MIDI 那条的 tick 由非负增量累加），
        // 所以不为它加一道分支。这边比的是门槛，命令那边要的是「挪完之后落在哪个 tick」。
        if (deltaTicks < -minStart) deltaTicks = -minStart;
        if (deltaPitch < -minPitch) deltaPitch = -minPitch;
        if (deltaPitch > 127 - maxPitch) deltaPitch = 127 - maxPitch;

        return (deltaTicks, deltaPitch);
    }

    // ==================== 内部 ====================

    /// <summary>
    /// 每条轨按定位顺序排好的音符身份，<see cref="MoveSelection"/> 走它。
    /// 顺序是「时间优先」：起点升序，同一个起点上按音高降序（卷帘上高音画在上头，
    /// 降序读出来才是先上到下、再左到右）。一条轨一张表，因为定位只在焦点轨里走、不跨轨。
    /// 表里装的是 <see cref="NoteId"/>，不是 <see cref="Note"/>：<see cref="Note.Equals"/> 刻意不比
    /// <see cref="Note.Id"/>，拿音符去查会把内容恰好相同的另一个音认成当前这个。
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
