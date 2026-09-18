using MidiPerformer.Adapters;
using MidiPerformer.Core.Model;
using NUnit.Framework;

namespace MidiPerformer.Tests.PianoRoll;

/// <summary>
/// <b>S4 缝</b>：卷帘的坐标换算（见 spec 的「Testing Decisions」）。
///
/// 这一块全是纯函数，所以能拿假视口一遍遍扫 —— 卷帘编辑器最容易出的 bug 就在这几个乘除里，
/// 而只要它不是纯函数（往里塞一个 <c>Control</c>），这些用例一条都写不出来。
///
/// 两个方向互为逆运算，所以「往返恒等」一测就同时覆盖了两边：
/// <c>Presenter 方向</c>（tick → 像素）与 <c>Controller 方向</c>（像素 → tick）。
/// </summary>
public class PianoRollGeometryTests
{
    /// <summary>一个小节 1920 tick（4/4、480 PPQ）。</summary>
    private const long Bar = 1920;

    private static PianoRollGeometry.Viewport View(
        double width = 800, double height = 200, long viewStart = 0, int low = 48, int high = 72)
        => new(width, height, viewStart, Bar, low, high);

    // ==================== 固定 4 小节 ====================

    [Test]
    public void 一屏恒定跨四小节()
    {
        var view = View(width: 800);

        Assert.Multiple(() =>
        {
            Assert.That(PianoRollGeometry.BarsVisible, Is.EqualTo(4), "4 是个常量，不是参数 —— 界面上没有缩放入口");
            Assert.That(view.TicksVisible, Is.EqualTo(Bar * 4));
        });
    }

    [Test]
    public void 窗口变宽是每小节变宽而不是显示更多小节()
    {
        foreach (double width in new[] { 200, 400, 800, 1600, 1281.5 })
        {
            var view = View(width: width);

            Assert.Multiple(() =>
            {
                // 一个小节占屏幕的四分之一，无论窗口多宽
                Assert.That(
                    PianoRollGeometry.XAtTick(view, view.ViewStartTick + Bar),
                    Is.EqualTo(width / 4).Within(1e-9),
                    $"宽 {width}：一小节该是屏幕的四分之一");
                // 右边缘正好是第四小节的末尾 —— 可见范围就是这 4 小节，不多不少
                Assert.That(
                    PianoRollGeometry.XAtTick(view, view.ViewStartTick + Bar * 4),
                    Is.EqualTo(width).Within(1e-9),
                    $"宽 {width}：可见范围该正好 4 小节");
            });
        }
    }

    [Test]
    public void 视图不在曲子开头时可见范围仍是四小节()
    {
        var view = View(width: 640, viewStart: Bar * 9);

        Assert.Multiple(() =>
        {
            Assert.That(PianoRollGeometry.XAtTick(view, Bar * 9), Is.EqualTo(0).Within(1e-9));
            Assert.That(PianoRollGeometry.XAtTick(view, Bar * 13), Is.EqualTo(640).Within(1e-9));
        });
    }

    // ==================== 往返恒等 ====================

    [Test]
    public void 像素到tick再回到像素是同一个位置()
    {
        foreach (double width in new[] { 120, 399.5, 800, 1600 })
        {
            foreach (long start in new long[] { 0, Bar, Bar * 7, Bar * 200 })
            {
                var view = View(width: width, viewStart: start);

                foreach (double x in new[] { 0, 0.5, 1, 33.25, width / 3, width - 0.5, width })
                {
                    double tick = PianoRollGeometry.TickAtX(view, x);
                    double back = PianoRollGeometry.XAtTick(view, tick);

                    Assert.That(back, Is.EqualTo(x).Within(1e-6),
                        $"宽 {width} 起点 {start} 横坐标 {x}：tick 一去一回该回到原处");
                }
            }
        }
    }

    [Test]
    public void tick到像素再回到tick是同一个位置()
    {
        var view = View(width: 800, viewStart: Bar * 3);

        foreach (long tick in new long[] { Bar * 3, Bar * 3 + 1, Bar * 3 + 479, Bar * 7 - 1 })
        {
            double x = PianoRollGeometry.XAtTick(view, tick);
            double back = PianoRollGeometry.TickAtX(view, x);

            Assert.That(back, Is.EqualTo((double)tick).Within(1e-6), $"tick {tick} 转一圈该回到原处");
        }
    }

