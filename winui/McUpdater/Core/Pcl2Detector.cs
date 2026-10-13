using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace McUpdater.Core;

/// <summary>
/// PCL2 启动器探测（移植自 Node 版 <c>src/services/pcl2-detector.js</c>）。
///
/// <para><b>三条线索，可靠性从高到低</b>（顺序即优先级，别排序）：
///   1. 正在运行的进程 → 拿 exe 路径 → 反推启动器根目录（最准，玩家此刻用的就是它）
///   2. <c>PCL/Log1~5.txt</c> 里的「有效的 Minecraft 文件夹：名字 &gt; 路径」→ 历史文件夹
///   3. 常见安装位置兜底扫描
/// 三条取并集，按路径去重（小写比较）。</para>
///
/// <para><b>为什么进程探测可以注入</b>：<see cref="Detect"/> 的
/// <c>probe</c> 参数让测试能喂一个固定结果。不是为测试加的后门 ——
/// 「PCL2 有没有在跑」天然是环境状态，把它硬编码进探测函数里
/// 就等于让「探测逻辑」和「本机此刻的进程表」绑死，测不了也复现不了。</para>
/// </summary>
public static class Pcl2Detector
{
    // ------------------------------------------------------------------
    // Setup.ini
    // ------------------------------------------------------------------

    /// <summary>
    /// 读并解析一个 PCL2 的 <c>Setup.ini</c>；读不到返回空字典。
    ///
    /// <para><b>为什么复用 <see cref="ModpackReader.ParseSetupIni"/></b>：
    /// Node 侧 <c>pcl2-detector.parseSetupIni</c> 与
    /// <c>modpack-reader</c> 里那份是同一份实现（<c>Key:Value</c>、
    /// 只按第一个冒号切、大小写敏感）。搬两遍必然产生分叉，
    /// 而「整合包里读出来的版本名」与「本地版本读出来的版本名」
    /// 一旦用两套解析规则，匹配就会在边界情况下静默失配。</para>
    ///
    /// <para><b>编码按 UTF-8 读是真的验证过的</b>：日志里的中文
    /// （「有效的 Minecraft 文件夹」）若文件是 GBK，Node 的
    /// <c>readFileSync(utf-8)</c> 会解出替换字符，正则根本匹配不上 ——
    /// 而本机 Log1.txt 匹配到了，说明它就是 UTF-8。</para>
    /// </summary>
    public static Dictionary<string, string> ParseSetupIniFile(string iniPath)
    {
        try
        {
            return ModpackReader.ParseSetupIni(File.ReadAllText(iniPath, Encoding.UTF8));
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// PCL2 用 <c>$</c> 前缀表示「相对于启动器根目录」（Node <c>resolvePcl2Path</c>）。
    /// </summary>
    public static string? ResolvePcl2Path(string pcl2Root, string? p)
    {
        if (string.IsNullOrEmpty(p))
        {
            return null;
        }
        return p.StartsWith('$') ? Path.Combine(pcl2Root, p[1..]) : p;
    }

    // ------------------------------------------------------------------
    // 日志解析
    // ------------------------------------------------------------------

    // 不用 [GeneratedRegex]：它要求模式是编译期常量字面量，而这里想让
    // AppConfig.Pcl2LogFolderPattern 成为唯一定义处（与 Node 的 config.js 对齐）。
    private static readonly Regex LogFolderRegexValue = new(AppConfig.Pcl2LogFolderPattern);

    /// <summary>
    /// 从 <c>Log1~5.txt</c> 里捞出所有 PCL2 认过的 Minecraft 文件夹
    /// （Node <c>parseFoldersFromLogs</c>）。
    ///
    /// <para><b>去重是跨文件的</b>：<c>seen</c> 在整个函数生命周期内有效，
    /// 所以 Log1 里出现过的路径，Log2 里再出现不会重复。
    /// 这条决定了「文件夹顺序」—— 而顺序会出现在目标卡与候选列表上。</para>
    ///
    /// <para><b>只按路径去重、不按名字</b>：同一个路径在不同时期可能叫不同名字
    /// （PCL2 改过显示名），Node 就是这么写的，别"顺手"改成按名字。</para>
    /// </summary>
    public static List<GameFolder> ParseFoldersFromLogs(string pcl2Root)
    {
        var folders = new List<GameFolder>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var logDir = Path.Combine(pcl2Root, AppConfig.Pcl2LogDir);

        foreach (var file in AppConfig.Pcl2LogFiles)
        {
            var logPath = Path.Combine(logDir, file);
            string text;
            try
            {
                if (!File.Exists(logPath))
                {
                    continue;
                }
                text = File.ReadAllText(logPath, Encoding.UTF8);
            }
            catch
            {
                continue;
            }

            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            {
                var m = LogFolderRegexValue.Match(line);
                if (!m.Success)
                {
                    continue;
                }
                var name = m.Groups[1].Value.Trim();
                var dir = m.Groups[2].Value.Trim().TrimEnd('\\', '/');
                if (!seen.Add(dir))
                {
                    continue;
                }
                folders.Add(new GameFolder(name, dir, "log", pcl2Root, false));
            }
        }

        return folders;
    }

    // ------------------------------------------------------------------
    // 进程探测
    // ------------------------------------------------------------------

    /// <summary>
    /// 跑一条外部命令拿 stdout；任何异常都返回空串（Node <c>run()</c> 的语义）。
    ///
    /// <para><b>为什么吞掉所有异常</b>：这三条命令（tasklist / powershell / wmic）
    /// 在不同 Windows 版本上都可能不存在或被策略禁掉（wmic 自 Win11 24H2 起移除）。
    /// 它们是「锦上添花的线索」，失败应当降级为"没探测到"，
    /// 而不是让整个目标选择挂掉。</para>
    /// </summary>
    private static string Run(string fileName, string arguments, int timeoutMs = 6000)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                return "";
            }

            // 【必须并发排空 stdout/stderr，否则必死锁 —— 本机实测过】
            // 匿名管道缓冲区只有几 KB，而 tasklist /FO CSV 的输出轻松十几 KB。
            // 若写成 "先 WaitForExit 再 ReadToEnd"：子进程写满管道阻塞 →
            // 永远退不出去 → 我们白等满整个超时。
            // 症状极具误导性：探测一次 6039ms，看上去像"扫描磁盘很慢"，
            // 实际一个字节的盘都没扫。
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();

            // WaitForExit(int) 超时后不会杀进程 —— 必须自己收尾，
            // 否则每次探测都漏一个挂着的 powershell。
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch { /* 已退出 */ }
                return "";
            }

