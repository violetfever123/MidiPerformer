using MidiPerformer.Adapters;
using MidiPerformer.Adapters.Presenters;
using MidiPerformer.Core.Model;
using NUnit.Framework;

namespace MidiPerformer.Tests.PianoRoll;

/// <summary>
/// 卷帘的 <b>tick → 像素</b> 方向：一屏要画的东西算得对不对。
///
/// 这里测的是「画什么」，不是「画在哪」—— 落笔的位置全部来自 S4 缝，那边已经扫过一遍了。
/// 产出是纯数据（<see cref="PianoRollPresenter.LaneScene"/> / <see cref="PianoRollPresenter.NavScene"/>），
/// 所以不用起窗口就能一条条比。
/// </summary>
public class PianoRollPresenterTests
{
    private const long Bar = 1920;
    private const int TicksPerQuarter = 480;

    private static Track Lane(params Note[] notes) => new(0, 0, "主旋律", 24, notes);

    private static PianoRollGeometry.Viewport View(
        double width = 800, long viewStart = 0, int low = 48, int high = 72)
        => new(width, 200, viewStart, Bar, low, high);

    private static PianoRollPresenter.LaneScene Build(
        Track track,
        PianoRollGeometry.Viewport view,
        int barCount = 8,
        bool[]? inRange = null,
        long playhead = 0,
        bool playheadVisible = true,
        int selected = -1)
        => PianoRollPresenter.BuildLane(
            track, view, barCount, TicksPerQuarter,
            inRange ?? Array.Empty<bool>(),
            new PianoRollPresenter.RollOverlay(playhead, playheadVisible, selected));

    // ==================== 画哪些音 ====================

    [Test]
    public void 只画这一屏里的音()
    {
        var track = Lane(
            new Note(60, 0, 240, 100),          // 屏幕里
            new Note(62, Bar * 2, 240, 100),    // 屏幕里
            new Note(64, Bar * 4, 240, 100),    // 正好从右边缘起 —— 屏幕外
            new Note(65, Bar * 10, 240, 100));  // 远在屏幕外

        var scene = Build(track, View());

        Assert.Multiple(() =>
        {
            Assert.That(scene.Notes.Select(n => n.Pitch), Is.EqualTo(new[] { 60, 62 }));
            Assert.That(scene.Notes.Select(n => n.Index), Is.EqualTo(new[] { 0, 1 }),
                "下标要指回原来的音符数组，命中之后才回得到模型");
        });
    }

    [Test]
    public void 压着左边缘的音要画出来()
    {
        // 从屏幕左边之前起、一直响到屏幕里：不算「屏幕外」，否则一滚过去音就没了
        var track = Lane(new Note(60, Bar * 3 - 100, 400, 100));

        var scene = Build(track, View(viewStart: Bar * 3));

        Assert.That(scene.Notes, Has.Count.EqualTo(1));
    }

    [Test]
    public void 音域之外的音不画()
    {
        var track = Lane(new Note(30, 0, 240, 100), new Note(60, 480, 240, 100));

        var scene = Build(track, View(low: 48, high: 72));

        Assert.Multiple(() =>
        {
            Assert.That(scene.Notes, Has.Count.EqualTo(1));
            Assert.That(scene.Notes[0].Pitch, Is.EqualTo(60));
        });
    }

    [Test]
    public void 音符块画的是移调之后的音高()
    {
        // 卷帘上看到的音高 = 听到的音高 = 游戏里按的那个音高，三处必须是同一个数
        var track = new Track(0, 0, "主旋律", 24, new[] { new Note(60, 0, 240, 100) }, Transpose: -12);

        var scene = Build(track, View(low: 40, high: 60));

        Assert.Multiple(() =>
        {
            Assert.That(scene.Notes, Has.Count.EqualTo(1));
            Assert.That(scene.Notes[0].Pitch, Is.EqualTo(48));
            Assert.That(scene.Notes[0].Y,
                Is.EqualTo(PianoRollGeometry.YAtPitch(View(low: 40, high: 60), 48)
                    + PianoRollGeometry.NotePad).Within(1e-9));
        });
    }

