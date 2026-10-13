using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using McUpdater.Core;

namespace McUpdater.Services;

/// <summary>
/// 读包服务：把 <see cref="ModpackReader"/> 的同步 API 包成可异步等待的进度式调用。
///
/// <para><b>为什么必须有这个类</b>：<c>ModpackReader.Open</c> 是同步阻塞的，
/// 而 226MB 的真实整合包实测要跑数秒。直接在 UI 线程调用会让窗口完全无响应，
/// 拖动、关闭、连点按钮全部失效。所以必须 <c>Task.Run</c> 到后台线程。</para>
///
/// <para><b>进度回调的线程陷阱</b>：<c>Open</c> 在后台线程上回调 <c>onProgress</c>，
/// 而回调里要改的进度条/ 文本都是 UI 元素，<b>跨线程改会抛
/// <c>RPC_E_WRONG_THREAD</c></b>。必须经 <see cref="DispatcherQueue"/>
/// marshal 回 UI 线程 —— 这一步是本类存在的另一半理由。</para>
/// </summary>
internal static class PackLoadService
{
    /// <summary>
    /// 后台线程读包，UI 线程收进度。
    /// </summary>
    /// <param name="zipPath">整合包路径。</param>
    /// <param name="dispatcher">UI 线程调度器。</param>
    /// <param name="onProgress">进度回调（消息, 百分比）。<b>在 UI 线程上调用</b>。</param>
    /// <exception cref="InvalidDataException">包结构不合法（不是 zip、没有 mods 等）。</exception>
    /// <exception cref="FileNotFoundException">文件不存在。</exception>
    public static Task<ModpackHandle> OpenAsync(
        string zipPath,
        DispatcherQueue dispatcher,
        Action<string, int> onProgress)
    {
        return Task.Run(() =>
        {
            void Report(string msg, int pct)
            {
                // 【已知的取舍】TryEnqueue 而非 await enqueue：
                // Open 内部的 Report 调用点并不都处于 async 上下文
                // （有几个在同步辅助方法里），无法全部 await。
                // TryEnqueue 的语义是「UI 忙就丢这一帧」—— 进度文案丢一帧无副作用，
                // 而若在后台线程同步等UI 线程则会死锁。
                dispatcher.TryEnqueue(() => onProgress(msg, pct));
            }

            return ModpackReader.Open(zipPath, Report);
        });
    }
}