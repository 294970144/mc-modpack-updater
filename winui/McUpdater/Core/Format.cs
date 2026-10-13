namespace McUpdater.Core;

/// <summary>
/// 展示格式化工具（移植自 Node 版 <c>public/js/ui.js</c> 的 <c>bytes</c> / <c>shortPath</c>）。
///
/// <para><b>为什么不放在 MainWindow 里</b>：这两条是「数据 → 文案」的纯映射规则，
/// 既不含 WinUI 依赖，也不该只存在于窗口类里。放进 Core 才能被测试台直接链接验证
/// —— 数值格式错了（比如把 1.5MB 显示成 2MB）光看界面是看不出来的。</para>
/// </summary>
public static class Format
{
    /// <summary>
    /// 字节数格式化（Node <c>UI.bytes</c>）。
    ///
    /// <para><b>原版的不对称规则必须照搬</b>：
    /// 小于 10 的非整数部分保留 1 位（<c>1.5 MB</c>），大于等于 10 的取整（<c>15 MB</c>）。
    /// 若"顺手统一"成一种，同一个包在 UI 上显示的体积就和Node 版不一样了。
    /// 这类细节正是"保真移植"最容易漏、也最容易被当成无关 bug 的地方。</para>
    ///
    /// <para>边界：只有 4 档单位（到 GB 为止，Node 的 <c>units</c> 数组就这4 个），
    /// 所以 226MB 显示为 <c>226 MB</c>，2GB 显示为 <c>2.0 GB</c>。</para>
    /// </summary>
    public static string Bytes(long n)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double v = n;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }

        return i == 0
            ? v.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " " + units[i]
            : (v < 10
                ? v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                : v.ToString("0", System.Globalization.CultureInfo.InvariantCulture)) + " " + units[i];
    }

    /// <summary>
    /// 路径过长时中间省略，**头尾都保留**（Node <c>UI.shortPath</c>）。
    ///
    /// <para>尾巴保留得比头部多（60% 给尾巴），因为尾巴是版本名/文件名，
    /// 那是用户唯一认得出来的部分。省略号是单字符 <c>…</c>。</para>
    /// </summary>
    public static string ShortPath(string? p, int max = 58)
    {
        p ??= "";
        if (p.Length <= max)
        {
            return p;
        }
        var keepTail = (int)Math.Floor(max * 0.6);
        return p[..(max - keepTail - 1)] + "…" + p[^keepTail..];
    }
}