    [Test]
    public void 音高到纵坐标再回到音高是同一行()
    {
        foreach (int low in new[] { 0, 21, 48, 96, 120 })
        {
            foreach (int high in new[] { low, low + 1, low + 12, 127 })
            {
                if (high < low) continue;
                var view = View(height: 240, low: low, high: high);

                for (int pitch = low; pitch <= high; pitch++)
                {
                    // 取这一行的中间高度：行边界上的归属是下一行的（半开区间）
                    double y = PianoRollGeometry.YAtPitch(view, pitch) + view.RowHeight / 2;
                    Assert.That(PianoRollGeometry.PitchAtY(view, y), Is.EqualTo(pitch),
                        $"音域 {low}..{high} 音高 {pitch}：纵坐标转一圈该回到同一个音");
                }
            }
        }
    }

    [Test]
    public void 最高音那行贴着标尺()
    {
        var view = View(height: 200, low: 48, high: 72);

        Assert.Multiple(() =>
        {
            Assert.That(PianoRollGeometry.YAtPitch(view, 72), Is.EqualTo(PianoRollGeometry.RulerHeight));
            Assert.That(PianoRollGeometry.PitchAtY(view, PianoRollGeometry.RulerHeight),
                Is.EqualTo(72), "标尺下沿是最高音那行的第一像素");
            // 高音在上：音越高，纵坐标越小
            Assert.That(PianoRollGeometry.YAtPitch(view, 60),
                Is.LessThan(PianoRollGeometry.YAtPitch(view, 59)));
        });
    }

    // ==================== 命中判定 ====================

    [Test]
    public void 命中音符的头尾和身体()
    {
        var view = View(width: 800);
        var box = PianoRollGeometry.BoxOf(view, index: 0, startTick: 0, lengthTicks: 480, pitch: 60, inRange: true);
        double middle = box.Y + box.Height / 2;

        Assert.Multiple(() =>
        {
            Assert.That(PianoRollGeometry.HitTest(box, box.X, middle), Is.EqualTo(PianoRollGeometry.RollHit.Head));
            Assert.That(PianoRollGeometry.HitTest(box, box.X + 1, middle), Is.EqualTo(PianoRollGeometry.RollHit.Head));
            Assert.That(PianoRollGeometry.HitTest(box, box.Right - 1, middle), Is.EqualTo(PianoRollGeometry.RollHit.Tail));
            Assert.That(PianoRollGeometry.HitTest(box, box.X + box.Width / 2, middle),
                Is.EqualTo(PianoRollGeometry.RollHit.Body));
        });
    }

    [Test]
    public void 命中判定的四条边都是半开区间()
    {
        var view = View(width: 800);
        var box = PianoRollGeometry.BoxOf(view, 0, 0, 480, 60, true);
        double middle = box.Y + box.Height / 2;

        Assert.Multiple(() =>
        {
            // 左边缘、上边缘算命中（含）
            Assert.That(PianoRollGeometry.HitTest(box, box.X, box.Y), Is.Not.EqualTo(PianoRollGeometry.RollHit.None));
            // 右边缘、下边缘算不命中（不含）—— 相邻两个音不会同时被命中
            Assert.That(PianoRollGeometry.HitTest(box, box.Right, middle), Is.EqualTo(PianoRollGeometry.RollHit.None));
            Assert.That(PianoRollGeometry.HitTest(box, box.X + 1, box.Bottom), Is.EqualTo(PianoRollGeometry.RollHit.None));
            // 上下各让出的那 0.8 像素也归「没命中」：那是音符块之间的缝
            Assert.That(PianoRollGeometry.HitTest(box, box.X + 1, box.Y - 0.5),
                Is.EqualTo(PianoRollGeometry.RollHit.None));
        });
    }

    [Test]
    public void 很短的音符还能点到身体()
    {
        // 头尾各占 4px 的话，一个 6px 宽的短音上「身体」永远够不着 ——
        // 将来 09 的拖动会变成只能拉时值、拖不动位置。所以窄带上限是宽度的三分之一。
        var view = View(width: 200);
        var box = PianoRollGeometry.BoxOf(view, 0, 0, 12, 60, true);
        double middle = box.Y + box.Height / 2;

        Assert.Multiple(() =>
        {
            Assert.That(box.Width, Is.LessThan(12), "这个用例的前提是块比头尾窄带还窄");
            Assert.That(PianoRollGeometry.HitTest(box, box.X + box.Width / 2, middle),
                Is.EqualTo(PianoRollGeometry.RollHit.Body));
        });
    }

