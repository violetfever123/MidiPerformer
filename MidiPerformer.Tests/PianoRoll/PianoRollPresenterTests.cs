using MidiPerformer.Adapters;
using MidiPerformer.Adapters.Presenters;
using MidiPerformer.Core.Model;
using NUnit.Framework;

namespace MidiPerformer.Tests.PianoRoll;

/// <summary>
/// 卷帘 tick → 像素 方向：一屏要画什么。
/// 落笔位置全部来自坐标换算那边，这里的产出是纯数据
/// （<see cref="PianoRollPresenter.LaneScene"/> / <see cref="PianoRollPresenter.NavScene"/>），不用起窗口。
/// </summary>
public class PianoRollPresenterTests
{
    private const long Bar = 1920;
    private const int TicksPerQuarter = 480;

    private static Track Lane(params Note[] notes) => new(0, 0, "主旋律", 24, Numbered(notes));

    /// <summary>给没写身份的音按数组顺序发 1..N 号（场景里装的是身份，没号就谁都认不出来）。</summary>
    private static Note[] Numbered(Note[] notes)
    {
        var numbered = new Note[notes.Length];
        for (int i = 0; i < notes.Length; i++)
            numbered[i] = notes[i].Id == NoteId.None ? notes[i] with { Id = new NoteId(i + 1) } : notes[i];

        return numbered;
    }

