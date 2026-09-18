using MidiPerformer.Adapters.Gateways;
using NUnit.Framework;

namespace MidiPerformer.Tests.Perform;

/// <summary>
/// <c>SendInput</c> 那个联合体的**布局**。
///
/// 这不是网关的行为测试（spec 把网关划进「明确不测」那一类，是对的：发没发出去、
/// 游戏认没认，只有真机看得见）。这里验的是纯结构事实：64 位下 <c>INPUT</c> 必须是 40 字节。
///
/// 值得单独守一条，是因为这个错**不会报错**：尺寸对不上时 <c>SendInput</c> 按 <c>cbSize</c>
/// 直接拒收，返回值 0，一个事件都不发。演奏时的表现是「点了开始，一切正常，就是游戏里没动静」——
/// 这是最难查的一类毛病。少一个字段、少一层对齐填充，尺寸就变了。
/// </summary>
public class InputSenderLayoutTests
{
    [Test]
    public void INPUT联合体在64位下是40字节()
    {
        Assert.That(Environment.Is64BitProcess, Is.True,
            "本程序只发 win-x64（见 spec 的构建一节），这条断言在别的位宽下没有意义");

        // 读这个属性本身就会触发 InputSender 的静态构造 —— 那里也有一道同样的检查，
        // 是给发布产物用的（exe 跑不了测试，见 11 的自检）。
        Assert.That(InputSender.InputStructSize, Is.EqualTo(40));
    }
}
