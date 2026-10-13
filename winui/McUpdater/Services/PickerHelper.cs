using System;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace McUpdater.Services;

/// <summary>
/// 文件选择器封装。
///
/// 【unpackaged 下的关键事实 —— MarukoBox 已实证】
/// WinUI 3 unpackaged（无包身份）没有 AUMID，传统 WinRT
/// <see cref="FileOpenPicker"/> 会因为无法解析应用的 AUMID 而弹不出来。
/// 解法是 <see cref="InitializeWithWindow"/>：把宿主窗口的 HWND 交给 picker，
/// 让它以该窗口为 owner 弹窗。MarukoBox（同为 unpackaged self-contained）
/// 已在生产验证过这条路径，本项目直接沿用 —— 不重复踩AUMID 的坑。
///
/// 【为什么不加 COM 的 IFileOpenDialog 降级链】
/// 考虑过再加一条 IFileOpenDialog 兜底（它不依赖 AUMID），但：
///   - MarukoBox 生产环境只用 WinRT picker，从未出现过需要兜底的失败；
///   - COM interop 的 vtable 成员顺序必须与原生接口完全一致，写错是
///     静默内存踩踏，极难调试；
///   - Shell COM 对象要求 STA apartment，而 Task.Run 给的是 MTA 线程，
///     不显式切 apartment 会抛 RPC_E_CHANGED_MODE。
/// 在没有真实故障证据前加这层复杂度不划算。**等真的遇到 picker 弹不出来
/// 的用户反馈再补**，而不是预防性地写。
///
/// 【范围】M1 阶段只保证链路接通（能弹出选择框、拿到路径）。
/// 读包逻辑在 M3 接入 ModpackReader。
/// </summary>
public static class PickerHelper
{
    /// <summary>整合包可接受的文件类型（扩展名含点）。</summary>
    private static readonly string[] ModpackFilters =
    {
        ".zip", ".mrpack", ".rar", ".7z",
    };

    /// <summary>用应用内选择器选整合包；用户取消返回 null。</summary>
    public static async Task<string?> PickModpackAsync(IntPtr windowHandle)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.Downloads,
        };
        foreach (var ext in ModpackFilters)
        {
            picker.FileTypeFilter.Add(ext);
        }

        // 【关键】缺了这一行，unpackaged 下 picker 会因无 AUMID 而失败
        InitializeWithWindow.Initialize(picker, windowHandle);

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    /// <summary>
    /// 选本地版本目录（.minecraft 所在的版本文件夹）。M4 接入。
    /// </summary>
    public static async Task<string?> PickVersionFolderAsync(IntPtr windowHandle)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
        };
        // 【WinUI 硬性要求】FolderPicker 的 FileTypeFilter 必须恰好加一个 "*"，
        // 否则运行时抛异常。MarukoBox release-notes 里记过这条。
        picker.FileTypeFilter.Add("*");

        InitializeWithWindow.Initialize(picker, windowHandle);

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
}