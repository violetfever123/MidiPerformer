using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Project;
using MidiPerformer.Core.UseCases.Timeline;
using MidiPerformer.Tests.Corpus;
using NUnit.Framework;

namespace MidiPerformer.Tests.Timeline;

/// <summary>
/// 音乐时间 ⇄ 物理时间的积分器：纯数学，物理时刻由测试喂进去，一行真时间都不用等。
/// </summary>
public class SongWalkerTests
{
    // ==================== 两把尺子互为逆运算 ====================

    [TestCase(0.25)]
    [TestCase(0.5)]
    [TestCase(1.0)]
    [TestCase(1.5)]
    [TestCase(2.0)]
    [TestCase(4.0)]
    public void 音乐时间与物理时间互为逆运算(double speed)
    {
        var walker = new SongWalker(变速曲(), speed);
        walker.Seek(1.3, 100.0);

        Assert.Multiple(() =>
        {
            // 锚点本身：两个方向都该原样答出来
            Assert.That(walker.MusicAt(100.0), Is.EqualTo(1.3).Within(1e-12));
            Assert.That(walker.PhysicalAt(1.3), Is.EqualTo(100.0).Within(1e-9));

            foreach (double physical in new[] { 100.0, 100.5, 103.0, 137.25 })
            {
                double music = walker.MusicAt(physical);
                Assert.That(walker.PhysicalAt(music), Is.EqualTo(physical).Within(1e-9),
                    $"倍速 {speed}：物理 {physical} → 音乐 {music} → 物理回来对不上");
            }

            foreach (double music in new[] { 0.0, 0.4, 1.3, 2.0, 3.25 })
            {
                double physical = walker.PhysicalAt(music);
                Assert.That(walker.MusicAt(physical), Is.EqualTo(music).Within(1e-9),
                    $"倍速 {speed}：音乐 {music} → 物理 {physical} → 音乐回来对不上");
            }
        });
    }

    [TestCase(2.0)]
    [TestCase(0.5)]
    public void 走一段之后两把尺子仍然互为逆运算(double speed)
    {
        var walker = new SongWalker(变速曲(), speed);
        walker.Start(0.0);
        walker.AdvanceTo(1.7);

        double musicNow = walker.MusicNow;

        Assert.Multiple(() =>
        {
            Assert.That(walker.MusicAt(1.7), Is.EqualTo(musicNow).Within(1e-12), "推进之后的当下音乐时间");
            Assert.That(musicNow, Is.EqualTo(1.7 * speed).Within(1e-12), "恒定倍速下音乐时间是物理时间的线性函数");
            Assert.That(walker.PhysicalAt(musicNow), Is.EqualTo(1.7).Within(1e-9));
        });
    }

    // ==================== 曲子自己的变速与倍速是两回事 ====================

    [Test]
    public void 变速曲目在原速下走四秒音乐时间就是四秒()
    {
        // 这首曲子在音乐时间 2.0s 处变速（120BPM → 240BPM）；音乐时间不含播放倍速。
        var walker = new SongWalker(变速曲(), speed: 1.0);
        walker.Start(0.0);

        walker.AdvanceTo(4.0);

        Assert.Multiple(() =>
        {
            Assert.That(walker.MusicNow, Is.EqualTo(4.0).Within(1e-12), "曲子变速不该改变音乐时间的流速");
            Assert.That(walker.TotalMusicSeconds, Is.EqualTo(3.25).Within(1e-12),
                "总时长 = 2.0s（变速前 4 个四分音符） + 1.25s（变速后 5 个）");
        });
    }

    [Test]
    public void 倍速翻倍音乐时间走得快一倍()
    {
        var normal = new SongWalker(变速曲(), 1.0);
        var fast = new SongWalker(变速曲(), 2.0);
        normal.Start(0.0);
        fast.Start(0.0);

        normal.AdvanceTo(1.5);
        fast.AdvanceTo(1.5);

        Assert.That(fast.MusicNow, Is.EqualTo(normal.MusicNow * 2).Within(1e-12));
    }

    [Test]
    public void 倍速为零或负数时退回原速()
    {
        // 倍速 0 会让 PhysicalAt 除以零，负数是时间倒流
        var walker = new SongWalker(变速曲(), 0.0);
        Assert.That(walker.Speed, Is.EqualTo(1.0));

        walker.SetSpeed(-3.0, 0.0);
        Assert.That(walker.Speed, Is.EqualTo(1.0));

        walker.Start(0.0);
        walker.AdvanceTo(1.0);
        Assert.That(walker.MusicNow, Is.EqualTo(1.0).Within(1e-12), "退回原速之后应当正常往前走");
        Assert.That(double.IsFinite(walker.PhysicalAt(1.0)), Is.True);
    }