    [Test]
    public void 标灰标记跟着音符走()
    {
        var track = Lane(new Note(60, 0, 240, 100), new Note(64, 480, 240, 100));

        var scene = Build(track, View(), inRange: new[] { false, true });

        Assert.Multiple(() =>
        {
            Assert.That(scene.Notes[0].InRange, Is.False);
            Assert.That(scene.Notes[1].InRange, Is.True);
        });
    }

    [Test]
    public void 标灰标记缺了就当在范围内()
    {
        // 这一格只影响画法。数组短了一格就画成正常音，不该连累得画都画不出来
        var track = Lane(new Note(60, 0, 240, 100));

        var scene = Build(track, View(), inRange: Array.Empty<bool>());

        Assert.That(scene.Notes[0].InRange, Is.True);
    }

    // ==================== 播放头 ====================

    [Test]
    public void 播放头不画的时候给一个NaN()
    {
        // 拖导航条时整个卷帘都不画红线（wireframe 标注 3）
        var scene = Build(Lane(new Note(60, 0, 240, 100)), View(), playhead: Bar, playheadVisible: false);

        Assert.That(double.IsNaN(scene.PlayheadX), Is.True,
            "不画的时候要明确说「不画」，不能给一个 0 —— 那会画到屏幕左边缘上去");
    }

    [Test]
    public void 播放头画的时候落在它自己的位置上()
    {
        var view = View(viewStart: Bar * 2);
        var scene = Build(Lane(new Note(60, 0, 240, 100)), view, playhead: Bar * 3);

        Assert.That(scene.PlayheadX,
            Is.EqualTo(PianoRollGeometry.XAtTick(view, Bar * 3)).Within(1e-9));
    }

    [Test]
    public void 选中的音标出来()
    {
        var scene = Build(Lane(new Note(60, 0, 240, 100), new Note(62, 480, 240, 100)), View(), selected: 1);

        Assert.That(scene.SelectedNote, Is.EqualTo(1));
    }

    // ==================== 网格 ====================

    [Test]
    public void 一屏画四小节的小节线()
    {
        // 起点 0、一屏 4 小节：小节线落在 0/1/2/3/4 小节上。
        // 多出来那两条（第 5、6 小节）在屏幕外，画了也看不见，但没有理由为它们加一个判断
        var scene = Build(Lane(new Note(60, 0, 240, 100)), View(width: 800), barCount: 8);

        // 乘出来的浮点数会有末位噪声，比到小数点后六位就够了
        Assert.That(scene.BarLines.Select(x => Math.Round(x, 6)),
            Is.EqualTo(new[] { 0.0, 200.0, 400.0, 600.0, 800.0, 1000.0 }));
    }

    [Test]
    public void 小节号从一开始数()
    {
        var scene = Build(Lane(new Note(60, 0, 240, 100)), View(width: 800), barCount: 8);

        Assert.Multiple(() =>
        {
            // 贴着屏幕右边缘那个小节号（x=800）画出去就看不见了，直接不给
            Assert.That(scene.BarLabels.Select(l => l.Text), Is.EqualTo(new[] { "1", "2", "3", "4" }));
            Assert.That(scene.BarLabels[0].X, Is.EqualTo(4).Within(1e-6), "小节号从小节线往右让 4px");
            Assert.That(scene.BarLabels[1].X, Is.EqualTo(204).Within(1e-6));
        });
    }

    [Test]
    public void 从中间开始看时小节号跟着走()
    {
        var scene = Build(Lane(new Note(60, 0, 240, 100)), View(width: 800, viewStart: Bar * 5), barCount: 8);

        Assert.Multiple(() =>
        {
            Assert.That(scene.BarLabels[0].Text, Is.EqualTo("6"), "左边缘是第 6 小节");
            Assert.That(scene.BarLabels[0].X, Is.EqualTo(4).Within(1e-6));
        });
    }

