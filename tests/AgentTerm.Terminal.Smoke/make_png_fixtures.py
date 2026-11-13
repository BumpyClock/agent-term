"""Generates PngFixtures.cs: PNG files covering every color type, bit depth, filter type and Adam7, with expected RGBA.

The writer here is independent of AgentTerm's decoder. Expected pixels come from the PNG 1.2 rules applied to the
source samples, and Pillow decodes every 8-bit fixture as a cross-check of the writer.
Run: python -I -B tests/AgentTerm.Terminal.Smoke/make_png_fixtures.py
"""

import base64
import io
import pathlib
import random
import struct
import zlib

ADAM7 = [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)]
CHANNELS = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}


def chunk(kind, data):
    body = kind + data
    return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)


def pack_row(samples, depth):
    if depth == 8:
        return bytes(samples)
    if depth == 16:
        return b"".join(struct.pack(">H", s) for s in samples)
    out, acc, bits = bytearray(), 0, 0
    for s in samples:
        acc = (acc << depth) | s
        bits += depth
        if bits == 8:
            out.append(acc)
            acc = bits = 0
    if bits:
        out.append(acc << (8 - bits))
    return bytes(out)


def paeth(a, b, c):
    p = a + b - c
    pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
    return a if pa <= pb and pa <= pc else b if pb <= pc else c


def filter_row(kind, row, prev, bpp):
    out = bytearray()
    for i, x in enumerate(row):
        a = row[i - bpp] if i >= bpp else 0
        b = prev[i]
        c = prev[i - bpp] if i >= bpp else 0
        predictor = [0, a, b, (a + b) // 2, paeth(a, b, c)][kind]
        out.append((x - predictor) & 0xFF)
    return bytes([kind]) + bytes(out)


def encode(width, height, color, depth, pixels, interlace, palette=None, trns=None):
    channels = CHANNELS[color]
    bpp = max(1, channels * depth // 8)
    raw, line = bytearray(), 0
    passes = ADAM7 if interlace else [(0, 0, 1, 1)]
    for x0, y0, dx, dy in passes:
        xs = list(range(x0, width, dx))
        ys = list(range(y0, height, dy))
        if not xs or not ys:
            continue
        prev = bytes(len(pack_row([0] * channels * len(xs), depth)))
        for y in ys:
            row = pack_row([s for x in xs for s in pixels[y][x]], depth)
            raw += filter_row(line % 5, row, prev, bpp)
            prev = row
            line += 1
    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, depth, color, 0, 0, interlace))
    if palette:
        png += chunk(b"PLTE", bytes(v for rgb in palette for v in rgb))
    if trns is not None:
        png += chunk(b"tRNS", trns)
    png += chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b"")
    return png


def scale(value, depth):
    return value >> 8 if depth == 16 else value if depth == 8 else value * 255 // ((1 << depth) - 1)


def expected(color, depth, pixels, palette=None, trns=None):
    out = bytearray()
    for row in pixels:
        for s in row:
            if color == 0:
                g = scale(s[0], depth)
                alpha = 0 if trns is not None and s[0] == struct.unpack(">H", trns)[0] else 255
                out += bytes([g, g, g, alpha])
            elif color == 2:
                alpha = 0 if trns is not None and tuple(s) == struct.unpack(">HHH", trns) else 255
                out += bytes([scale(s[0], depth), scale(s[1], depth), scale(s[2], depth), alpha])
            elif color == 3:
                r, g, b = palette[s[0]]
                alpha = trns[s[0]] if trns is not None and s[0] < len(trns) else 255
                out += bytes([r, g, b, alpha])
            elif color == 4:
                g = scale(s[0], depth)
                out += bytes([g, g, g, scale(s[1], depth)])
            else:
                out += bytes(scale(v, depth) for v in s)
    return bytes(out)


def main():
    rng = random.Random(1729)
    cases = []
    for color, depths in [(0, [1, 2, 4, 8, 16]), (2, [8, 16]), (3, [1, 2, 4, 8]), (4, [8, 16]), (6, [8, 16])]:
        for depth in depths:
            for interlace in (0, 1):
                width, height = 11, 9
                top = (1 << depth) - 1
                palette = trns = None
                if color == 3:
                    count = min(1 << depth, 6)
                    palette = [tuple(rng.randrange(256) for _ in range(3)) for _ in range(count)]
                    trns = bytes(rng.randrange(256) for _ in range(count - 1))
                    pixels = [[(rng.randrange(count),) for _ in range(width)] for _ in range(height)]
                else:
                    channels = CHANNELS[color]
                    pixels = [[tuple(rng.randrange(top + 1) for _ in range(channels)) for _ in range(width)] for _ in range(height)]
                    if color == 0:
                        trns = struct.pack(">H", pixels[0][0][0])
                    elif color == 2:
                        trns = struct.pack(">HHH", *pixels[0][0])
                png = encode(width, height, color, depth, pixels, interlace, palette, trns)
                rgba = expected(color, depth, pixels, palette, trns)
                if depth == 8:
                    from PIL import Image
                    decoded = Image.open(io.BytesIO(png))
                    decoded.load()
                    check = decoded.convert("RGBA").tobytes()
                    if check != rgba:
                        raise SystemExit(f"Pillow disagrees with the writer for color {color} depth {depth} interlace {interlace}")
                cases.append((f"color {color} depth {depth} interlace {interlace}", width, height, png, rgba))
    lines = [
        "// Generated by make_png_fixtures.py. Do not edit.",
        "internal static class PngFixtures",
        "{",
        "    internal static readonly (string Name, int Width, int Height, string Png, string Rgba)[] All =",
        "    [",
    ]
    for name, width, height, png, rgba in cases:
        lines.append(f'        ("{name}", {width}, {height}, "{base64.b64encode(png).decode()}", "{base64.b64encode(rgba).decode()}"),')
    lines += ["    ];", "}", ""]
    target = pathlib.Path(__file__).with_name("PngFixtures.cs")
    target.write_text("\n".join(lines), encoding="utf-8", newline="\n")
    print(f"Wrote {len(cases)} fixtures to {target}")


if __name__ == "__main__":
    main()
