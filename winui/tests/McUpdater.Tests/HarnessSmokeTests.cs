namespace McUpdater.Core.Tests;

/// <summary>
/// 测试台自检：确认 xunit 工具链可用、且 Core 反向链接机制成立。
///
/// 这不是业务测试，是**地基测试**。在写任何 Core 代码之前先跑它，
/// 目的是把「环境问题」与「代码问题」分开：
/// 万一后面编译失败，我能立刻断定是 Core 代码写错了，
/// 而不是 NuGet 没拉下来 / 测试台配置不对。
/// </summary>
public class HarnessSmokeTests
{
    [Fact]
    public void 测试台能够发现并运行断言()
    {
        // 1 + 1 = 2 显然成立；真正被验证的是「xunit 真的发现并跑了这个测试」
        Assert.Equal(2, 1 + 1);
    }

    [Fact]
    public void 故意失败的断言应当抛出_用于确认断言真的在生效()
    {
        // 这条测试的正确结果是「失败」。如果它反而通过了，
        // 说明 Assert 那层根本没生效，测试台是坏的 —— 那才是真正要担心的事。
        //
        // 显式转成 Action：直接写 `() => throw new ...` 会被编译器
        // 匹配到 Assert.Throws<T>(Func<Task>) 这个已过时重载（CS0619）。
        // 那是个容易踩的坑 —— 报错信息说的是"异步测试要用 ThrowsAsync"，
        // 与实际问题毫无关系。
        Action act = () => throw new InvalidOperationException("boom");
        Assert.Throws<InvalidOperationException>(act);
    }
}
