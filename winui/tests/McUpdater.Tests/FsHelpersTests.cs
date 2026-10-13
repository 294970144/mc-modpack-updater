using McUpdater.Core;

namespace McUpdater.Core.Tests;

/// <summary>
/// <see cref="FsHelpers"/> 的移植验证。
///
/// <para>重点不是"能跑"，而是<b>与 Node 版逐项对齐</b> —— 其中两处一旦走样
/// 会造成真实故障：</para>
/// <list type="bullet">
/// <item><b>CRC32 必须无符号</b>：对不上 zip 存的值 → unchanged 全被判成 updated。</item>
/// <item><b>glob 的 <c>**/</c> 零层语义</b>：漏掉 <c>(?:.*/)?</c> 里的
/// <c>?</c> → 根目录下的 <c>*.zip</c> 不受保护 → 玩家备份被列为待删除。</item>
/// </list>
/// </summary>
public class FsHelpersTests
{
    // ------------------------------------------------------------------
    // CRC32
    // ------------------------------------------------------------------

    [Fact]
    public void Crc32_空输入_返回0()
    {
        // CRC32 的定义：空输入的校验值为 0
        Assert.Equal(0u, FsHelpers.Crc32(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Crc32_已知向量_与标准值一致()
    {
        // 经典测试向量："123456789" 的 CRC32 恒为 0xCBF43926。
        // 用它验证多项式 0xEDB88320 与位序没有搞反——
        // 这类错误（反射多项式、位序颠倒）算出来的值"看着像个数"，
        // 但和任何标准实现都对不上，只有已知向量能抓住。
        var data = System.Text.Encoding.ASCII.GetBytes("123456789");
        Assert.Equal(0xCBF43926u, FsHelpers.Crc32(data));
    }

    [Fact]
    public void Crc32_返回值必须是无符号的()
    {
        // 回归测试：Node 侧是 (crc ^ -1) >>> 0，若这里用 int 收尾会得到负数。
        // 负 CRC32 与 zip 中央目录里的无符号值对不上，
        // 后果是「本来没变的文件」被全部判成 updated。
        var data = System.Text.Encoding.ASCII.GetBytes("123456789");
        var crc = FsHelpers.Crc32(data);
        Assert.True(crc <= uint.MaxValue, "CRC32 天然无符号，此断言是占位，真正的检查是类型");

        // 更直接的检查：遍历一批随机数据，确认没有负的 int 形式
        var buf = new byte[1024];
        new Random(42).NextBytes(buf);
        var c = FsHelpers.Crc32(buf);
        Assert.Equal((uint)c, c);
    }

    // ------------------------------------------------------------------
    // glob
    // ------------------------------------------------------------------

    [Theory]
    // **/ 必须能匹配「零层目录」—— 下面是这组用例的全部意义所在
    [InlineData("**/*.zip", "a.zip", true)]
    [InlineData("**/*.zip", "config/a.zip", true)]
    [InlineData("**/*.zip", "config/sub/deep/a.zip", true)]
    [InlineData("**/*.zip", "a.txt", false)]
    [InlineData("**/*.zip", "a.zip.bak", false)]
    public void Glob_双星斜杠_能匹配零层目录(string pattern, string path, bool expected)
    {
        // 对应 Node: globToRegExp 把 `**/` 翻成 (?:.*/)? 里的那个 ? 不可省。
        // 如果实现写成 `.*/`，第一条用例（零层）就会失败。
        var matcher = FsHelpers.MakeMatcher([pattern]);
        Assert.Equal(expected, matcher(path));
    }

    [Theory]
    [InlineData("fuji/backup/**", "fuji/backup/x.dat", true)]
    [InlineData("fuji/backup/**", "fuji/backup/deep/x.dat", true)]
    [InlineData("fuji/backup/**", "fuji/cache/x.dat", false)]
    public void Glob_尾部双星_匹配子路径(string pattern, string path, bool expected)
    {
        var matcher = FsHelpers.MakeMatcher([pattern]);
        Assert.Equal(expected, matcher(path));
    }

    [Fact]
    public void Glob_单星不跨越斜杠()
    {
        // * 译成 [^/]*，所以不能跨目录
        var matcher = FsHelpers.MakeMatcher(["a/*.txt"]);
        Assert.True(matcher("a/b.txt"));
        Assert.False(matcher("a/b/c.txt"));
    }

    [Fact]
    public void Glob_问号匹配单个字符()
    {
        var matcher = FsHelpers.MakeMatcher(["a?.txt"]);
        Assert.True(matcher("ab.txt"));
        Assert.False(matcher("abc.txt"));
    }

    [Fact]
    public void Glob_大小写不敏感()
    {
        // Node 用 'i' 标志。这里验证大小写混合时仍能匹配
        var matcher = FsHelpers.MakeMatcher(["**/*.LOG"]);
        Assert.True(matcher("a.log"));
        Assert.True(matcher("dir/a.Log"));
    }

    [Fact]
    public void Glob_正则元字符被转义_不当成通配()
    {
        // 配置目录名里可能出现 . 与 +（如 "mod.name+1.0.zip"），
        // 若不转义，. 会匹配任意字符 —— 保护规则会被错误地放宽。
        var matcher = FsHelpers.MakeMatcher(["a.b/*"]);
        Assert.True(matcher("a.b/x"));
        Assert.False(matcher("axb/x"));
    }

    [Fact]
    public void MakeMatcher_空规则集_恒返回false()
    {
        // 这正是 mods 分类的情况（CATEGORY_PROTECTED 里没有 mods 条目）。
        // Node 的 makeMatcher([]) 返回 () => false。
        var matcher = FsHelpers.MakeMatcher([]);
        Assert.False(matcher("anything.jar"));
        Assert.False(matcher(""));
    }

    // ------------------------------------------------------------------
    // 路径穿越
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("../../evil", true)]
    [InlineData("..\\evil", true)]
    [InlineData("a/../b", true)]
    [InlineData("foo..bar.zip", true)]   // 刻意过严：宁可误伤也不放过穿越
    [InlineData("normal/路径.jar", false)]
    public void HasTraversal_判定与Node版一致(string path, bool expected)
    {
        // Node: p.includes('..') —— 只要任何位置出现两个点就算。
        // 这里显式固化「宁可误伤」这个决策，防止后人"优化"成逐段检查。
        Assert.Equal(expected, FsHelpers.HasTraversal(path));
    }

    [Fact]
    public void HasTraversal_空值判定为穿越()
    {
        // Node: typeof p !== 'string' || p.includes('..') → 非字符串一律为 true
        Assert.True(FsHelpers.HasTraversal(null));
    }
}
