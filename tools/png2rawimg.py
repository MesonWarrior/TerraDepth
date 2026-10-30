#!/usr/bin/env python3
"""Converts PNG files to tModLoader's .rawimg texture format (int32 version=1, int32 width, int32 height, RGBA bytes).

tModLoader normally converts PNGs itself while packaging, but that needs the FNA3D native library, which is not
available when packaging headlessly. Usage: png2rawimg.py <src_dir> <dst_dir>
"""
import os
import struct
import sys
import zlib


def read_png(path):
    data = open(path, 'rb').read()
    if data[:8] != b'\x89PNG\r\n\x1a\n':
        raise ValueError(f'{path}: not a PNG')
    pos, idat, palette, trns = 8, b'', None, None
    while pos < len(data):
        length = struct.unpack('>I', data[pos:pos + 4])[0]
        kind, body = data[pos + 4:pos + 8], data[pos + 8:pos + 8 + length]
        if kind == b'IHDR':
            w, h, depth, color, _, _, interlace = struct.unpack('>IIBBBBB', body)
        elif kind == b'PLTE':
            palette = [tuple(body[i:i + 3]) for i in range(0, len(body), 3)]
        elif kind == b'tRNS':
            trns = body
        elif kind == b'IDAT':
            idat += body
        pos += 12 + length
    if depth != 8 or interlace != 0:
        raise ValueError(f'{path}: only 8-bit, non-interlaced PNGs are supported')
    channels = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[color]
    stride = w * channels
    raw = zlib.decompress(idat)
    prev = bytearray(stride)
    rgba = bytearray()
    p = 0
    for _ in range(h):
        f = raw[p]
        line = bytearray(raw[p + 1:p + 1 + stride])
        p += 1 + stride
        for x in range(stride):
            a = line[x - channels] if x >= channels else 0
            b = prev[x]
            c = prev[x - channels] if x >= channels else 0
            if f == 1:
                line[x] = (line[x] + a) & 255
            elif f == 2:
                line[x] = (line[x] + b) & 255
            elif f == 3:
                line[x] = (line[x] + (a + b) // 2) & 255
            elif f == 4:
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                line[x] = (line[x] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        for x in range(w):
            px = line[x * channels:(x + 1) * channels]
            if color == 6:
                r, g, bl, al = px
            elif color == 2:
                (r, g, bl), al = px, 255
            elif color == 0:
                r = g = bl = px[0]
                al = 255
            elif color == 4:
                r = g = bl = px[0]
                al = px[1]
            else:
                r, g, bl = palette[px[0]]
                al = trns[px[0]] if trns and px[0] < len(trns) else 255
            rgba += bytes((r, g, bl, al) if al else (0, 0, 0, 0))
        prev = line
    return w, h, bytes(rgba)


def main(src, dst):
    os.makedirs(dst, exist_ok=True)
    for name in sorted(os.listdir(src)):
        if not name.lower().endswith('.png'):
            continue
        w, h, rgba = read_png(os.path.join(src, name))
        out = os.path.join(dst, os.path.splitext(name)[0] + '.rawimg')
        with open(out, 'wb') as f:
            f.write(struct.pack('<iii', 1, w, h))
            f.write(rgba)
        print(f'{name} -> {os.path.relpath(out)} ({w}x{h})')


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2])
