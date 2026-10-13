#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
生成 McUpdater 的应用图标（草方块）与打包占位 PNG。

造型取自 public/index.html 的 favicon SVG：
    <rect width=16 height=16 fill=#7CBD56/>        草绿
    <rect y=9  width=16 height=7 fill=#8B6444/>  泥土棕
即 16×16 网格中第 0~8 行草绿、第 9~15 行泥土棕。

【为什么手写 ICO 容器，不用 Pillow 的 ICO 保存】
两个原因，都踩过：
  1. Pillow 的 ICO 保存是「降采样」，给多大的 base 图就只能出多大的帧 ——
     传 frames[0]（16px）进去，7 个尺寸只会得到 1 帧 16px。
  2. Pillow 内部用 LANCZOS 降采样，会把像素画糊掉，破坏「像素但不粗糙」的前提。
  3. 更要紧：Pillow 默认把所有帧写成 PNG 编码。Windows 只对 256×256 帧保证
     PNG 支持；小尺寸 PNG 帧在 LoadImage / AppWindow.SetIcon / .NET Icon 处
     解码失败 → 应用窗口与任务栏回落默认图标，而资源管理器里却正常
     （它用现代 WIC 解析）。这个现象极具迷惑性。
所以本脚本自己拼 ICONDIR + ICONDIRENTRY + 逐帧 BITMAPINFOHEADER，
保证：① 每帧都是 32 位 BMP；② 缩放用 NEAREST，像素边缘绝对锐利。
"""
import os
import struct

from PIL import Image, ImageDraw

# 本脚本就放在 Assets/ 里，产物写到脚本所在目录（不要再嵌一层 Assets/）
ASSETS = os.path.dirname(os.path.abspath(__file__))

# 像素风配色（与 Styles/Palette.xaml 一致）
GRASS = (124, 189, 86, 255)      # #7CBD56
GRASS_HI = (138, 203, 99, 255)   # #8ACB63（hover 提亮，同按钮 hover 补偿色）
GRASS_LO = (110, 172, 78, 255)   # 草层底部暗一档，做出层次
DIRT = (139, 100, 68, 255)       # #8B6444
DIRT_HI = (161, 118, 82, 255)    # 泥土底部亮一档，做出"下沿受反光"
EDGE = (46, 36, 26, 255)         # #2E241A 像素硬边深棕

GRID = 16          # 造型网格
GRASS_ROWS = 9     # 第 0~8 行为草绿
ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]


def draw_grid_block() -> Image.Image:
    """按 GRID×GRID 网格画草方块（16×16，无缩放）。"""
    im = Image.new("RGBA", (GRID, GRID), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    for y in range(GRID):
        for x in range(GRID):
            if y < GRASS_ROWS:
                if y == 0:
                    c = GRASS_HI                       # 最上一行受光
                elif y >= GRASS_ROWS - 2:
                    c = GRASS_LO                       # 草与土交界处暗一档
                else:
                    c = GRASS
            else:
                c = DIRT_HI if y >= GRID - 2 else DIRT
            d.point((x, y), fill=c)
    return im


def render_block(size: int, *, with_edge: bool) -> Image.Image:
    """把 16×16 网格用 NEAREST 放大到 size×size 的草方块。"""
    im = draw_grid_block().resize((size, size), Image.NEAREST)

    if with_edge and size >= 32:
        # 加 1px 深棕硬边：把主体下移 1px，底板画描边矩形
        edged = Image.new("RGBA", (size, size), (0, 0, 0, 0))
        edged.paste(im, (1, 1))
        ImageDraw.Draw(edged).rectangle(
            [0, 0, size - 1, size - 1], outline=EDGE, width=1
        )
        im = edged
    return im


def ico_frame(img: Image.Image) -> bytes:
    """把 RGBA 图转成 ICO 用的 32 位 BMP 帧（含 AND 掩码）。

    ICO 里的 BMP 与普通 .bmp 文件有两处不同：
      - BITMAPINFOHEADER 的 biHeight 是「两倍图像高」（XOR 位图 + AND 掩码）
      - 像素数据自下而上存储
    """
    w, h = img.size
    rgba = img.convert("RGBA")

    # 像素数据：自下而上，每行 BGRA
    xor = bytearray()
    for y in range(h - 1, -1, -1):
        for x in range(w):
            r, g, b, a = rgba.getpixel((x, y))
            xor += bytes((b, g, r, a))

    # AND 掩码：alpha==0 处置 1（该像素透明）。每行按 4 字节对齐补齐。
    row_bytes = ((w + 31) // 32) * 4
    and_mask = bytearray()
    for y in range(h - 1, -1, -1):
        bits = bytearray(row_bytes)
        for x in range(w):
            if rgba.getpixel((x, y))[3] == 0:
                bits[x // 8] |= 0x80 >> (x % 8)
        and_mask += bits

    # BITMAPINFOHEADER 共 11 个字段 40 字节
    header = struct.pack(
        "<IiiHHIIiiII",
        40,                # biSize
        w,                 # biWidth
        h * 2,             # biHeight（XOR + AND）
        1,                 # biPlanes
        32,                # biBitCount ← 32 位，保证 alpha 有效
        0,                 # biCompression = BI_RGB
        len(xor),          # biSizeImage
        0, 0,              # biXPelsPerMeter / biYPelsPerMeter（ICO 里可忽略）
        0, 0,              # biClrUsed / biClrImportant（32 位无调色板，均 0）
    )
    return bytes(header) + bytes(xor) + bytes(and_mask)


def write_ico(path: str, sizes):
    """手写 ICO 容器：逐帧 32 位 BMP + NEAREST 缩放。"""
    frames = []
    for s in sizes:
        img = render_block(s, with_edge=s >= 32)
        data = ico_frame(img)
        w = 0 if s >= 256 else s          # 256 在目录项里记 0
        # wBitCount 写 32（与帧内 BITMAPINFOHEADER 的 biBitCount 一致）。
        # 目录项里这个字段对 32 位 BMP 基本被忽略，但填对更规范。
        frames.append((w, 0 if s >= 256 else s, 0, 0, 1, 32, len(data), data))

    count = len(frames)
    # ICONDIR: reserved(2) type(2)=1 count(2)
    out = bytearray(struct.pack("<HHH", 0, 1, count))

    # ICONDIRENTRY 每项 16 字节；偏移量先占位，写完帧再回填
    offset = 6 + 16 * count
    entries = bytearray()
    blobs = bytearray()
    for (w, h, ncol, planes, bpp, bits, size, data) in frames:
        # ICONDIRENTRY 共 8 个字段 16 字节：
        # bWidth bHeight bColorCount bReserved wPlanes wBitCount dwBytesInRes dwImageOffset
        entries += struct.pack(
            "<BBBBHHII", w, h, ncol, 0, planes, bpp, size, offset
        )
        blobs += data
        offset += size

    out += entries + blobs
    with open(path, "wb") as f:
        f.write(bytes(out))
    return len(out)


def save_png(path: str, size: int, *, with_edge: bool = False):
    render_block(size, with_edge=with_edge).save(path, format="PNG")


def verify_ico(path: str):
    """自检：逐帧确认是 BMP 而非 PNG，并打印尺寸/编码。"""
    d = open(path, "rb").read()
    res, typ, cnt = struct.unpack("<HHH", d[:6])
    assert typ == 1, f"不是 ICO（type={typ}）"
    bad = []
    print(f"  文件 {len(d)} 字节，帧数 {cnt}")
    off = 6
    for _ in range(cnt):
        w, h, _n, _rsv, _p, bpp, size, offset = struct.unpack("<BBBBHHII", d[off:off + 16])
        off += 16
        w = w or 256
        h = h or 256
        blob = d[offset:offset + size]
        is_png = blob[:8] == b"\x89PNG\r\n\x1a\n"
        bitcount = -1 if is_png else int.from_bytes(blob[14:16], "little")
        flag = "" if (not is_png and bitcount == 32) else "  ←← 有问题"
        if flag:
            bad.append(w)
        print(f"    {w:>3}x{h:<3} bpp={bpp:>2} size={size:>7}  PNG={is_png}  BMP_bitcount={bitcount}{flag}")
    assert not bad, f"这些帧不是 32 位 BMP（Windows 会解码失败）: {bad}"
    return cnt


if __name__ == "__main__":
    os.makedirs(ASSETS, exist_ok=True)

    ico = os.path.join(ASSETS, "AppIcon.ico")
    write_ico(ico, ICO_SIZES)
    print("AppIcon.ico 自检：")
    verify_ico(ico)

    print("\n打包占位 PNG：")
    for name, size in [
        ("Square44x44Logo.png", 44),
        ("Square150x150Logo.png", 150),
        ("SplashScreen.png", 620),
        ("StoreLogo.png", 50),
    ]:
        p = os.path.join(ASSETS, name)
        save_png(p, size)
        print(f"  {name}  {os.path.getsize(p)} 字节")

    # 商店横幅：草方块 + 米底 + 深棕描边
    wide = Image.new("RGBA", (310, 150), (242, 234, 212, 255))
    wide.paste(render_block(96, with_edge=True), (16, (150 - 96) // 2))
    ImageDraw.Draw(wide).rectangle([0, 0, 309, 149], outline=EDGE, width=2)
    wp = os.path.join(ASSETS, "Wide310x150Logo.png")
    wide.save(wp, format="PNG")
    print(f"  Wide310x150Logo.png  {os.path.getsize(wp)} 字节")