    /// <summary>第 <paramref name="index"/> 个音（数组序，0 起）的号；用例说的是「第几个音」，模型存的是号。</summary>
    private static NoteId IdOf(int index) => new(index + 1);

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
        NoteId selected = default)
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
            new Note(64, Bar * 4, 240, 100),    // 正好从右边缘起，屏幕外
            new Note(65, Bar * 10, 240, 100));  // 远在屏幕外

        var scene = Build(track, View());

        Assert.Multiple(() =>
        {
            Assert.That(scene.Notes.Select(n => n.Pitch), Is.EqualTo(new[] { 60, 62 }));
            Assert.That(scene.Notes.Select(n => n.Id), Is.EqualTo(new[] { IdOf(0), IdOf(1) }),
                "块上带的是**身份**，界面凭它认出选中集里的音、也凭它回到模型");
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
        // 卷帘上看到的音高 = 听到的音高 = 游戏里按的那个，三处必须是同一个数
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
        // 这一格只影响画法：数组短了一格就画成正常音，不该连累得画都画不出来
        var track = Lane(new Note(60, 0, 240, 100));

        var scene = Build(track, View(), inRange: Array.Empty<bool>());

        Assert.That(scene.Notes[0].InRange, Is.True);
    }

    // ==================== 播放头 ====================

    [Test]
    public void 播放头不画的时候给一个NaN()
    {
        // 拖导航条时整个卷帘都不画红线
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
        var scene = Build(Lane(new Note(60, 0, 240, 100), new Note(62, 480, 240, 100)), View(), selected: IdOf(1));

        Assert.That(scene.SelectedNote, Is.EqualTo(IdOf(1)));
    }

    // ==================== 网格 ====================

    [Test]
    public void 一屏画四小节的小节线()
    {
        // 起点 0、一屏 4 小节：小节线落在 0/1/2/3/4 小节上；多出来那两条在屏幕外，画了也看不见
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
        // 4/4 每小节 3 条拍线（不含小节线本身），一屏 4 小节共 12 条落在屏幕里
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
        // 整曲才 2 小节，一屏 4 小节的窗口右半边是空白谱面：只有存在的那几小节才有线，
        // 最后那个小节的起始线照画，它之后一条都不画
        var scene = Build(Lane(new Note(60, 0, 240, 100)), View(width: 800), barCount: 2);

        Assert.That(scene.BarLines.Select(x => Math.Round(x, 6)), Is.EqualTo(new[] { 0.0, 200.0 }));
    }

    // ==================== 导航条 ====================

    /// <summary>建一张导航条场景。默认 1000px 宽、4 小节的曲子。</summary>
    private static PianoRollPresenter.NavScene Nav(
        Track? track,
        (int Low, int High)? pitchRange = null,
        long playhead = 0,
        double width = 1000,
        double height = 26,
        int barCount = 4,
        long totalTicks = Bar * 4,
        long viewStart = 0,
        long ticksVisible = Bar * 4)
        => PianoRollPresenter.BuildNav(
            new PianoRollPresenter.NavViewport(width, height, totalTicks, barCount),
            track, pitchRange ?? (60, 72), playhead, viewStart, ticksVisible);

    [Test]
    public void 导航条画的是焦点轨的音符块()
    {
        // 音域 60..72 共 13 行，摊到 26px 上正好一行 2px，块高 = 2 - 1 = 1
        var scene = Nav(Lane(new Note(60, 0, Bar / 2, 100), new Note(72, Bar, Bar / 4, 100)));

        Assert.Multiple(() =>
        {
            Assert.That(scene.Notes, Has.Count.EqualTo(2));

            // 横向按 tick 铺满整曲：整曲 4 小节 = 1000px，一小节 250px
            Assert.That(scene.Notes[0].X, Is.EqualTo(0).Within(1e-9), "曲子开头那个音贴着左边缘");
            Assert.That(scene.Notes[0].Width, Is.EqualTo(125 - 1).Within(1e-9), "半小节宽 125px，再让出 1px 的缝");
            Assert.That(scene.Notes[1].X, Is.EqualTo(250).Within(1e-9));

            // 纵向按音高铺开、高音在上：最高音那行贴着顶边
            Assert.That(scene.Notes[1].Y, Is.EqualTo(0).Within(1e-9), "音域最高的那个音在最上面一行");
            Assert.That(scene.Notes[0].Y, Is.EqualTo(24).Within(1e-9), "比它低 12 个半音 = 12 行 × 2px");
            Assert.That(scene.Notes[0].Height, Is.EqualTo(1).Within(1e-9));
        });
    }

    [Test]
    public void 导航条换一条轨就换一份音符()
    {
        // 缩略图跟的是焦点轨，不是「所有轨加在一起」：换了焦点轨，同一段 tick 上画的就得换成那条轨的音
        var melody = Lane(new Note(60, 0, Bar / 2, 100));
        var bass = new Track(0, 1, "贝斯", 32, new[] { new Note(48, Bar, Bar / 2, 100) });

        var melodyScene = Nav(melody);
        var bassScene = Nav(bass);

        Assert.Multiple(() =>
        {
            Assert.That(melodyScene.Notes, Has.Count.EqualTo(1));
            Assert.That(melodyScene.Notes[0].X, Is.EqualTo(0).Within(1e-9), "旋律的音在开头");

            Assert.That(bassScene.Notes, Has.Count.EqualTo(1));
            Assert.That(bassScene.Notes[0].X, Is.EqualTo(250).Within(1e-9), "贝斯的音在第 2 小节");
        });
    }

    [Test]
    public void 导航条纵向用这条轨自己的音域铺开()
    {
        // 音域越窄纵向拉得越开：缩略图的纵向分辨率全给这条轨用到的音
        var track = Lane(new Note(60, 0, Bar / 2, 100), new Note(62, Bar, Bar / 2, 100));

        var narrow = Nav(track, pitchRange: (60, 62), height: 30);
        var wide = Nav(track, pitchRange: (48, 72), height: 30);

        Assert.Multiple(() =>
        {
            Assert.That(narrow.Notes[0].Height, Is.GreaterThan(wide.Notes[0].Height),
                "音域窄的轨，一行更矮不了、块更厚");
            Assert.That(narrow.Notes[0].Y, Is.EqualTo(2 * (30.0 / 3)).Within(1e-9),
                "3 行摊 30px，低音那行在第 2 行顶");
        });
    }

    [Test]
    public void 导航条音高越界的音贴在边行不丢()
    {
        // 音域被 FitPitchRange 夹过之后某个音会落在音域之外：卷帘那边直接不画，缩略图不能 —— 少一个音看着就是「这段没谱」
        var track = Lane(new Note(60, 0, Bar / 2, 100), new Note(80, Bar, Bar / 2, 100));

        var scene = Nav(track, pitchRange: (60, 72), height: 26);

        Assert.Multiple(() =>
        {
            Assert.That(scene.Notes, Has.Count.EqualTo(2), "越界的那个音照样要画");
            Assert.That(scene.Notes[1].Y, Is.EqualTo(0).Within(1e-9), "夹到最高那行");
            Assert.That(scene.Notes[1].Y + scene.Notes[1].Height, Is.LessThanOrEqualTo(26),
                "夹完不能画到条外面去");
        });
    }

    [Test]
    public void 导航条上的短音也有一笔可看()
    {
        // 长曲子铺进一条几百像素的带子：一个十六分音符只剩零点几个像素，不给最小宽度，快的段落整段消失
        var scene = Nav(
            Lane(new Note(60, 0, TicksPerQuarter / 4, 100)),
            barCount: 400, totalTicks: Bar * 400);

        Assert.That(scene.Notes[0].Width,
            Is.GreaterThanOrEqualTo(PianoRollGeometry.MinNoteWidth));
    }

    [Test]
    public void 导航条一个音都没有也不除零()
    {
        var scene = Nav(Lane());

        Assert.Multiple(() =>
        {
            Assert.That(scene.Notes, Is.Empty);
            Assert.That(double.IsNaN(scene.BarWidth), Is.False);
            Assert.That(scene.BarWidth, Is.EqualTo(250).Within(1e-9), "4 小节摊 1000px");
        });
    }

    [Test]
    public void 没有这条轨时导航条是一张空的()
    {
        // 轨被删光的那一帧手上没有轨对象可给（控制器把越界下标当「没这条轨」），要的是一条空缩略图，不是崩溃
        var scene = Nav(track: null);

        Assert.Multiple(() =>
        {
            Assert.That(scene.Notes, Is.Empty);
            Assert.That(scene.BarWidth, Is.EqualTo(250).Within(1e-9));
            Assert.That(double.IsNaN(scene.PlayheadX), Is.False);
        });
    }

    [Test]
    public void 导航条上的播放头按整曲位置落点()
    {
        var scene = Nav(Lane(new Note(60, 0, Bar / 2, 100)), playhead: Bar * 2);

        Assert.That(scene.PlayheadX, Is.EqualTo(500).Within(1e-9), "4 小节的曲子播到一半就是一半宽");
    }

    [Test]
    public void 导航条上的框标出当前可见的那一段()
    {
        var scene = Nav(
            Lane(new Note(60, 0, Bar / 2, 100)),
            viewStart: Bar, ticksVisible: Bar * 2);

        Assert.Multiple(() =>
        {
            Assert.That(scene.ThumbX, Is.EqualTo(250).Within(1e-9), "从第 2 小节起");
            Assert.That(scene.ThumbWidth, Is.EqualTo(500).Within(1e-9), "看得见一半");
        });
    }

    [Test]
    public void 曲子比一屏还短时框铺满整条()
    {
        var scene = Nav(
            Lane(new Note(60, 0, Bar / 2, 100)),
            barCount: 2, totalTicks: Bar * 2, ticksVisible: Bar * 4);

        Assert.That(scene.ThumbWidth, Is.EqualTo(1000), "框不能比导航条还宽");
    }

    // ==================== 拖动预览（幽灵块）与框选 ====================

    /// <summary>建一张带拖动预览 / 框选 / 划段 / 多选的场景。位移是增量，和命令收的是同一个说法。</summary>
    private static PianoRollPresenter.LaneScene BuildDragging(
        Track track,
        PianoRollGeometry.Viewport view,
        IReadOnlyList<NoteId>? dragging = null,
        long startDelta = 0,
        long lengthDelta = 0,
        int pitchDelta = 0,
        PianoRollPresenter.MarqueeRange? marquee = null,
        IReadOnlyList<NoteId>? selected = null,
        PianoRollPresenter.MarqueeRange? cut = null)
        => PianoRollPresenter.BuildLane(
            track, view, 8, TicksPerQuarter,
            Array.Empty<bool>(),
            new PianoRollPresenter.RollOverlay(
                0, false, selected ?? Array.Empty<NoteId>(),
                dragging is null
                    ? null
                    : new PianoRollPresenter.DragPreview(dragging, startDelta, lengthDelta, pitchDelta),
                marquee,
                cut));

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

        var scene = BuildDragging(track, View(), dragging: new[] { IdOf(1) }, startDelta: 480);

        Assert.Multiple(() =>
        {
            Assert.That(scene.GhostNotes, Has.Count.EqualTo(1));
            Assert.That(scene.GhostNotes[0].Id, Is.EqualTo(IdOf(1)), "拖的是第二个，幽灵也只有第二个");
        });
    }

    /// <summary>
    /// 幽灵块要和这个音改完之后真画出来的块一模一样 —— 预览不等于落点，用户就是照着一幅假象在拖。
    /// 拿一个窄到被 <c>MinNoteWidth</c> 托住的极短音来试，末一条断言保证落点真的宽过下限。
    /// </summary>
    [Test]
    public void 幽灵块和这个音改完之后真画出来的块一模一样()
    {
        var before = Lane(new Note(60, 0, 1, 100));
        var after = Lane(new Note(60, 480, 481, 100));

        var ghost = BuildDragging(before, View(), dragging: new[] { IdOf(0) }, startDelta: 480, lengthDelta: 480)
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
            dragging: new[] { IdOf(0) }, pitchDelta: 2).GhostNotes.Single();
        var landed = Build(Lane(new Note(62, 0, 240, 100)), View()).Notes.Single();

        Assert.That(ghost.Y, Is.EqualTo(landed.Y).Within(1e-9), "幽灵得落在升两个半音那一行上");
    }

    [Test]
    public void 没在框选时没有带子()
        => Assert.That(Build(Lane(new Note(60, 0, 240, 100)), View()).Marquee, Is.Null);

    [Test]
    public void 框选那把带子往左拖也是正的宽()
    {
        // 起止是反的（从右往左拖），谁算像素谁负责归一
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
        // 框的纵向不参与判定（删的是这段区间里的所有音，与音高无关），画矮了就是在撒谎
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
    public void 没在划段时没有红带子()
        => Assert.That(Build(Lane(new Note(60, 0, 240, 100)), View()).CutBand, Is.Null);

    /// <summary>红带子和蓝带子铺的是同一份 <see cref="PianoRollGeometry"/> 几何：红带子复用蓝带子的算法，不各算各的。</summary>
    [Test]
    public void 划段那把红带子往左拖也是正的宽()
    {
        var view = View();
        var rect = BuildDragging(Lane(new Note(60, 0, 240, 100)), view,
            cut: new PianoRollPresenter.MarqueeRange(Bar, 0)).CutBand!.Value;

        Assert.Multiple(() =>
        {
            Assert.That(rect.X, Is.EqualTo(PianoRollGeometry.XAtTick(view, 0)).Within(1e-9), "左边缘取小的那头");
            Assert.That(rect.Width, Is.EqualTo(PianoRollGeometry.XAtTick(view, Bar)).Within(1e-9));
        });
    }

    [Test]
    public void 划段那把红带子铺满标尺以下的整条轨()
    {
        // 和框选同理：抽掉的是这段时间里的所有音，与音高无关
        var view = View();
        var rect = BuildDragging(Lane(new Note(60, 0, 240, 100)), view,
            cut: new PianoRollPresenter.MarqueeRange(0, Bar)).CutBand!.Value;

        Assert.Multiple(() =>
        {
            Assert.That(rect.Y, Is.EqualTo(PianoRollGeometry.RulerHeight));
            Assert.That(rect.Height, Is.EqualTo(view.Height - PianoRollGeometry.RulerHeight));
        });
    }

    [Test]
    public void 红蓝两条带子互不干扰()
    {
        var view = View();
        var scene = BuildDragging(Lane(new Note(60, 0, 240, 100)), view,
            marquee: new PianoRollPresenter.MarqueeRange(0, Bar),
            cut: new PianoRollPresenter.MarqueeRange(Bar * 2, Bar * 3));

        Assert.Multiple(() =>
        {
            Assert.That(scene.Marquee!.Value.X, Is.EqualTo(PianoRollGeometry.XAtTick(view, 0)).Within(1e-9),
                "框选那条还在自己该在的地方");
            Assert.That(scene.CutBand!.Value.X, Is.EqualTo(PianoRollGeometry.XAtTick(view, Bar * 2)).Within(1e-9),
                "划段那条也在自己该在的地方 —— 两个字段没写串");
        });
    }

    [Test]
    public void 多选时主选中是最后加进去的那个()
    {
        var track = Lane(new Note(60, 0, 240, 100), new Note(62, 480, 240, 100));

        var scene = BuildDragging(track, View(), selected: new[] { IdOf(0), IdOf(1) });

        Assert.Multiple(() =>
        {
            Assert.That(scene.SelectedNotes, Is.EqualTo(new[] { IdOf(0), IdOf(1) }));
            Assert.That(scene.SelectedNote, Is.EqualTo(IdOf(1)), "主选中 = 选中集的尾巴，不是另存的第二份状态");
        });
    }

    /// <summary>
    /// 选中集按加进来的先后留着，不被排成升序：
    /// <see cref="PianoRollPresenter.LaneScene.SelectedNote"/> 取的是尾巴，排成升序主选中就成了号最大的那个。
    /// </summary>
    [Test]
    public void 选中集的顺序是加进来的先后不是升序()
    {
        var track = Lane(new Note(60, 0, 240, 100), new Note(62, 480, 240, 100));

        // 先点后面那个，再 Shift 点前面那个：先 9 号后 8 号
        var scene = BuildDragging(track, View(), selected: new[] { new NoteId(9), new NoteId(8) });

        Assert.Multiple(() =>
        {
            Assert.That(scene.SelectedNotes, Is.EqualTo(new[] { new NoteId(9), new NoteId(8) }), "原样留着，别排");
            Assert.That(scene.SelectedNote, Is.EqualTo(new NoteId(8)), "主选中是最后加的那个，不是号最大的那个");
        });
    }

    [Test]
    public void 场景自己留一份选中集不跟着调用方的缓冲变()
    {
        // 卷帘那边复用同一个缓冲，下一帧就清掉重填；留着引用的话，这一帧刚画到一半选中集就被改了
        var buffer = new List<NoteId> { IdOf(0), IdOf(1) };
        var track = Lane(new Note(60, 0, 240, 100), new Note(62, 480, 240, 100));

        var scene = BuildDragging(track, View(), selected: buffer);
        buffer.Clear();

        Assert.That(scene.SelectedNotes, Is.EqualTo(new[] { IdOf(0), IdOf(1) }), "调用方清空之后场景还得是原来那两个");
    }
}