            Task.WaitAll(new Task[] { stdout, stderr }, 2000);
            return stdout.Status == TaskStatus.RanToCompletion ? stdout.Result : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 查 PCL2 进程并拿到 exe 完整路径（Node <c>checkRunning()</c>）。
    ///
    /// <para>优先 PowerShell（wmic 在新版 Windows 上已被移除），
    /// 失败再退 wmic。两条都拿不到路径时仍然返回
    /// <c>Running=true, ExePath=null</c> —— "在跑但不知道在哪"是有效信息，
    /// 它会影响 <see cref="Pcl2RootInfo.IsActive"/> 之外的展示。</para>
    /// </summary>
    public static Pcl2ProcessState CheckRunning()
    {
        var list = Run("tasklist", "/FO CSV /NH");
        var hit = AppConfig.Pcl2ExeNames.Any(n => list.Contains(n, StringComparison.Ordinal))
                  || Regex.IsMatch(list, "Plain Craft Launcher", RegexOptions.IgnoreCase);
        if (!hit)
        {
            return new Pcl2ProcessState(false, null);
        }

        // 与 Node 逐字一致：PowerShell 收到的 -Command 参数里，
        // 内层引号要靠 \" 转义（CommandLineToArgv 会把它还原成 "）。
        const string psArgs =
            "-NoProfile -NonInteractive -Command \"Get-CimInstance Win32_Process " +
            "-Filter \\\"Name LIKE '%Plain Craft Launcher%'\\\" " +
            "| Select-Object -ExpandProperty ExecutablePath\"";

        var ps = Run("powershell", psArgs).Trim();
        if (ps.Length > 0)
        {
            var first = ps.Replace("\r\n", "\n").Split('\n')
                .Select(s => s.Trim()).FirstOrDefault(s => s.Length > 0);
            if (first is not null)
            {
                return new Pcl2ProcessState(true, first);
            }
        }

        var wmic = Run("wmic",
            "process where \"name like '%Plain Craft Launcher%'\" get ExecutablePath /FORMAT:LIST");
        var m = Regex.Match(wmic, @"ExecutablePath=(.+)");
        if (m.Success && m.Groups[1].Value.Trim().Length > 0)
        {
            return new Pcl2ProcessState(true, m.Groups[1].Value.Trim());
        }

        return new Pcl2ProcessState(true, null);
    }

    // ------------------------------------------------------------------
    // 根目录候选
    // ------------------------------------------------------------------