    [Test]
    public void 空白处什么都不命中()
    {
        var view = View(width: 800);
        var box = PianoRollGeometry.BoxOf(view, 0, 0, 480, 60, true);

        Assert.Multiple(() =>
        {
            Assert.That(PianoRollGeometry.HitTest(box, box.X - 1, box.Y + 1),
                Is.EqualTo(PianoRollGeometry.RollHit.None), "左边一点");
            Assert.That(PianoRollGeometry.HitTest(box, box.X + 1, box.Y - 1),
                Is.EqualTo(PianoRollGeometry.RollHit.None), "上边一点");
            Assert.That(PianoRollGeometry.HitTest(box, box.X + 1, box.Bottom + 1),
                Is.EqualTo(PianoRollGeometry.RollHit.None), "下边一点");
        });
    }

    [Test]
    public void 音符块照模子留出缝()
    {
        var view = View(width: 800);
        var box = PianoRollGeometry.BoxOf(view, 0, 0, 480, 60, true);

        Assert.Multiple(() =>
        {
            // 横向让 1px、上下各让 0.8px —— 和 RollPreviewStrip 一模一样，不这样相邻的音会糊成一块
            Assert.That(box.Right, Is.EqualTo(PianoRollGeometry.XAtTick(view, 480) - PianoRollGeometry.NoteGap)
                .Within(1e-9));
            Assert.That(box.Y, Is.EqualTo(PianoRollGeometry.YAtPitch(view, 60) + PianoRollGeometry.NotePad)
                .Within(1e-9));
            Assert.That(box.Height, Is.EqualTo(view.RowHeight - PianoRollGeometry.NotePad * 2).Within(1e-9));
        });
    }

    // ==================== 边界 ====================

    [Test]
    public void 卷帘左边以左夹到曲子开头()
    {
        var view = View(width: 800, viewStart: 0);

        Assert.Multiple(() =>
        {
            Assert.That(PianoRollGeometry.TickAtX(view, -500), Is.EqualTo(0), "负 tick 在谱面上不存在");
            Assert.That(PianoRollGeometry.TickAtX(View(width: 800, viewStart: Bar * 5), -500),
                Is.GreaterThan(0), "视图不在开头时，左边缘以左夹到视图起点，不掉到负数");
        });
    }

    [Test]
    public void 卷帘右边缘之外是空谱面()
    {
        var view = View(width: 800);

        // 不夹：曲子末尾之后就是没音符，硬夹回一个位置等于编一个不存在的落点出来
        Assert.That(PianoRollGeometry.TickAtX(view, 9999), Is.GreaterThan(view.ViewStartTick + Bar * 4));
    }

    [Test]
    public void 纵坐标越界夹到这条轨的音域两端()
    {
        var view = View(height: 200, low: 48, high: 72);

        Assert.Multiple(() =>
        {
            Assert.That(PianoRollGeometry.PitchAtY(view, -1000), Is.EqualTo(72), "标尺上面归最高音");
            Assert.That(PianoRollGeometry.PitchAtY(view, 99999), Is.EqualTo(48), "卷帘下面归最低音");
            Assert.That(PianoRollGeometry.PitchAtY(view, double.NaN), Is.EqualTo(72), "NaN 不该变成垃圾音高");
        });
    }

    [Test]
    public void 尺寸还没量出来时不崩也不乱算()
    {
        var zeroWidth = new PianoRollGeometry.Viewport(0, 200, Bar * 2, Bar, 48, 72);
        var zeroHeight = new PianoRollGeometry.Viewport(800, 0, Bar * 2, Bar, 48, 72);

        Assert.Multiple(() =>
        {
            Assert.That(PianoRollGeometry.TickAtX(zeroWidth, 100), Is.EqualTo(Bar * 2), "宽度为 0 就停在视图起点");
            Assert.That(PianoRollGeometry.PitchAtY(zeroHeight, 100), Is.EqualTo(48), "高度为 0 不至于除出 NaN");
            Assert.That(PianoRollGeometry.XAtTick(zeroWidth, Bar * 2), Is.EqualTo(0).Within(1e-9));
        });
    }

