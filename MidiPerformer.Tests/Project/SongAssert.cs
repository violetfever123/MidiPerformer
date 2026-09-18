using MidiPerformer.Core.Model;
using NUnit.Framework;

namespace MidiPerformer.Tests.Project;

/// <summary>
/// S1 缝的逐字段比较帮手 —— MIDI 那半（<see cref="SongProjectWriteTests"/>）和
/// .mproj 那半（<see cref="SongProjectFileTests"/>）共用一份。
///
/// 为什么不写 <c>song1 == song2</c>：<see cref="Song"/> 刻意只有引用相等
/// （撤销装饰器拿它当「这条命令改没改」的判据），要比内容就只能自己比。
/// <see cref="Track"/> 有值相等（音符**逐个**比），直接用；比不到的只剩速度表那一摊。
///
/// 比较一律**精确**：tick 是整数，不给容差 —— 这是 S1 缝的原话。
/// </summary>
internal static class SongAssert
{
    public static void Same(Song expected, Song actual, string because)
    {
        Assert.That(actual.Tracks, Has.Count.EqualTo(expected.Tracks.Count), $"{because}：轨数");

        for (int i = 0; i < expected.Tracks.Count; i++)
        {
            // Track 的值相等会把轨块序号 / 声道 / 轨名 / 音色 / 移调 / 每个音的四个字段全部比掉，
            // 而 tick 是整数，比的就是精确值，没有容差。
            Assert.That(actual.Tracks[i], Is.EqualTo(expected.Tracks[i]),
                $"{because}：第 {i} 条轨（{expected.Tracks[i].Name}）");
        }

        Assert.Multiple(() =>
        {
            Assert.That(actual.TempoMap.Division, Is.EqualTo(expected.TempoMap.Division), $"{because}：分辨率");
            Assert.That(actual.TempoMap.TempoChanges, Is.EqualTo(expected.TempoMap.TempoChanges),
                $"{because}：速度事件表");
            Assert.That(actual.TempoMap.TimeSignatureChanges, Is.EqualTo(expected.TempoMap.TimeSignatureChanges),
                $"{because}：变拍事件表");
        });
    }
}
