using MidiPerformer.Tests.Corpus;
using NUnit.Framework;
using MidiReader = MidiPerformer.Core.UseCases.Project.MidiReader;

namespace MidiPerformer.Tests.Project;

/// <summary>
/// 读半边与原版 <c>MidiLoader</c> 的对拍。
/// 单独成文件是因为它引用原版工程（并排目录里的 harmonica-auto-player）；
/// 原版不在时 <c>MidiPerformer.Tests.csproj</c> 会把本文件排除在编译之外。
/// </summary>
public partial class SongProjectReadTests
{
    /// <summary>轨的骨架（几条、哪条、叫什么、每个音的音高力度）与原版 <c>MidiLoader</c> 逐条对齐。</summary>
    [TestCaseSource(typeof(MidiCorpus), nameof(MidiCorpus.TestFiles))]
    public void 真实MIDI的轨与原版逐条对齐(string path)
    {
        var expected = HarpAutoPlayer.Midi.MidiLoader.Parse(path).Candidates;
        var song = MidiReader.Read(path);

        Assert.That(song.Tracks.Count, Is.EqualTo(expected.Count),
            $"{Path.GetFileName(path)}：轨数不对（一个轨块里的每个声道算一条轨）");

        for (int i = 0; i < expected.Count; i++)
        {
            var e = expected[i];
            var a = song.Tracks[i];

            Assert.That(a.TrackIndex, Is.EqualTo(e.TrackIndex), $"第 {i} 条轨的轨块序号");
            Assert.That(a.Channel, Is.EqualTo(e.Channel), $"第 {i} 条轨的声道");
            Assert.That(a.Name, Is.EqualTo(e.Name), $"第 {i} 条轨的轨名");
            Assert.That(a.NoteCount, Is.EqualTo(e.Notes.Count), $"第 {i} 条轨的音符数");

            // 音高与力度两边同单位（音符号、0-127），可直接逐条比；原版那边的时间是秒，tick 由另一条测试盯着。
            for (int k = 0; k < e.Notes.Count; k++)
            {
                Assert.That(a.Notes[k].Pitch, Is.EqualTo(e.Notes[k].Pitch), $"第 {i} 条轨第 {k} 个音的音高");
                Assert.That(a.Notes[k].Velocity, Is.EqualTo(e.Notes[k].Velocity), $"第 {i} 条轨第 {k} 个音的力度");
            }
        }
    }
}