    /// <summary>
    /// NaN 和 ±∞ 与 0 / 负数一样，一律退回原速。
    /// NaN 要单独照顾：<c>NaN &lt;= 0</c> 是 false，只写 <c>speed &lt;= 0</c> 的守卫会放行它。
    /// </summary>
    [TestCase(double.NaN)]
    [TestCase(double.PositiveInfinity)]
    [TestCase(double.NegativeInfinity)]
    [TestCase(0.0)]
    [TestCase(-1.0)]
    public void 坏倍速一律退回原速(double bad)
    {
        var walker = new SongWalker(变速曲(), bad);
        Assert.That(walker.Speed, Is.EqualTo(1.0));

        walker.Start(0.0);
        walker.AdvanceTo(2.0);

        Assert.Multiple(() =>
        {
            Assert.That(double.IsFinite(walker.MusicNow), Is.True, $"倍速 {bad} 把音乐时间污染成了 {walker.MusicNow}");
            Assert.That(walker.MusicNow, Is.EqualTo(2.0).Within(1e-12));
            Assert.That(walker.TickNow, Is.EqualTo(变速曲().TempoMap.TickAt(2.0)));
            Assert.That(walker.Finished, Is.False, "才走了 2 秒就不该说结束了");
            // 倍速 0 会让这里除以零得 ∞，正是退回原速要防的
            Assert.That(walker.PhysicalAt(3.0), Is.EqualTo(3.0).Within(1e-9));
        });
    }

    [Test]
    public void 播放中改成坏倍速也不会把播放头弄坏()
    {
        var walker = new SongWalker(变速曲(), 1.0);
        walker.Start(0.0);
        walker.AdvanceTo(1.0);

        walker.SetSpeed(double.NaN, 1.0);

        Assert.Multiple(() =>
        {
            Assert.That(walker.Speed, Is.EqualTo(1.0));
            Assert.That(walker.MusicNow, Is.EqualTo(1.0).Within(1e-12), "改速那一下不该动播放头");
        });

        walker.AdvanceTo(3.0);
        Assert.That(walker.MusicNow, Is.EqualTo(3.0).Within(1e-12), "退回原速之后照常往前走");
    }

    // ==================== 改倍速不跳 ====================

    [Test]
    public void 改倍速时播放头不倒退也不前跳()
    {
        var walker = new SongWalker(变速曲(), 1.0);
        walker.Start(0.0);
        walker.AdvanceTo(2.0);

        double before = walker.MusicNow;
        walker.SetSpeed(3.0, 2.0);
        double after = walker.MusicNow;

        Assert.Multiple(() =>
        {
            Assert.That(after, Is.EqualTo(before).Within(1e-12), "改倍速的那一瞬间播放头必须原地不动");
            Assert.That(walker.Speed, Is.EqualTo(3.0));

            walker.AdvanceTo(3.0);
            Assert.That(walker.MusicNow, Is.EqualTo(before + 3.0).Within(1e-12), "之后按新倍速走");
        });
    }

    [Test]
    public void 连续改倍速不会累积漂移()
    {
        var walker = new SongWalker(变速曲(), 1.0);
        walker.Start(0.0);

        double expected = 0;
        double physical = 0;
        foreach (double speed in new[] { 2.0, 0.5, 1.0, 4.0, 0.25 })
        {
            walker.SetSpeed(speed, physical);
            expected += 1.0 * speed;          // 每档都走 1 秒物理时间
            physical += 1.0;
            walker.AdvanceTo(physical);
        }

        Assert.That(walker.MusicNow, Is.EqualTo(expected).Within(1e-12));
    }

    // ==================== 跳转（seek） ====================

    [TestCase(0.0)]
    [TestCase(0.75)]
    [TestCase(2.0)]    // 正好落在变速点上
    [TestCase(2.6)]    // 变速点之后
    [TestCase(3.25)]   // 曲尾
    public void 能从任意位置起播(double musicSeconds)
    {
        var song = 变速曲();
        var walker = new SongWalker(song, 1.0);

        walker.Seek(musicSeconds, physicalNow: 500.0);

        Assert.Multiple(() =>
        {
            Assert.That(walker.MusicNow, Is.EqualTo(musicSeconds).Within(1e-12));
            Assert.That(walker.TickNow, Is.EqualTo(song.TempoMap.TickAt(musicSeconds)), "当前 tick 要跟着跳过去");
            Assert.That(walker.MusicAt(500.0), Is.EqualTo(musicSeconds).Within(1e-12), "跳转当刻不该带着旧锚点算");
            Assert.That(walker.PhysicalAt(musicSeconds), Is.EqualTo(500.0).Within(1e-9));
        });
    }

