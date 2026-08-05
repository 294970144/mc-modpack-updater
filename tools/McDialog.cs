// McDialog — 原生文件/文件夹选择对话框小工具
//
// 作用：替代 PowerShell 弹系统对话框。PowerShell 每次冷启动 ~1.5~2s，
// 这个 C# 程序只有 .NET 运行时启动 ~0.3~0.5s，快 3~5 倍，且无 stdin/STA 状态坑。
//
// 编译：见同目录 build-dialog.bat（用 Windows 自带 .NET Framework 的 csc，一次性）
// 用法（由 src/services/dialog.js 调用）：
//   McDialog.exe open    [--initial DIR] [--filter "f|f2"] [--title T]
//   McDialog.exe folder  [--initial DIR] [--title T]
// 选中路径打到 stdout 第一行；用户取消则无输出。
using System;
using System.IO;
using System.Windows.Forms;

internal static class McDialog
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string mode = args.Length > 0 ? args[0] : "open";
        string initial = "", filter = "All files (*.*)|*.*", title = "";
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--initial" && i + 1 < args.Length) initial = args[++i];
            else if (args[i] == "--filter" && i + 1 < args.Length) filter = args[++i];
            else if (args[i] == "--title" && i + 1 < args.Length) title = args[++i];
        }

        // TopMost 隐藏 owner：让对话框弹在最上层，不会藏在别的窗口后面
        Form owner = new Form
        {
            TopMost = true,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new System.Drawing.Point(-1000, -1000),
            Size = new System.Drawing.Size(0, 0),
            FormBorderStyle = FormBorderStyle.None,
        };

        string result = null;
        if (mode == "folder")
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                if (!string.IsNullOrEmpty(title)) d.Description = title;
                d.ShowNewFolderButton = false;
                if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial)) d.SelectedPath = initial;
                if (d.ShowDialog(owner) == DialogResult.OK) result = d.SelectedPath;
            }
        }
        else
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                if (!string.IsNullOrEmpty(title)) d.Title = title;
                d.Filter = filter;
                d.Multiselect = false;
                if (!string.IsNullOrEmpty(initial)) d.InitialDirectory = initial;
                if (d.ShowDialog(owner) == DialogResult.OK) result = d.FileName;
            }
        }

        if (!string.IsNullOrEmpty(result))
        {
            Console.Out.WriteLine(result);
        }
        return 0;
    }
}
