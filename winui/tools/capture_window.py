#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
按窗口标题抓取窗口画面（不抢前台）。

为什么不用 ImageGrab.grab(bbox=...)：
    它抓的是屏幕像素，窗口被别的窗口盖住就抓到别人。
    SetForegroundWindow 在别的应用抢焦点时会失败（Windows 的前台锁）。

用 PrintWindow(PW_RENDERFULLCONTENT)：
    让窗口自己把自己画进 DC，完全不依赖可见性/前后台关系，
    不抢焦点、不干扰用户正在做的事。

用法：
    python capture_window.py "<窗口标题片段>" "<输出png>"
"""
import ctypes
import ctypes.wintypes as wt
import sys

from PIL import Image

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32

PW_RENDERFULLCONTENT = 0x00000002
SRCCOPY = 0x00CC0020


def find_windows(needle: str):
    """枚举所有标题含 needle 的可见顶层窗口。"""
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


def capture(hwnd, path):
    rect = wt.RECT()
    if not user32.GetWindowRect(hwnd, ctypes.byref(rect)):
        raise RuntimeError("GetWindowRect 失败")
    w, h = rect.right - rect.left, rect.bottom - rect.top
    if w <= 0 or h <= 0:
        raise RuntimeError(f"窗口尺寸异常 {w}x{h}")

    # 用整个虚拟屏幕大小建内存 DC，避免多显示器坐标系转换的坑
    win_dc = user32.GetWindowDC(hwnd)
    if not win_dc:
        raise RuntimeError("GetWindowDC 失败")
    mem_dc = gdi32.CreateCompatibleDC(win_dc)
    bmp = gdi32.CreateCompatibleBitmap(win_dc, w, h)
    old = gdi32.SelectObject(mem_dc, bmp)

    try:
        # PW_RENDERFULLCONTENT 对 WinUI/Win32 合成窗口是必需的，
        # 否则截到的是黑屏（窗口没被 WM_PAINT 过）
        ok = user32.PrintWindow(hwnd, mem_dc, PW_RENDERFULLCONTENT)
        if not ok:
            # 退回普通模式再试一次
            ok = user32.PrintWindow(hwnd, mem_dc, 0)
        if not ok:
            raise RuntimeError("PrintWindow 两次都失败")

        class BITMAPINFOHEADER(ctypes.Structure):
            _fields_ = [
                ("biSize", wt.DWORD), ("biWidth", ctypes.c_long),
                ("biHeight", ctypes.c_long), ("biPlanes", wt.WORD),
                ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
                ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", ctypes.c_long),
                ("biYPelsPerMeter", ctypes.c_long), ("biClrUsed", wt.DWORD),
                ("biClrImportant", wt.DWORD),
            ]

        hdr = BITMAPINFOHEADER()
        hdr.biSize = ctypes.sizeof(BITMAPINFOHEADER)
        hdr.biWidth = w
        hdr.biHeight = -h            # 负数 = 自上而下，省一次翻转
        hdr.biPlanes = 1
        hdr.biBitCount = 32
        hdr.biCompression = 0        # BI_RGB

        buf = ctypes.create_string_buffer(w * h * 4)
        got = gdi32.GetDIBits(mem_dc, bmp, 0, h, buf, ctypes.byref(hdr), 0)
        if got == 0:
            raise RuntimeError("GetDIBits 返回 0")

        im = Image.frombuffer("RGBA", (w, h), buf, "raw", "BGRA", 0, 1)
        im.convert("RGB").save(path)
        return im.size
    finally:
        gdi32.SelectObject(mem_dc, old)
        gdi32.DeleteObject(bmp)
        gdi32.DeleteDC(mem_dc)
        user32.ReleaseDC(hwnd, win_dc)


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(1)
    needle, out = sys.argv[1], sys.argv[2]
    hits = find_windows(needle)
    if not hits:
        print(f"没找到标题含「{needle}」的窗口")
        sys.exit(2)
    hwnd, title = hits[0]
    size = capture(hwnd, out)
    print(f"已抓取「{title}」{size} -> {out}")
