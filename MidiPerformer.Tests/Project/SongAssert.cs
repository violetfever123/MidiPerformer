using MidiPerformer.Core.Model;
using NUnit.Framework;

namespace MidiPerformer.Tests.Project;

/// <summary>
/// 逐字段比较两份 <see cref="Song"/> 的帮手。
/// <see cref="Song"/> 只有引用相等，比内容只能自己比；<see cref="Track"/> 有值相等，直接用。
/// 比较一律精确，不给容差。
/// </summary>
internal static class SongAssert
{
    public static void Same(Song expected, Song actual, string because)
    {
        Assert.That(actual.Tracks, Has.Count.EqualTo(expected.Tracks.Count), $"{because}：轨数");

        for (int i = 0; i < expected.Tracks.Count; i++)
        {
            // Track 的值相等会连轨块序号 / 声道 / 轨名 / 音色 / 移调 / 每个音的内容字段一起比掉，tick 比的是精确值。
            // 身份（Note.Id）不在里面，要比身份的地方自己比。
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
