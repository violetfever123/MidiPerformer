#!/usr/bin/env python3
"""生成 MidiPerformer 的应用图标：一颗键帽上压着一个音符。

不依赖任何第三方库，形状与光栅化都在这里手写 —— 形状先画进一张 4 倍超采样的画布
（只做「在不在里面」的布尔测试，抗锯齿交给最后那步盒式降采样），再逐尺寸降到目标大小，
最后手写 ICO 装起来。

配色跟着应用主题走：键帽面近白冷色，音符用应用里的深藏青。只用在 exe 图标与任务栏列表两处。

用法：python tools/make-appicon.py
产物：MidiPerformer.App/Assets/appicon.ico
（.scratch/appicon-preview/ 下的各尺寸 PNG 只是给人看的预览，不是产物）
"""

import os
import struct
import sys
import zlib

SS = 4  # 超采样倍数：每个输出像素画成 4x4，最后盒式平均

# 一、形状。坐标都是 0..1 的归一化方形，y 向下。
# 键帽 = 两块圆角矩形：下面那块（深一点的底）露出一条边，读起来才像"一颗键"而不是一块板。
键帽底 = dict(色=(157, 184, 214), x0=0.075, x1=0.925, y0=0.145, y1=0.905, 圆角=0.175)
键帽面 = dict(色=(228, 237, 249), x0=0.075, x1=0.925, y0=0.095, y1=0.805, 圆角=0.175)
音符色 = (30, 42, 58)  # 应用主题里的深藏青

# 二分音符：符头（转过的椭圆）+ 符干（胶囊）+ 符尾（一串渐细的圆点串成的曲线）。
# 尺寸照着「键帽面 0.85 x 0.71」配：音符约占面宽 43%、面高 61%，四周留白。
符头 = dict(心=(0.428, 0.565), 长半轴=0.098, 短半轴=0.072, 转角=-20.0)
符干 = dict(x=0.506, y0=0.258, y1=0.565, 半径=0.024)
符尾 = dict(起=(0.506, 0.261), 控=(0.628, 0.250), 终=(0.663, 0.372), 粗0=0.042, 粗1=0.020)


def 圆角矩形内(px, py, s):
    """点在一个圆角矩形里吗。s 是那个 dict。"""
    x0, x1, y0, y1, r = s["x0"], s["x1"], s["y0"], s["y1"], s["圆角"]
    if px < x0 or px > x1 or py < y0 or py > y1:
        return False
    # 四个角各切一刀：把点折到最近的角心，看是不是还在半径内
    cx = x0 + r if px < x0 + r else (x1 - r if px > x1 - r else px)
    cy = y0 + r if py < y0 + r else (y1 - r if py > y1 - r else py)
    if cx == px or cy == py:
        return True  # 不在任何一个角的那一条带上
    return (px - cx) ** 2 + (py - cy) ** 2 <= r * r


def 椭圆内(px, py, s):
    import math

    a = math.radians(s["转角"])
    dx, dy = px - s["心"][0], py - s["心"][1]
    # 把点反向转回椭圆的本地坐标系
    lx = dx * math.cos(a) + dy * math.sin(a)
    ly = -dx * math.sin(a) + dy * math.cos(a)
    return (lx / s["长半轴"]) ** 2 + (ly / s["短半轴"]) ** 2 <= 1.0


def 胶囊内(px, py, s):
    if px < s["x"] - s["半径"] or px > s["x"] + s["半径"]:
        return False
    if py < s["y0"] - s["半径"] or py > s["y1"] + s["半径"]:
        return False
    cy = min(max(py, s["y0"]), s["y1"])  # 竖着的一段，只有两端是圆的
    return (px - s["x"]) ** 2 + (py - cy) ** 2 <= s["半径"] ** 2