    [Test]
    public void 拍线按拍号切()
    {
        // 4/4 一小节 4 拍：每小节 3 条拍线（不含小节线本身），一屏 4 小节共 12 条落在屏幕里
        var scene = Build(Lane(new Note(60, 0, 240, 100)), View(width: 800), barCount: 8);
        double beatWidth = 800.0 / (Bar * 4) * TicksPerQuarter;

        Assert.Multiple(() =>
        {
            Assert.That(scene.BeatLines[0], Is.EqualTo(beatWidth).Within(1e-9));
            Assert.That(scene.BeatLines.Any(x => Math.Abs(x) < 1e-9), Is.False, "小节线不重复画成拍线");
            Assert.That(scene.BeatLines.Count(x => x < 800), Is.EqualTo(12));
        });
    }

    [Test]
    public void 曲子之外不画小节线()
    {
        // 整曲才 2 小节，一屏 4 小节的窗口右半边是空白谱面。
        // 只有「存在的那几小节」才有线：最后那个小节的起始线照画，
        // 它之后（曲子已经完了）一条都不画 —— 空着的半边正是「这里没谱了」的样子
        var scene = Build(Lane(new Note(60, 0, 240, 100)), View(width: 800), barCount: 2);

        Assert.That(scene.BarLines.Select(x => Math.Round(x, 6)), Is.EqualTo(new[] { 0.0, 200.0 }));
    }

    // ==================== 导航条 ====================

    [Test]
    public void 导航条的柱子按小节音符数归一化()
    {
        var scene = PianoRollPresenter.BuildNav(
            new[] { 0, 4, 2, 8 }, width: 1000, totalTicks: Bar * 4, viewStartTick: 0, ticksVisible: Bar * 4);

        Assert.Multiple(() =>
        {
            Assert.That(scene.Bars.Select(b => b.NoteCount), Is.EqualTo(new[] { 0, 4, 2, 8 }));
            Assert.That(scene.Bars[3].Fraction, Is.EqualTo(1.0), "最密的那小节顶格");
            Assert.That(scene.Bars[1].Fraction, Is.EqualTo(0.5));
            Assert.That(scene.Bars[0].Fraction, Is.EqualTo(0), "空小节的柱高交给画的那边兜底，数据就是 0");
            Assert.That(scene.BarWidth, Is.EqualTo(250));
        });
    }

    [Test]
    public void 导航条一个音都没有也不除零()
    {
        var scene = PianoRollPresenter.BuildNav(
            new[] { 0, 0, 0 }, width: 900, totalTicks: Bar * 3, viewStartTick: 0, ticksVisible: Bar * 4);

        Assert.Multiple(() =>
        {
            Assert.That(scene.Bars.All(b => b.Fraction == 0), Is.True);
            Assert.That(double.IsNaN(scene.BarWidth), Is.False);
        });
    }

    [Test]
    public void 导航条上的框标出当前可见的那一段()
    {
        var scene = PianoRollPresenter.BuildNav(
            new[] { 1, 1, 1, 1 }, width: 1000, totalTicks: Bar * 4, viewStartTick: Bar, ticksVisible: Bar * 2);

        Assert.Multiple(() =>
        {
            Assert.That(scene.ThumbX, Is.EqualTo(250).Within(1e-9), "从第 2 小节起");
            Assert.That(scene.ThumbWidth, Is.EqualTo(500).Within(1e-9), "看得见一半");
        });
    }

    [Test]
    public void 曲子比一屏还短时框铺满整条()
    {
        var scene = PianoRollPresenter.BuildNav(
            new[] { 1, 1 }, width: 1000, totalTicks: Bar * 2, viewStartTick: 0, ticksVisible: Bar * 4);

        Assert.That(scene.ThumbWidth, Is.EqualTo(1000), "框不能比导航条还宽");
    }

