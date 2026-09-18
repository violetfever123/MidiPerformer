using MidiPerformer.Adapters.Presenters;
using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Perform.Repertoire;

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
/// 这一张是**只读**的：没有任何改谱面的方法，只有「看哪儿、选中谁」。
/// 编辑命令从它旁边过去 —— 08 只借了 <see cref="RestoreSelection"/> 这一个口子
/// （编辑之后控制器要重建，选中得能放回去），改谱面那些归 09。
/// </summary>
public sealed class PianoRollController
{
    private readonly Song _song;
    private readonly List<(int Track, int Note)> _ordered = new();
    private readonly List<bool>[] _inRange;
    private readonly (int Low, int High)[] _pitchRanges;
    private readonly int _ticksPerQuarterNote;

    /// <summary>按曲子的节拍网格建一个卷帘控制器。视图位置从曲子开头开始。</summary>
    public PianoRollController(Song song)
    {
        _song = song;
        TicksPerBar = PianoRollGeometry.BarTicks(song.TempoMap);
        _ticksPerQuarterNote = Math.Max(1, song.TempoMap.Division.TicksPerQuarterNote);
        BarCount = PianoRollGeometry.BarCount(song.EndTick, TicksPerBar);

        _inRange = new List<bool>[song.Tracks.Count];
        _pitchRanges = new (int, int)[song.Tracks.Count];
        // (0, 0) 是个合法音域，当不了「还没算」的哨兵 —— 显式填一个不可能的 Low
        for (int i = 0; i < _pitchRanges.Length; i++) _pitchRanges[i] = (-1, -1);
        BarNoteCounts = CountNotesPerBar();

        BuildTimeOrder();
    }

    public Song Song => _song;

    /// <summary>一个小节多少 tick（按第一个拍号）。</summary>
    public long TicksPerBar { get; }

    /// <summary>整曲多少小节。视图、导航条、键盘定位都以它为上界。</summary>
    public int BarCount { get; }

    /// <summary>一屏跨多少 tick —— 固定 4 小节，界面上没有缩放入口。</summary>
    public long TicksVisible => TicksPerBar * PianoRollGeometry.BarsVisible;

    /// <summary>整曲 tick 跨度，按小节向上取整。</summary>
    public long TotalTicks => TicksPerBar * BarCount;

    /// <summary>每小节的音符数（多轨合计）—— 导航条的柱子高度就是它。</summary>
    public IReadOnlyList<int> BarNoteCounts { get; }

    /// <summary>最密的那个小节有多少个音。导航条按它归一化。</summary>
    public int MaxBarNoteCount { get; private set; }

    /// <summary>当前视图左边缘对应的 tick。**永远落在合法范围内**（见 <see cref="PianoRollGeometry.ClampViewStart"/>）。</summary>
    public long ViewStartTick { get; private set; }

    /// <summary>视图左边缘落在第几小节（0 起）。导航条的「第 a–b 小节」用它。</summary>
    public int ViewStartBar => PianoRollGeometry.BarAtTick(ViewStartTick, TicksPerBar);

    /// <summary>当前选中的音符（轨下标, 音下标）。没选中是 null。</summary>
    public (int Track, int Note)? Selection { get; private set; }

    /// <summary>某个 tick 落在第几小节（**1 起**）。走带条那个「位置」用它。</summary>
    public int BarOfTick(long tick) => PianoRollGeometry.BarAtTick(tick, TicksPerBar) + 1;

    /// <summary>
    /// 把选中放回某个音符上。<b>编辑之后重建控制器时用它。</b>
    ///
    /// 改速度、改移调都会换一份 <see cref="Song"/>，而控制器是照着曲子建出来的一次性对象，
    /// 只能重建 —— 重建之后新控制器不认识上一个的选中，用户改一下就会丢掉选中。
    /// 音符数组本身一个字节都没动，所以下标照旧有效；这里仍然夹一道，越界就当没选中
    /// （曲子换成另一首、或将来某条命令删掉了音符时，不该抛在这儿）。
    /// </summary>
    /// <param name="track">轨下标（0 起）。</param>
    /// <param name="note">音在该轨音符数组里的下标。</param>
    public void RestoreSelection(int track, int note)
    {
        bool valid = track >= 0 && track < _song.Tracks.Count
            && note >= 0 && note < _song.Tracks[track].Notes.Count;

        Selection = valid ? (track, note) : null;
    }

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