    /// <summary>
    /// 一个目录像不像 PCL2 根：有 <c>PCL/Setup.ini</c>，或者有主程序 exe
    /// （Node <c>looksLikePcl2Root</c>）。
    /// </summary>
    public static bool LooksLikePcl2Root(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return false;
        }
        if (File.Exists(Path.Combine(dir, AppConfig.Pcl2SetupIni)))
        {
            return true;
        }
        return AppConfig.Pcl2ExeNames.Any(n => File.Exists(Path.Combine(dir, n)));
    }

    private sealed record RootCandidate(string Path, string Source);

    /// <summary>
    /// 收集根目录候选（Node <c>collectRootCandidates</c>）。
    /// 顺序：custom → process → preset → 各盘盘根下的常见目录名。
    /// </summary>
    private static List<RootCandidate> CollectRootCandidates(string? customRoot, Pcl2ProcessState proc)
    {
        var cands = new List<RootCandidate>();
        var seenPath = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Push(string? p, string source)
        {
            if (string.IsNullOrEmpty(p))
            {
                return;
            }
            var abs = Path.GetFullPath(p);
            if (!seenPath.Add(abs))
            {
                return;
            }
            cands.Add(new RootCandidate(abs, source));
        }

        Push(customRoot, "custom");

        if (proc.ExePath is not null)
        {
            Push(Path.GetDirectoryName(proc.ExePath), "process");
        }

        foreach (var r in AppConfig.Pcl2DefaultRoots)
        {
            Push(r, "preset");
        }

        // 各盘根下的常见目录名
        string[] drives = ["C:", "D:", "E:", "F:", "G:"];
        string[] names = ["PCL2", "PCL", "pcl2", "Minecraft", "mc"];
        foreach (var d in drives)
        {
            if (!Directory.Exists(d + "\\"))
            {
                continue;
            }
            foreach (var n in names)
            {
                Push(Path.Combine(d + "\\", n), "scan");
            }
        }

        return cands.Where(c => LooksLikePcl2Root(c.Path)).ToList();
    }

    // ------------------------------------------------------------------
    // 主入口
    // ------------------------------------------------------------------

    /// <summary>
    /// 探测 PCL2（Node <c>detect()</c>）。
    /// </summary>
    /// <param name="customRoot">用户手动指定的启动器根目录。</param>
    /// <param name="probe">
    /// 进程探测函数；传 null 时用 <see cref="CheckRunning"/> 真跑一次。
    /// </param>
    public static Pcl2DetectResult Detect(string? customRoot = null, Func<Pcl2ProcessState>? probe = null)
    {
        var proc = probe?.Invoke() ?? CheckRunning();
        var candidates = CollectRootCandidates(customRoot, proc);

        if (candidates.Count == 0)
        {
            // Node: logger.warn('没有找到 PCL2 启动器，稍后可以手动指定游戏目录')
            return new Pcl2DetectResult(false, proc.Running, proc.ExePath, [], []);
        }

        var roots = new List<Pcl2RootInfo>();
        var folders = new List<GameFolder>();
        var seenFolder = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in candidates)
        {
            var setup = ParseSetupIniFile(Path.Combine(c.Path, AppConfig.Pcl2SetupIni));
            var logFolders = ParseFoldersFromLogs(c.Path);

            var procDir = proc.ExePath is not null ? Path.GetDirectoryName(proc.ExePath) : null;
            roots.Add(new Pcl2RootInfo(
                c.Path,
                c.Source,
                procDir is not null && string.Equals(procDir, c.Path, StringComparison.OrdinalIgnoreCase),
                setup));

            // Setup.ini 里记录的当前选中文件夹
            var selected = setup.TryGetValue("LaunchFolderSelect", out var raw) && raw.Length > 0
                ? Path.GetFullPath(ResolvePcl2Path(c.Path, raw)!).TrimEnd('\\', '/')
                : null;

            void AddFolder(string name, string? dir, string source)
            {
                if (string.IsNullOrEmpty(dir))
                {
                    return;
                }
                var abs = Path.GetFullPath(dir);
                // 【保真】Node 只判 existsSync，不判是不是目录。
                // 一个同名的文件会挡住这条线索，但那是 Node 的行为，照搬。
                if (!Directory.Exists(abs) && !File.Exists(abs))
                {
                    return;
                }
                if (!seenFolder.Add(abs))
                {
                    return;
                }
                folders.Add(new GameFolder(
                    name,
                    abs,
                    source,
                    c.Path,
                    selected is not null && string.Equals(abs, selected, StringComparison.OrdinalIgnoreCase)));
            }

            foreach (var f in logFolders)
            {
                AddFolder(f.Name, f.Path, "log");
            }
            if (selected is not null)
            {
                AddFolder(Path.GetFileName(Path.GetDirectoryName(selected)) ?? "当前文件夹", selected, "setup");
            }
            AddFolder("启动器目录", Path.Combine(c.Path, ".minecraft"), "root");

            // 根目录下一层的 xxx/.minecraft
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(c.Path))
                {
                    var name = Path.GetFileName(sub);
                    if (name == "PCL")
                    {
                        continue;
                    }
                    AddFolder(name, Path.Combine(sub, ".minecraft"), "scan");
                }
            }
            catch
            {
                // 目录读不了就算了（Node 的 catch(_)）
            }
        }

        return new Pcl2DetectResult(true, proc.Running, proc.ExePath, roots, folders);
    }
}