    // ==================== 拖动预览（幽灵块）与框选 ====================

    /// <summary>建一张带拖动预览 / 框选 / 多选的场景。位移是**增量**，和命令收的是同一个说法。</summary>
    private static PianoRollPresenter.LaneScene BuildDragging(
        Track track,
        PianoRollGeometry.Viewport view,
        int[]? dragging = null,
        long startDelta = 0,
        long lengthDelta = 0,
        int pitchDelta = 0,
        PianoRollPresenter.MarqueeRange? marquee = null,
        IReadOnlyList<int>? selected = null)
        => PianoRollPresenter.BuildLane(
            track, view, 8, TicksPerQuarter,
            Array.Empty<bool>(),
            new PianoRollPresenter.RollOverlay(
                0, false, selected ?? Array.Empty<int>(),
                dragging is null
                    ? null
                    : new PianoRollPresenter.DragPreview(dragging, startDelta, lengthDelta, pitchDelta),
                marquee));

    [Test]
    public void 没在拖的时候没有幽灵块()
    {
        var scene = Build(Lane(new Note(60, 0, 240, 100)), View());

        Assert.That(scene.GhostNotes, Is.Empty);
    }

    [Test]
    public void 幽灵块只给正在拖的那几个音()
    {
        var track = Lane(new Note(60, 0, 240, 100), new Note(62, 480, 240, 100));

        var scene = BuildDragging(track, View(), dragging: new[] { 1 }, startDelta: 480);

        Assert.Multiple(() =>
        {
            Assert.That(scene.GhostNotes, Has.Count.EqualTo(1));
            Assert.That(scene.GhostNotes[0].Index, Is.EqualTo(1), "拖的是第二个，幽灵也只有第二个");
        });
    }

    /// <summary>
    /// 这条是幽灵块存在的全部理由：预览要是不等于落点，用户就是照着一幅假象在拖。
    ///
    /// 拿一个**窄到被 <c>MinNoteWidth</c> 托住**的极短音来试，两种写法在这儿会分道扬镳：
    /// 「真块宽度 + 位移像素」算出来是一截偏宽的，而「拿新 tick 重算一遍」和落点严丝合缝。
    /// 末一条断言盯的就是这一点 —— 落点得真的宽过下限，不然这个用例量的是下限、两种写法都能过。
    /// </summary>
    [Test]
    public void 幽灵块和这个音改完之后真画出来的块一模一样()
    {
        var before = Lane(new Note(60, 0, 1, 100));
        var after = Lane(new Note(60, 480, 481, 100));

        var ghost = BuildDragging(before, View(), dragging: new[] { 0 }, startDelta: 480, lengthDelta: 480)
            .GhostNotes.Single();
        var landed = Build(after, View()).Notes.Single();

        Assert.Multiple(() =>
        {
            Assert.That(ghost.X, Is.EqualTo(landed.X).Within(1e-9));
            Assert.That(ghost.Width, Is.EqualTo(landed.Width).Within(1e-9));
            Assert.That(landed.Width, Is.GreaterThan(PianoRollGeometry.MinNoteWidth + 40),
                "落点得真的在量宽度，不是坐在下限上 —— 否则这个用例区分不出两种写法");
        });
    }

    [Test]
    public void 幽灵块跟着音高位移换行()
    {
        var ghost = BuildDragging(Lane(new Note(60, 0, 240, 100)), View(),
            dragging: new[] { 0 }, pitchDelta: 2).GhostNotes.Single();
        var landed = Build(Lane(new Note(62, 0, 240, 100)), View()).Notes.Single();

        Assert.That(ghost.Y, Is.EqualTo(landed.Y).Within(1e-9), "幽灵得落在升两个半音那一行上");
    }

    [Test]
    public void 没在框选时没有带子()
        => Assert.That(Build(Lane(new Note(60, 0, 240, 100)), View()).Marquee, Is.Null);