    /// <summary>这条轨显示用的音高范围（含余量），只算一次。</summary>
    public (int Low, int High) PitchRangeOf(int trackIndex)
    {
        if (_pitchRanges[trackIndex].Low >= 0) return _pitchRanges[trackIndex];

        var track = _song.Tracks[trackIndex];
        if (track.Notes.Count == 0)
        {
            _pitchRanges[trackIndex] = PianoRollGeometry.FitPitchRange(60, 60);
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
    /// 命中判定：这根轨的这个点上有没有音，命中的是头、尾还是身体。
    /// 没命中返回 <see cref="PianoRollGeometry.RollHit.None"/>，<paramref name="noteIndex"/> 为 -1。
    /// </summary>
    public PianoRollGeometry.RollHit HitTest(
        int trackIndex, in PianoRollGeometry.Viewport viewport, double x, double y, out int noteIndex)
    {
        noteIndex = -1;
        var track = _song.Tracks[trackIndex];
        var flags = InRangeFlagsOf(trackIndex);

        for (int i = 0; i < track.Notes.Count; i++)
        {
            var note = track.Notes[i];
            // 先用 tick 粗筛：一屏最多几十个音，但一首曲子可能上万，一个一个建块太浪费
            if (note.StartTick > viewport.ViewStartTick + viewport.TicksVisible) break;
            if (note.EndTick < viewport.ViewStartTick) continue;

            var box = PianoRollGeometry.BoxOf(
                viewport, i, note.StartTick, note.LengthTicks, note.Pitch + track.Transpose,
                i < flags.Count && flags[i]);

            var hit = PianoRollGeometry.HitTest(box, x, y);
            if (hit == PianoRollGeometry.RollHit.None) continue;

            noteIndex = i;
            return hit;
        }

        return PianoRollGeometry.RollHit.None;
    }

    // ==================== 读数条 / 键盘定位 ====================

    /// <summary>读数条要的那几个数：某个音符在谱面上的位置。</summary>
    /// <param name="Track">轨下标（0 起）。</param>
    /// <param name="Note">音在该轨音符数组里的下标。</param>
    /// <param name="Pitch">**移调之后**的音高 —— 和卷帘上看到、耳朵听到的是同一个。</param>
    /// <param name="Bar">第几小节（1 起）。</param>
    /// <param name="BeatInBar">小节内第几拍（1 起，带小数）。</param>
    /// <param name="LengthBeats">时值，单位拍。</param>
    public readonly record struct NoteInfo(
        int Track, int Note, int Pitch, int Bar, double BeatInBar, double LengthBeats);

    /// <summary>把某个音符描述出来。下标越界返回 null（悬停时轨道刚被换掉就会碰上）。</summary>
    public NoteInfo? Describe(int trackIndex, int noteIndex)
    {
        if (trackIndex < 0 || trackIndex >= _song.Tracks.Count) return null;
        var track = _song.Tracks[trackIndex];
        if (noteIndex < 0 || noteIndex >= track.Notes.Count) return null;

        var note = track.Notes[noteIndex];
        long barTicks = TicksPerBar;
        long intoBar = note.StartTick - PianoRollGeometry.TickOfBar(
            PianoRollGeometry.BarAtTick(note.StartTick, barTicks), barTicks);

        return new NoteInfo(
            trackIndex,
            noteIndex,
            note.Pitch + track.Transpose,
            PianoRollGeometry.BarAtTick(note.StartTick, barTicks) + 1,
            Format.Beats(intoBar, _ticksPerQuarterNote) + 1,
            Format.Beats(note.LengthTicks, _ticksPerQuarterNote));
    }

    /// <summary>当前选中的音符描述。没选中是 null。</summary>
    public NoteInfo? DescribeSelection() =>
        Selection is { } s ? Describe(s.Track, s.Note) : null;

    /// <summary>
    /// 按时间在**所有轨的音符**之间前后跳一个（<paramref name="delta"/> = ±1）。
    ///
    /// 这一张只定位，不移动 —— 方向键选中它、把视图带过去，
    /// 到曲子头尾就停住（不绕回去：绕回去会让人以为自己按错了方向）。
    /// 返回新的选中项；一个音都没有时返回 null。
    /// </summary>
    public NoteInfo? MoveSelection(int delta)
    {
        if (_ordered.Count == 0) return null;
        if (delta == 0) return DescribeSelection();

        // 没选中时：往后跳从第一个开始，往前跳从最后一个开始
        int current = Selection is { } s ? _ordered.IndexOf(s) : (delta > 0 ? -1 : 0);
        int next = Math.Clamp(current + delta, 0, _ordered.Count - 1);
        if (next == current && Selection is not null) return DescribeSelection();

        Selection = _ordered[next];

        var (trackIndex, noteIndex) = _ordered[next];
        long startTick = _song.Tracks[trackIndex].Notes[noteIndex].StartTick;
        // 视图左边缘对齐到这个音所在的小节 —— 横向「滚进视野」就是这一步；
        // 纵向（哪条轨）由视图那边 BringIntoView
        SetViewStart(PianoRollGeometry.TickOfBar(
            PianoRollGeometry.BarAtTick(startTick, TicksPerBar), TicksPerBar));

        return DescribeSelection();
    }

    // ==================== 内部 ====================

    /// <summary>所有轨的音符按时间排成一条线，键盘定位在上面走。</summary>
    private void BuildTimeOrder()
    {
        for (int t = 0; t < _song.Tracks.Count; t++)
        {
            var notes = _song.Tracks[t].Notes;
            for (int n = 0; n < notes.Count; n++) _ordered.Add((t, n));
        }

        // 同刻的音按轨号、再按音高排：顺序必须是**确定的**，
        // 否则同一份谱子按两次方向键走到的地方可能不一样
        _ordered.Sort((a, b) =>
        {
            int cmp = _song.Tracks[a.Track].Notes[a.Note].StartTick
                .CompareTo(_song.Tracks[b.Track].Notes[b.Note].StartTick);
            if (cmp != 0) return cmp;
            cmp = a.Track.CompareTo(b.Track);
            if (cmp != 0) return cmp;
            return _song.Tracks[a.Track].Notes[a.Note].Pitch
                .CompareTo(_song.Tracks[b.Track].Notes[b.Note].Pitch);
        });
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