    [Test]
    public void 跳转不会把跳过的时长补回来()
    {
        var walker = new SongWalker(变速曲(), 1.0);
        walker.Start(0.0);
        walker.AdvanceTo(1.0);

        walker.Seek(2.5, physicalNow: 1.0);
        walker.AdvanceTo(1.5);

        Assert.That(walker.MusicNow, Is.EqualTo(3.0).Within(1e-12),
            "跳转之后只该按新锚点走 0.5 秒，不能把跳过的 1.5 秒也算进去");
    }

    [Test]
    public void 跳到零之前被夹回开头()
    {
        var walker = new SongWalker(变速曲(), 1.0);
        walker.Seek(2.0, 0.0);

        walker.Seek(-5.0, 0.0);

        Assert.That(walker.MusicNow, Is.EqualTo(0.0));
        Assert.That(walker.TickNow, Is.EqualTo(0));
    }

    // ==================== 变速点处不跳变 ====================

    [Test]
    public void 变速点处tick连续不跳变()
    {
        var walker = new SongWalker(变速曲(), 1.0);
        walker.Start(0.0);

        long previous = walker.TickNow;
        double previousMusic = 0;
        long biggestStep = 0;

        // 以 1ms 的步长走完整曲（3.25 秒）；变速点在音乐时间 2.0s 处
        for (int ms = 1; ms <= 3250; ms++)
        {
            double physical = ms / 1000.0;
            walker.AdvanceTo(physical);

            long now = walker.TickNow;
            long step = now - previous;

            Assert.That(step, Is.GreaterThanOrEqualTo(0), $"{physical}s 处播放头倒退了");
            Assert.That(walker.MusicNow, Is.GreaterThanOrEqualTo(previousMusic), $"{physical}s 处音乐时间倒退了");

            if (step > biggestStep) biggestStep = step;
            previous = now;
            previousMusic = walker.MusicNow;
        }

        // 最快一档 240BPM = 4 个四分音符/秒，480 tick/四分音符 → 1ms ≈ 1.92 tick；
        // 6 留了 3 倍余量，超过就是分段边界处被算重或算漏了。
        Assert.That(biggestStep, Is.LessThanOrEqualTo(6), "变速点处 tick 跳了");
    }

    [Test]
    public void 变速点两侧tick的推进速度确实不同()
    {
        // 上一条只证明不跳，这一条证明换挡真的发生了
        var song = 变速曲();
        var map = song.TempoMap;

        long beforeStep = map.TickAt(1.5) - map.TickAt(1.4);   // 变速前 120BPM
        long afterStep = map.TickAt(2.5) - map.TickAt(2.4);    // 变速后 240BPM

        Assert.That(afterStep, Is.GreaterThan(beforeStep * 1.5),
            "变速之后每 0.1 秒跨过的 tick 应当明显更多");
    }

    // ==================== 走到头 ====================

    [Test]
    public void 走到曲尾之前都不算结束()
    {
        var walker = new SongWalker(变速曲(), 1.0);
        walker.Start(0.0);

        Assert.That(walker.Finished, Is.False, "刚开头就说结束了");

        // 用 Seek 直接落到两个点上，避开累加误差
        double total = walker.TotalMusicSeconds;

        walker.Seek(total - 0.01, 0.0);
        Assert.That(walker.Finished, Is.False, "还差一点点就说结束了");

        walker.Seek(total, 0.0);
        Assert.That(walker.Finished, Is.True);

        walker.Seek(total + 10, 0.0);
        Assert.That(walker.Finished, Is.True, "走过头了仍然是结束");
    }

    [Test]
    public void 没人喂物理时刻就不会自己走()
    {
        // 不读时钟；查询本身不能改变状态
        var walker = new SongWalker(变速曲(), 2.0);
        walker.Start(10.0);

        for (int i = 0; i < 5; i++)
        {
            _ = walker.MusicAt(99.0);
            _ = walker.PhysicalAt(2.0);
            _ = walker.TickNow;
        }

        Assert.Multiple(() =>
        {
            Assert.That(walker.MusicNow, Is.EqualTo(0.0), "查询不该动状态");
            Assert.That(walker.MusicAt(10.0), Is.EqualTo(0.0));
        });
    }

    // ==================== 曲尾是可以搬动的 ====================

