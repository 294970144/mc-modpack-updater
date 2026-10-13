using Xunit;

namespace McUpdater.Tests;

/// <summary>
/// 展示格式化的保真测试（Node <c>public/js/ui.js</c> 的 <c>bytes</c> / <c>shortPath</c>）。
///
/// <para><b>期望值怎么来的</b>：全部由 Node 原版实跑生成，不是手算。
/// 见 <c>tools/gen-baseline.js</c> 的 <c>bytes</c> / <c>shortPath</c> 段。</para>
/// </summary>
public class FormatTests
{
    // ------------------------------------------------------------------
    // Bytes
    // ------------------------------------------------------------------

    /// <summary>
    /// Node <c>bytes</c> 的核心不对称规则：
    /// <c>(i === 0 ? v : v.toFixed(v &lt; 10 ? 1 : 0))</c>。
    /// 小于 10 保留 1 位小数，大于等于 10 取整 —— 这条最容易在移植时"顺手统一"。
    /// </summary>
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1, "1 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(10239, "10.0 KB")]      // 10.0 而非 10：v<10 判定的是除后值，10.0 已不小于 10
    [InlineData(10240, "10 KB")]
    [InlineData(15360, "15 KB")]
    // 1023.999 KB取整成 1024 —— 看着像 bug，实为原版行为：只判一次 1024 就不再进位
    [InlineData(1048575, "1024 KB")]
    [InlineData(1048576, "1.0 MB")]
    [InlineData(1572864, "1.5 MB")]
    [InlineData(10485760, "10 MB")]
    [InlineData(237489475, "226 MB")]    // Master 提供的真实包大小
    [InlineData(1073741824, "1.0 GB")]
    [InlineData(2147483648, "2.0 GB")]
    public void Bytes_与Node原版一致(long input, string expected)
    {
        Assert.Equal(expected, Core.Format.Bytes(input));
    }

    [Fact]
    public void Bytes_超过GB不再扩展单位()
    {
        // Node 的 units 只有 4 档，超出部分不再往上进位
        Assert.Equal("1024 GB", Core.Format.Bytes(1024L * 1024 * 1024 * 1024));
    }

    // ------------------------------------------------------------------
    // ShortPath
    // ------------------------------------------------------------------

    [Fact]
    public void ShortPath_未超长原样返回()
    {
        Assert.Equal("C:/a/b.zip", Core.Format.ShortPath("C:/a/b.zip", 58));
    }

    [Fact]
    public void ShortPath_恰好等于上限不加省略号()
    {
        // 边界：p.length <= max 时直接返回，不是 < max
        var p = new string('x', 58);
        Assert.Equal(p, Core.Format.ShortPath(p, 58));
    }

    [Fact]
    public void ShortPath_超长时中间省略且头尾都保留()
    {
        var p = "C:/very/long/path/to/some/整合包/1.21.11 1.0.6.zip";
        var got = Core.Format.ShortPath(p, 46);

        // 长度守恒：头部 + 单字符省略号 + 尾部
        Assert.Equal(46, got.Length);
        Assert.Contains('…', got);

        // 【关键】尾巴必须完整保留 —— 版本名/文件名是用户唯一认得出的部分
        Assert.EndsWith("1.21.11 1.0.6.zip", got);

        // 头部保留 keepHead = max - floor(max*0.6) - 1 个字符
        var keepTail = (int)(46 * 0.6);           // 27
        var keepHead = 46 - keepTail - 1;         // 18
        Assert.StartsWith("C:/very/long/path/", got[..keepHead]);
    }

    [Fact]
    public void ShortPath_空值不崩()
    {
        Assert.Equal("", Core.Format.ShortPath(null, 46));
        Assert.Equal("", Core.Format.ShortPath("", 46));
    }
}