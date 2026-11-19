# -*- coding: utf-8 -*-
"""生成 日置3238上位机 的图标（多尺寸 ICO）"""
import io
import os
import struct

from PIL import Image, ImageDraw, ImageFont

OUT_DIR = r'D:\开发板\日置3238上位机V2'
FONT = r'C:\Windows\Fonts\consolab.ttf'

BODY_TOP = (38, 50, 70)
BODY_BOTTOM = (15, 23, 38)
SCREEN = (8, 15, 26)
GREEN = (77, 255, 158)
CYAN = (154, 220, 255)


def make(size):
    scale = 8 if size <= 64 else 4
    S = size * scale
    img = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    r = int(S * 0.22)

    # 竖向渐变机身
    grad = Image.new('RGBA', (1, S))
    gd = ImageDraw.Draw(grad)
    for y in range(S):
        t = y / max(S - 1, 1)
        c = tuple(int(BODY_TOP[i] * (1 - t) + BODY_BOTTOM[i] * t) for i in range(3))
        gd.point((0, y), fill=c + (255,))
    mask = Image.new('L', (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, S - 1, S - 1], radius=r, fill=255)
    img.paste(grad.resize((S, S)), (0, 0), mask)

    # 外描边
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([0, 0, S - 1, S - 1], radius=r, outline=(70, 88, 112, 255),
                        width=max(1, int(S * 0.012)))

    # 屏幕
    x0, x1 = int(S * 0.12), int(S * 0.88)
    y0, y1 = int(S * 0.30), int(S * 0.70)
    d.rounded_rectangle([x0, y0, x1, y1], radius=int(S * 0.07), fill=SCREEN)
    d.rounded_rectangle([x0, y0, x1, y1], radius=int(S * 0.07),
                        outline=(45, 61, 82, 255), width=max(1, int(S * 0.010)))

    # 数字
    text = '1.23' if size >= 40 else '1.2'
    fs = int((y1 - y0) * (0.62 if len(text) == 4 else 0.80))
    font = ImageFont.truetype(FONT, fs)
    bbox = d.textbbox((0, 0), text, font=font)
    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    tx = (x0 + x1) / 2 - tw / 2 - bbox[0]
    ty = (y0 + y1) / 2 - th / 2 - bbox[1]
    d.text((tx, ty), text, font=font, fill=GREEN + (255,))

    # 底部一条青色指示条
    bw, bh = int(S * 0.30), max(2, int(S * 0.035))
    bx = (S - bw) // 2
    by = int(S * 0.79)
    d.rounded_rectangle([bx, by, bx + bw, by + bh], radius=bh // 2, fill=CYAN + (220,))

    return img.resize((size, size), Image.LANCZOS)


def bmp_frame(img):
    """32 位 BMP 帧（含 AND mask）"""
    w, h = img.size
    px = img.load()
    rows = []
    for y in range(h - 1, -1, -1):
        row = bytearray()
        for x in range(w):
            r, g, b, a = px[x, y]
            row += bytes((b, g, r, a))
        rows.append(bytes(row))
    pixels = b''.join(rows)

    mask_row = bytes(((w + 31) // 32) * 4)
    mask = mask_row * h

    header = struct.pack('<IiiHHIIiiII', 40, w, h * 2, 1, 32, 0, len(pixels), 0, 0, 0, 0)
    return header + pixels + mask


def png_frame(im):
    buf = io.BytesIO()
    im.save(buf, format='PNG')
    return buf.getvalue()


def write_ico(path, images):
    frames = [png_frame(im) if im.size[0] >= 64 else bmp_frame(im) for im in images]
    count = len(images)
    offset = 6 + 16 * count
    out = io.BytesIO()
    out.write(struct.pack('<HHH', 0, 1, count))
    for im, data in zip(images, frames):
        w, h = im.size
        out.write(struct.pack('<BBBBHHII', w % 256, h % 256, 0, 0, 1, 32, len(data), offset))
        offset += len(data)
    for data in frames:
        out.write(data)
    with open(path, 'wb') as f:
        f.write(out.getvalue())


def main():
    sizes = [16, 24, 32, 48, 64, 128, 256]
    imgs = [make(s) for s in sizes]

    # 预览图：把各尺寸并排放大到 128 方便肉眼检查
    sheet = Image.new('RGBA', (128 * len(sizes) + 20, 150), (240, 242, 246, 255))
    x = 10
    for im in imgs:
        big = im.resize((128, 128), Image.NEAREST)
        sheet.paste(big, (x, 10), big)
        x += 138
    sheet.save(os.path.join(OUT_DIR, 'icon_preview.png'))

    write_ico(os.path.join(OUT_DIR, 'app.ico'), imgs)
    print('icon ok')


if __name__ == '__main__':
    main()