    [Test]
    public void 框选那把带子往左拖也是正的宽()
    {
        // 起止是反的（从右往左拖）—— 谁算像素谁负责归一
        var view = View();
        var rect = BuildDragging(Lane(new Note(60, 0, 240, 100)), view,
            marquee: new PianoRollPresenter.MarqueeRange(Bar, 0)).Marquee!.Value;

        Assert.Multiple(() =>
        {
            Assert.That(rect.X, Is.EqualTo(PianoRollGeometry.XAtTick(view, 0)).Within(1e-9), "左边缘取小的那头");
            Assert.That(rect.Width, Is.EqualTo(PianoRollGeometry.XAtTick(view, Bar)).Within(1e-9));
        });
    }

    [Test]
    public void 框选那把带子铺满标尺以下的整条轨()
    {
        // 框的纵向不参与判定（删的是「这段区间里的所有音」，与音高无关），
        // 所以画矮了就是在撒谎：只盖住两行、结果删了三行的音
        var view = View();
        var rect = BuildDragging(Lane(new Note(60, 0, 240, 100)), view,
            marquee: new PianoRollPresenter.MarqueeRange(0, Bar)).Marquee!.Value;

        Assert.Multiple(() =>
        {
            Assert.That(rect.Y, Is.EqualTo(PianoRollGeometry.RulerHeight));
            Assert.That(rect.Height, Is.EqualTo(view.Height - PianoRollGeometry.RulerHeight));
        });
    }

    [Test]
    public void 多选时主选中是最后加进去的那个()
    {
        var track = Lane(new Note(60, 0, 240, 100), new Note(62, 480, 240, 100));

        var scene = BuildDragging(track, View(), selected: new[] { 0, 1 });

        Assert.Multiple(() =>
        {
            Assert.That(scene.SelectedNotes, Is.EqualTo(new[] { 0, 1 }));
            Assert.That(scene.SelectedNote, Is.EqualTo(1), "主选中 = 选中集的尾巴，不是另存的第二份状态");
        });
    }

    /// <summary>
    /// 选中集**按加进来的先后**留着，不被排成升序。
    ///
    /// 这条挡的是「顺手把它排一下」：<see cref="PianoRollPresenter.LaneScene.SelectedNote"/>
    /// 取的是尾巴，排成升序就等于把手上的「主选中」换成了下标最大的那个 ——
    /// 用户先点 5 号、再按住 Shift 点 2 号，主选中会从 2 号跳回 5 号。
    /// 顺序在这条链上是有含义的数据，不是随手排的容器。
    /// </summary>
    [Test]
    public void 选中集的顺序是加进来的先后不是升序()
    {
        var track = Lane(new Note(60, 0, 240, 100), new Note(62, 480, 240, 100));

        // 先点后面那个（1），再 Shift 点前面那个（0）
        var scene = BuildDragging(track, View(), selected: new[] { 1, 0 });

        Assert.Multiple(() =>
        {
            Assert.That(scene.SelectedNotes, Is.EqualTo(new[] { 1, 0 }), "原样留着，别排");
            Assert.That(scene.SelectedNote, Is.EqualTo(0), "主选中是最后加的那个，不是下标最大的那个");
        });
    }

    [Test]
    public void 场景自己留一份选中集不跟着调用方的缓冲变()
    {
        // 卷帘那边复用同一个缓冲，下一帧就清掉重填，而场景要活到下一次 SetScene ——
        // 留着引用的话，这一帧刚画到一半选中集就被改了
        var buffer = new List<int> { 0, 1 };
        var track = Lane(new Note(60, 0, 240, 100), new Note(62, 480, 240, 100));

        var scene = BuildDragging(track, View(), selected: buffer);
        buffer.Clear();

        Assert.That(scene.SelectedNotes, Is.EqualTo(new[] { 0, 1 }), "调用方清空之后场景还得是原来那两个");
    }
}