def 符尾点集(n=48):
    """把那条二次贝塞尔采样成一串圆心，半径从粗到细 —— 一串叠起来的圆就是一条渐细的曲线。"""
    out = []
    (x0, y0), (cx, cy), (x1, y1) = 符尾["起"], 符尾["控"], 符尾["终"]
    for i in range(n + 1):
        t = i / n
        u = 1 - t
        out.append((
            u * u * x0 + 2 * u * t * cx + t * t * x1,
            u * u * y0 + 2 * u * t * cy + t * t * y1,
            符尾["粗0"] * (1 - t) + 符尾["粗1"] * t,
        ))
    return out


符尾点 = 符尾点集()


def 在音符里(px, py):
    if 椭圆内(px, py, 符头) or 胶囊内(px, py, 符干):
        return True
    if not (0.44 <= px <= 0.72 and 0.20 <= py <= 0.43):  # 符尾的包围盒，先粗筛
        return False
    for (cx, cy, r) in 符尾点:
        if (px - cx) ** 2 + (py - cy) ** 2 <= r * r:
            return True
    return False


def 画一遍(尺寸):
    """画一张 尺寸 x 尺寸 的 RGBA（bytes，行从上到下）。"""
    n = 尺寸 * SS  # 超采样画布的边长
    rgb = [0.0, 0.0, 0.0]
    # 每个超采样像素一份 (r,g,b,a)，初始全透明
    画布 = bytearray(n * n * 4)
    覆盖 = [0.0] * (n * n)  # 只记 alpha

    def 铺一层(测试, 色, 盒):
        """把「测试」为真的超采样像素涂上「色」。盒 = (x0,x1,y0,y1) 的粗筛范围。"""
        if 盒 is None:
            x0, x1, y0, y1 = 0.0, 1.0, 0.0, 1.0
        else:
            x0, x1, y0, y1 = 盒
        i0 = max(0, int(x0 * n) - 1)
        i1 = min(n - 1, int(x1 * n) + 1)
        j0 = max(0, int(y0 * n) - 1)
        j1 = min(n - 1, int(y1 * n) + 1)
        for j in range(j0, j1 + 1):
            py = (j + 0.5) / n
            for i in range(i0, i1 + 1):
                if 测试((i + 0.5) / n, py):
                    k = j * n + i
                    画布[k * 4 + 0] = 色[0]
                    画布[k * 4 + 1] = 色[1]
                    画布[k * 4 + 2] = 色[2]
                    覆盖[k] = 1.0

    铺一层(lambda x, y: 圆角矩形内(x, y, 键帽底), 键帽底["色"], (0.075, 0.925, 0.145, 0.905))
    铺一层(lambda x, y: 圆角矩形内(x, y, 键帽面), 键帽面["色"], (0.075, 0.925, 0.095, 0.805))
    铺一层(在音符里, 音符色, (0.28, 0.72, 0.20, 0.70))

    # 盒式降采样：SS x SS 的平均
    out = bytearray(尺寸 * 尺寸 * 4)
    k = SS * SS
    for y in range(尺寸):
        for x in range(尺寸):
            r = g = b = a = 0
            for dy in range(SS):
                for dx in range(SS):
                    i = (y * SS + dy) * n + (x * SS + dx)
                    r += 画布[i * 4]
                    g += 画布[i * 4 + 1]
                    b += 画布[i * 4 + 2]
                    a += 覆盖[i]
            o = (y * 尺寸 + x) * 4
            out[o + 0] = int(r / k + 0.5)
            out[o + 1] = int(g / k + 0.5)
            out[o + 2] = int(b / k + 0.5)
            out[o + 3] = int(a / k * 255 + 0.5)
    return bytes(out)


def png(尺寸, rgba):
    raw = b"".join(b"\x00" + rgba[y * 尺寸 * 4:(y + 1) * 尺寸 * 4] for y in range(尺寸))

    def 块(名, 数据):
        return (struct.pack(">I", len(数据)) + 名 + 数据
                + struct.pack(">I", zlib.crc32(名 + 数据) & 0xFFFFFFFF))

    return (b"\x89PNG\r\n\x1a\n"
            + 块(b"IHDR", struct.pack(">IIBBBBB", 尺寸, 尺寸, 8, 6, 0, 0, 0))
            + 块(b"IDAT", zlib.compress(raw, 9))
            + 块(b"IEND", b""))


