#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
屏幕抓取版：抓「窗口所在那块屏幕」的真实像素。

为什么需要这个（实测记录，2026-10-09）：
    capture_window.py 用 PrintWindow(PW_RENDERFULLCONTENT)，对普通 Win32 窗口有效，
    但对 **WinUI 3 / DirectComposition** 的窗口，客户区抓回来是全白 ——
    标题栏能抓到（那还是 GDI 画的），客户区是 DComp 合成的，不走 WM_PAINT，
    PrintWindow 拿不到。M1 阶段截图能出内容是因为当时还没走到这个问题。

    所以改成：把窗口移到屏幕左上角并置顶，再按窗口矩形从屏幕抓像素。

    代价：会抢一次前台（对 Master 正在做的事有干扰）。仅用于开发期自检。

用法：
    python capture_screen.py "<窗口标题片段>" "<输出png>"
"""
import ctypes
import ctypes.wintypes as wt
import sys

from PIL import Image

user32 = ctypes.windll.user32

SW_RESTORE = 9
SWP_NOSIZE = 0x0001
SWP_NOZORDER = 0x0004


def find_window(needle: str):
    hits = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def cb(hwnd, lparam):
        if not user32.IsWindowVisible(hwnd):
            return True
        n = user32.GetWindowTextLengthW(hwnd)
        if n == 0:
            return True
        buf = ctypes.create_unicode_buffer(n + 1)
        user32.GetWindowTextW(hwnd, buf, n + 1)
        if needle in buf.value:
            hits.append((hwnd, buf.value))
        return True

    user32.EnumWindows(cb, 0)
    return hits


def capture(needle: str, path: str):
    hits = find_window(needle)
    if not hits:
        raise RuntimeError(f'没找到标题含「{needle}」的窗口')
    hwnd, title = hits[0]

    # 还原（可能被最小化）并移到 (0,0)，再置顶
    user32.ShowWindow(hwnd, SW_RESTORE)
    user32.SetWindowPos(hwnd, 0, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOZORDER)
    user32.BringWindowToTop(hwnd)
    user32.SetForegroundWindow(hwnd)

    # 给合成器一点时间出帧
    import time
    time.sleep(1.6)

    rect = wt.RECT()
    if not user32.GetWindowRect(hwnd, ctypes.byref(rect)):
        raise RuntimeError('GetWindowRect 失败')
    w, h = rect.right - rect.left, rect.bottom - rect.top
    if w <= 0 or h <= 0:
        raise RuntimeError(f'窗口尺寸异常 {w}x{h}')

    img = Image.new('RGB', (w, h))
    # 用 ImageGrab 的 raw 模式按 bbox 抓屏幕
    from PIL import ImageGrab
    img = ImageGrab.grab(bbox=(rect.left, rect.top, rect.right, rect.bottom), all_screens=True)
    img.save(path)
    return title, img.size


if __name__ == '__main__':
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(1)
    t, size = capture(sys.argv[1], sys.argv[2])
    print(f'已抓取「{t}」{size} -> {sys.argv[2]}')