    [Test]
    public void 单音轨的音域不会撑成一整条()
    {
        var (low, high) = PianoRollGeometry.FitPitchRange(60, 60);

        Assert.Multiple(() =>
        {
            Assert.That(high - low + 1, Is.GreaterThanOrEqualTo(6), "只弹一个音也要看得见一行，但别一行占满整条轨");
            Assert.That(low, Is.LessThanOrEqualTo(60));
            Assert.That(high, Is.GreaterThanOrEqualTo(60));
        });
    }

    [Test]
    public void 极宽音域不会越出MIDI两端()
    {
        var (low, high) = PianoRollGeometry.FitPitchRange(0, 127);

        Assert.Multiple(() =>
        {
            Assert.That(low, Is.GreaterThanOrEqualTo(0));
            Assert.That(high, Is.LessThanOrEqualTo(127));
        });
    }

    [Test]
    public void 贴着MIDI两端的窄音域仍然不越界()
    {
        foreach (var (min, max) in new[] { (0, 1), (126, 127), (0, 3), (120, 127) })
        {
            var (low, high) = PianoRollGeometry.FitPitchRange(min, max);

            Assert.Multiple(() =>
            {
                Assert.That(low, Is.GreaterThanOrEqualTo(0), $"音域 {min}..{max}");
                Assert.That(high, Is.LessThanOrEqualTo(127), $"音域 {min}..{max}");
                Assert.That(low, Is.LessThanOrEqualTo(min), $"音域 {min}..{max}：最低音要包得住");
                Assert.That(high, Is.GreaterThanOrEqualTo(max), $"音域 {min}..{max}：最高音要包得住");
            });
        }
    }

    [Test]
    public void 音域两端留了余量()
    {
        var (low, high) = PianoRollGeometry.FitPitchRange(48, 72);

        Assert.Multiple(() =>
        {
            Assert.That(low, Is.LessThan(48), "最低音不该顶着底边");
            Assert.That(high, Is.GreaterThan(72), "最高音不该顶着标尺");
        });
    }

    [Test]
    public void 音域参数颠倒也认()
    {
        Assert.That(PianoRollGeometry.FitPitchRange(72, 48), Is.EqualTo(PianoRollGeometry.FitPitchRange(48, 72)));
    }

    // ==================== 小节刻度 ====================