def bmp条(尺寸, rgba):
    """ICO 里的 BMP 条：BITMAPINFOHEADER（高度写成两倍）+ 自下而上的 BGRA + AND 掩码。"""
    头 = struct.pack("<IiiHHIIiiII", 40, 尺寸, 尺寸 * 2, 1, 32, 0, 尺寸 * 尺寸 * 4, 0, 0, 0, 0)
    好行 = []
    for y in range(尺寸 - 1, -1, -1):
        行数据 = rgba[y * 尺寸 * 4:(y + 1) * 尺寸 * 4]
        o = bytearray(len(行数据))
        o[0::4] = 行数据[2::4]  # B
        o[1::4] = 行数据[1::4]  # G
        o[2::4] = 行数据[0::4]  # R
        o[3::4] = 行数据[3::4]  # A
        好行.append(bytes(o))
    掩码行 = b"\x00" * (((尺寸 + 31) // 32) * 4)
    return 头 + b"".join(好行) + 掩码行 * 尺寸


def ico(条目):
    """条目 = [(尺寸, payload)]，payload 已经是 BMP 条或 PNG。"""
    头 = struct.pack("<HHH", 0, 1, len(条目))
    偏移 = 6 + 16 * len(条目)
    目录 = b""
    数据 = b""
    for 尺寸, payload in 条目:
        wh = 0 if 尺寸 >= 256 else 尺寸  # 256 在 ICO 目录里写成 0
        目录 += struct.pack("<BBBBHHII", wh, wh, 0, 0, 1, 32, len(payload), 偏移)
        偏移 += len(payload)
        数据 += payload
    return 头 + 目录 + 数据


def 文字预览(尺寸, rgba):
    """把渲染结果打成字符画，好在终端里直接看一眼 16px 到底糊没糊。"""
    out = []
    for y in range(尺寸):
        row = ""
        for x in range(尺寸):
            o = (y * 尺寸 + x) * 4
            a = rgba[o + 3]
            if a < 40:
                row += " "
            else:
                # 用亮度挑字符：键帽面亮、键帽底中、音符暗
                亮 = (rgba[o] * 299 + rgba[o + 1] * 587 + rgba[o + 2] * 114) // 1000
                row += " " if 亮 > 225 else ("." if 亮 > 175 else ("+" if 亮 > 110 else "#"))
        out.append(row)
    return "\n".join(out)


def main():
    根 = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    目标 = os.path.join(根, "MidiPerformer.App", "Assets", "appicon.ico")
    预览 = os.path.join(根, ".scratch", "appicon-preview")
    os.makedirs(os.path.dirname(目标), exist_ok=True)
    os.makedirs(预览, exist_ok=True)

    # Windows 会挑最接近的那一档。16 是主战场（标题栏/任务栏），256 给大图标视图。
    尺寸们 = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    条目 = []
    for s in 尺寸们:
        rgba = 画一遍(s)
        open(os.path.join(预览, f"appicon-{s}.png"), "wb").write(png(s, rgba))
        # 256 走 PNG 条（Windows Vista 以后都认）：BMP 那一版要 256KB
        条目.append((s, png(s, rgba) if s >= 256 else bmp条(s, rgba)))

    ico数据 = ico(条目)
    open(目标, "wb").write(ico数据)

    print(f"写出 {目标}（{len(ico数据)} 字节，{len(尺寸们)} 档：{'/'.join(map(str, 尺寸们))}）")
    print(f"预览 PNG 在 {预览}")
    for s in (16, 32):
        print(f"\n---- {s}x{s} 长这样 ----")
        print(文字预览(s, 画一遍(s)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
