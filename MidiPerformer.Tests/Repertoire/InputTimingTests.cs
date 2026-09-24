using MidiPerformer.Core.UseCases.Perform.Repertoire;
using NUnit.Framework;

namespace MidiPerformer.Tests.Repertoire;

/// <summary>
/// 两档时序档位与界面下拉框的一致性：列表里第几行，和真跑用哪一档。
/// 只钉「两档真的存在、真的互不相同、下标真的对得上」，不测时序参数本身。
/// </summary>
public class InputTimingTests
{
    /// <summary>界面下拉里有几行，就必须有几档 —— 多一行少一行都是错位。</summary>
    [Test]
    public void 下拉框的每一行都对得上一个档位()
    {
        Assert.That(InputTiming.Names, Has.Length.EqualTo(2), "界面上是两档：稳健 / 标准");

        for (int i = 0; i < InputTiming.Names.Length; i++)
        {
            var timing = InputTiming.FromIndex(i);

            Assert.That(InputTiming.Names[i], Does.StartWith(timing.Name),
                $"下拉第 {i} 行的文案和取到的档位对不上 —— 列表里选的和真跑的不是一个档");
        }
    }

    /// <summary>序号回指：0 稳健 / 1 标准。默认档（窗口选的是 1）必须是标准。</summary>
    [Test]
    public void 序号按稳健标准的顺序回指()
    {
        Assert.Multiple(() =>
        {
            Assert.That(InputTiming.FromIndex(0), Is.EqualTo(InputTiming.Safe));
            Assert.That(InputTiming.FromIndex(1), Is.EqualTo(InputTiming.Standard));
        });
    }

    /// <summary>
    /// 下标越界回标准档，不抛、也不给出第三档。
    /// 界面把 <c>SelectedIndex</c> 直接递进来，还没选中任何一项时它是 <c>-1</c>；
    /// <c>2</c> 是**原来的极限档**（界面收成两档之后这个序号不再存在），它照样走兜底而不是抛。
    /// </summary>
    [Test]
    public void 越界的下标回标准档()
    {
        Assert.Multiple(() =>
        {
            Assert.That(InputTiming.FromIndex(-1), Is.EqualTo(InputTiming.Standard));
            Assert.That(InputTiming.FromIndex(2), Is.EqualTo(InputTiming.Standard),
                "序号 2（原来的极限档）不再存在，该走 _ => 兜底回标准档");
            Assert.That(InputTiming.FromIndex(3), Is.EqualTo(InputTiming.Standard));
            Assert.That(InputTiming.FromIndex(int.MaxValue), Is.EqualTo(InputTiming.Standard));
        });
    }

    /// <summary>
    /// 两档是两个不同的档，而且余量按 稳健 &gt; 标准 递减。
    /// 防的是复制粘贴：新加一档时忘了改数，两档参数一模一样，下拉框里却看着是两个选项。
    /// </summary>
    [Test]
    public void 两档是两个不一样的档而且余量递减()
    {
        var tiers = new[] { InputTiming.Safe, InputTiming.Standard };

        Assert.Multiple(() =>
        {
            Assert.That(tiers.Distinct().Count(), Is.EqualTo(2), "两档的参数一模一样 —— 多半是复制粘贴漏改了数");
            Assert.That(tiers[0].LeadMs, Is.GreaterThan(tiers[1].LeadMs));
            Assert.That(tiers[0].FrameMs, Is.GreaterThan(tiers[1].FrameMs));
        });
    }

    /// <summary>
    /// 两档都要满足 <c>LeadMs ≥ ModLeadMs + FrameMs</c>：提前量盖不住「修饰键提前 + 一帧」的话，
    /// 修饰键和音键会被游戏折进同一帧，那正是漏音。
    /// </summary>
    [Test]
    public void 两档的提前量都盖得住修饰键加一帧()
    {
        foreach (var tier in new[] { InputTiming.Safe, InputTiming.Standard })
            Assert.That(tier.LeadMs, Is.GreaterThanOrEqualTo(tier.ModLeadMs + tier.FrameMs),
                $"{tier.Name}档：LeadMs {tier.LeadMs} < ModLeadMs {tier.ModLeadMs} + FrameMs {tier.FrameMs}");
    }
}