    [Test]
    public void 小节长度按拍号算()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PianoRollGeometry.BarTicks(Tempo(480, 4, 4)), Is.EqualTo(1920), "4/4");
            Assert.That(PianoRollGeometry.BarTicks(Tempo(480, 3, 4)), Is.EqualTo(1440), "3/4");
            Assert.That(PianoRollGeometry.BarTicks(Tempo(480, 6, 8)), Is.EqualTo(1440), "6/8 的一小节是 6 个八分音符");
            Assert.That(PianoRollGeometry.BarTicks(Tempo(960, 4, 4)), Is.EqualTo(3840), "PPQ 变了小节跟着变");
        });
    }

    [Test]
    public void 没有拍号就按四四拍()
    {
        var map = new TempoMap(TimeDivision.PulsesPerQuarter(480));

        Assert.That(PianoRollGeometry.BarTicks(map), Is.EqualTo(1920));
    }

    [Test]
    public void 小节数向上取整且至少一格()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PianoRollGeometry.BarCount(0, Bar), Is.EqualTo(1), "空曲也得有一格");
            Assert.That(PianoRollGeometry.BarCount(Bar, Bar), Is.EqualTo(1), "正好一小节");
            Assert.That(PianoRollGeometry.BarCount(Bar + 1, Bar), Is.EqualTo(2), "多一个 tick 就是下一小节");
            Assert.That(PianoRollGeometry.BarCount(Bar * 24, Bar), Is.EqualTo(24));
        });
    }

    [Test]
    public void 视图起点永远夹在合法范围里()
    {
        long total = Bar * 24;

        Assert.Multiple(() =>
        {
            Assert.That(PianoRollGeometry.ClampViewStart(-999, total, Bar), Is.EqualTo(0), "左边到底");
            Assert.That(PianoRollGeometry.ClampViewStart(total * 10, total, Bar), Is.EqualTo(total - Bar * 4),
                "右边到底 = 最后 4 小节正好铺满一屏，再往后只会把谱面缩在左边");
            Assert.That(PianoRollGeometry.ClampViewStart(Bar * 3, total, Bar), Is.EqualTo(Bar * 3), "中间不动");
            Assert.That(PianoRollGeometry.ClampViewStart(double.NaN, total, Bar), Is.EqualTo(0), "NaN 不该传下去");
        });
    }

    [Test]
    public void 比一屏还短的曲子视图只能停在开头()
    {
        Assert.That(PianoRollGeometry.ClampViewStart(Bar, Bar * 2, Bar), Is.EqualTo(0));
    }

    // ==================== 导航条吸附 ====================

    [Test]
    public void 导航条的横坐标到tick再回来是同一个位置()
    {
        long total = Bar * 24;

        foreach (double x in new[] { 0, 0.5, 33.25, 260, 519.75, 1040 })
        {
            double tick = PianoRollGeometry.NavTickAtX(x, 1040, total);
            Assert.That(PianoRollGeometry.NavXAtTick(tick, 1040, total), Is.EqualTo(x).Within(1e-6),
                $"横坐标 {x} 转一圈该回到原处");
        }
    }

    [Test]
    public void 导航条上的落点永远吸附到最近的小节线()
    {
        const int bars = 24;
        const double width = 1040;

        for (double x = 0; x <= width; x += 1)
        {
            int bar = PianoRollGeometry.NavBarAtX(x, width, bars);
            double exact = x / width * bars;

            Assert.Multiple(() =>
            {
                // 期望值也要夹：贴着右端的横坐标吸出来是第 25 小节，而小节一共才 24 个
                Assert.That(bar,
                    Is.EqualTo(Math.Clamp((int)Math.Round(exact, MidpointRounding.AwayFromZero), 0, bars - 1)),
                    $"横坐标 {x}：该落在离它最近的那条小节线上");
                Assert.That(bar, Is.InRange(0, bars - 1), $"横坐标 {x}：不该越出首尾两小节");
            });
        }
    }

    [Test]
    public void 拖出导航条两端夹到首尾小节()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PianoRollGeometry.NavBarAtX(-500, 1040, 24), Is.EqualTo(0));
            Assert.That(PianoRollGeometry.NavBarAtX(99999, 1040, 24), Is.EqualTo(23));
            Assert.That(PianoRollGeometry.NavBarAtX(0, 1040, 24), Is.EqualTo(0));
            Assert.That(PianoRollGeometry.NavBarAtX(1040, 1040, 24), Is.EqualTo(23));
        });
    }

    [Test]
    public void 只有一个小节时导航条也点得动()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PianoRollGeometry.NavBarAtX(0, 1040, 1), Is.EqualTo(0));
            Assert.That(PianoRollGeometry.NavBarAtX(9999, 1040, 1), Is.EqualTo(0));
        });
    }

    [Test]
    public void 拖导航条吸附出来的tick一定是小节线()
    {
        foreach (double tick in new[] { 0, 1, 959, 960, 961, 2879, 2880, 12345.6 })
        {
            long snapped = PianoRollGeometry.SnapToBar(tick, Bar);
            Assert.That(snapped % Bar, Is.EqualTo(0), $"tick {tick} 吸附之后该落在小节线上");
        }
    }

    [Test]
    public void 吸附取的是最近的那条而不是前面那条()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PianoRollGeometry.SnapToBar(Bar * 1.4, Bar), Is.EqualTo(Bar), "过了四成往回吸");
            Assert.That(PianoRollGeometry.SnapToBar(Bar * 1.6, Bar), Is.EqualTo(Bar * 2), "过了六成往前吸");
            Assert.That(PianoRollGeometry.SnapToBar(Bar * 1.5, Bar), Is.EqualTo(Bar * 2), "正中时往前");
            Assert.That(PianoRollGeometry.SnapToBar(-100, Bar), Is.EqualTo(0), "吸附不该吸出负数");
            Assert.That(PianoRollGeometry.SnapToBar(double.NaN, Bar), Is.EqualTo(0));
        });
    }

    private static TempoMap Tempo(int tpqn, int numerator, int denominator)
        => new(TimeDivision.PulsesPerQuarter(tpqn), null,
            new[] { new TimeSignatureChange(0, numerator, denominator) });
}