    /// <summary>没调过 <see cref="SongWalker.SetEndTick"/> 时，曲尾就是整份谱面的末尾。</summary>
    [Test]
    public void 没搬过曲尾时就是整份谱面的时长()
    {
        var song = 变速曲();
        var walker = new SongWalker(song, 1.0);

        Assert.Multiple(() =>
        {
            Assert.That(walker.EndTick, Is.EqualTo(song.EndTick));
            Assert.That(walker.TotalMusicSeconds, Is.EqualTo(3.25).Within(1e-12));
        });
    }

    /// <summary>
    /// 搬曲尾只改时长：积分不动、播放头一个 tick 都不跳。
    /// 搬短之后当前位置落在曲子外面，<see cref="SongWalker.Finished"/> 当场就是 true。
    /// </summary>
    [Test]
    public void 搬曲尾不动播放头但会当场算走到头()
    {
        var walker = new SongWalker(变速曲(), 1.0);
        walker.Seek(2.5, 0.0);
        long tickBefore = walker.TickNow;

        walker.SetEndTick(1920);   // 曲子砍到 1920 tick = 音乐时间 2.0s

        Assert.Multiple(() =>
        {
            Assert.That(walker.MusicNow, Is.EqualTo(2.5).Within(1e-12), "积分一个字节都没动");
            Assert.That(walker.TickNow, Is.EqualTo(tickBefore), "播放头没跳");
            Assert.That(walker.TotalMusicSeconds, Is.EqualTo(2.0).Within(1e-12));
            Assert.That(walker.Finished, Is.True, "停在曲子外面 = 已经走到头了");
        });
    }

    /// <summary>搬回去时长跟着回去。</summary>
    [Test]
    public void 曲尾搬回去时长跟着回去()
    {
        var song = 变速曲();
        var walker = new SongWalker(song, 1.0);

        walker.SetEndTick(1920);
        Assert.That(walker.TotalMusicSeconds, Is.EqualTo(2.0).Within(1e-12));

        walker.SetEndTick(song.EndTick);
        Assert.Multiple(() =>
        {
            Assert.That(walker.TotalMusicSeconds, Is.EqualTo(3.25).Within(1e-12));
            Assert.That(walker.Finished, Is.False, "停在开头，搬回去就不该再说到头了");
        });
    }

    /// <summary>负数当 0：空曲就是 0 长度的曲子，秒数不该变成负的。</summary>
    [Test]
    public void 曲尾给负数当零()
    {
        var walker = new SongWalker(变速曲(), 1.0);

        walker.SetEndTick(-5);

        Assert.Multiple(() =>
        {
            Assert.That(walker.EndTick, Is.EqualTo(0));
            Assert.That(walker.TotalMusicSeconds, Is.EqualTo(0));
            Assert.That(walker.Finished, Is.True);
        });
    }

    // ==================== 语料 ====================

    [Test]
    public void 真实变速曲目上积分是单调的()
    {
        MidiCorpus.AssertCorpusPresent();

        int checkedFiles = 0;
        foreach (var path in MidiCorpus.VariableTempoPaths.Take(5))
        {
            var song = MidiReader.Read(path);
            if (song.TotalSeconds <= 0) continue;

            var walker = new SongWalker(song, 1.0);
            walker.Start(0.0);

            long previous = 0;
            // 整曲切成 200 步走完（2 tick 容累加漂移）
            for (int i = 1; i <= 200; i++)
            {
                walker.AdvanceTo(song.TotalSeconds * i / 200.0);
                Assert.That(walker.TickNow, Is.GreaterThanOrEqualTo(previous),
                    $"{Path.GetFileName(path)}：第 {i} 步播放头倒退了");
                previous = walker.TickNow;
            }

            Assert.That((double)previous, Is.EqualTo(song.EndTick).Within(2),
                $"{Path.GetFileName(path)}：走到曲尾时 tick 应当落在 EndTick 附近");
            checkedFiles++;
        }

        Assert.That(checkedFiles, Is.GreaterThan(0), "一首变速曲都没测到");
    }

    /// <summary>
    /// 手工造的变速曲：480 tick/四分音符，第 1920 tick 起 120BPM → 240BPM，
    /// 音乐时间 0–2.0s 是第一档，之后是第二档，总长 3.25s。
    /// </summary>
    private static Song 变速曲() => MidiReader.ReadBytes(SmfWriter.Build(1, 480,
        SmfTrack.Named("旋律")
            .Tempo(0, 500_000)
            .Tempo(1920, 250_000)
            .Note(0, 480, 0, 60)
            .Note(1920, 480, 0, 62)
            .Note(3840, 480, 0, 64)));
}
