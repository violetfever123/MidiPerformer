using MidiPerformer.Core.UseCases.Perform.Repertoire;
using NUnit.Framework;

namespace MidiPerformer.Tests.Repertoire;

/// <summary>
/// 三档时序档位与界面下拉框的一致性：列表里第几行，和真跑用哪一档。
/// 只钉「三档真的存在、真的互不相同、下标真的对得上」，不测时序参数本身。
/// </summary>
public class InputTimingTests
{
    /// <summary>界面下拉里有几行，就必须有几档 —— 多一行少一行都是错位。</summary>
    [Test]
    public void 下拉框的每一行都对得上一个档位()
    {
        Assert.That(InputTiming.Names, Has.Length.EqualTo(3), "界面上是三档：稳健 / 标准 / 极限");

        for (int i = 0; i < InputTiming.Names.Length; i++)
        {
            var timing = InputTiming.FromIndex(i);

            Assert.That(InputTiming.Names[i], Does.StartWith(timing.Name),
                $"下拉第 {i} 行的文案和取到的档位对不上 —— 列表里选的和真跑的不是一个档");
        }
    }

    /// <summary>序号回指：0 稳健 / 1 标准 / 2 极限。默认档（窗口选的是 1）必须是标准。</summary>
    [Test]
    public void 序号按稳健标准极限的顺序回指()
    {
        Assert.Multiple(() =>
        {
            Assert.That(InputTiming.FromIndex(0), Is.EqualTo(InputTiming.Safe));
            Assert.That(InputTiming.FromIndex(1), Is.EqualTo(InputTiming.Standard));
            Assert.That(InputTiming.FromIndex(2), Is.EqualTo(InputTiming.Aggressive));
        });
    }

    /// <summary>
    /// 下标越界回标准档，不抛、也不给出第四档。
    /// 界面把 <c>SelectedIndex</c> 直接递进来，还没选中任何一项时它是 <c>-1</c>。
    /// </summary>
    [Test]
    public void 越界的下标回标准档()
    {
        Assert.Multiple(() =>
        {
            Assert.That(InputTiming.FromIndex(-1), Is.EqualTo(InputTiming.Standard));
            Assert.That(InputTiming.FromIndex(3), Is.EqualTo(InputTiming.Standard));
            Assert.That(InputTiming.FromIndex(int.MaxValue), Is.EqualTo(InputTiming.Standard));
        });
    }

    /// <summary>
    /// 三档是三个不同的档，而且余量按 稳健 &gt; 标准 &gt; 极限 递减。
    /// 防的是复制粘贴：新加一档时忘了改数，两档参数一模一样，下拉框里却看着是两个选项。
    /// </summary>
    [Test]
    public void 三档是三个不一样的档而且余量递减()
    {
        var tiers = new[] { InputTiming.Safe, InputTiming.Standard, InputTiming.Aggressive };

        Assert.Multiple(() =>
        {
            Assert.That(tiers.Distinct().Count(), Is.EqualTo(3), "有两档的参数一模一样 —— 多半是复制粘贴漏改了数");
            Assert.That(tiers[0].LeadMs, Is.GreaterThan(tiers[1].LeadMs));
            Assert.That(tiers[1].LeadMs, Is.GreaterThan(tiers[2].LeadMs), "稳健 → 标准 → 极限，余量该一档比一档紧");
            Assert.That(tiers[0].FrameMs, Is.GreaterThan(tiers[1].FrameMs));
            Assert.That(tiers[1].FrameMs, Is.GreaterThan(tiers[2].FrameMs));
        });
    }
}
