"""Draws the Ziri's Workshop icon in two styles, both after Ziri Shortfuse,
a pink-pigtailed gnome tinkerer:

  bomb  a little purple goggle bomb with Ziri's pink pigtails and a lit fuse
  ziri  Ziri's face: pink pigtails and fringe, gnome ears with silver rings,
        brass goggles, and a lit fuse on top of her head

Run: python tools/make_icon.py [bomb|ziri]
Writes assets/icon-option-<style>.png previews for both styles, and
assets/icon.ico + assets/icon-sizes.png for the chosen one (default ziri).
"""
import math
import os
import sys

from PIL import Image, ImageDraw, ImageFilter

S = 1024
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "assets")

INK = (38, 30, 70, 255)
BODY = (104, 82, 178, 255)
BODY_LIGHT = (150, 128, 222, 255)
METAL = (172, 180, 198, 255)
FUSE = (139, 92, 46, 255)
STRAP = (70, 48, 34, 255)
BRASS = (232, 170, 60, 255)
LENS = (196, 240, 255, 255)
PUPIL = (30, 26, 50, 255)
GOLD = (255, 210, 63, 255)
BLUSH = (255, 120, 150, 110)
PINK = (236, 112, 190, 255)
PINK_DARK = (176, 62, 138, 255)
PINK_LIGHT = (255, 170, 222, 255)
SKIN = (250, 204, 176, 255)
SKIN_SHADE = (232, 170, 140, 255)
SILVER = (214, 220, 230, 255)
TIE = (60, 56, 70, 255)


def star(cx, cy, r_out, r_in, points=8, turn=0.0):
    pts = []
    for i in range(points * 2):
        r = r_out if i % 2 == 0 else r_in
        a = turn + math.pi * i / points
        pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))
    return pts


def bezier(p0, p1, p2, steps=24):
    out = []
    for i in range(steps + 1):
        t = i / steps
        x = (1 - t) ** 2 * p0[0] + 2 * (1 - t) * t * p1[0] + t * t * p2[0]
        y = (1 - t) ** 2 * p0[1] + 2 * (1 - t) * t * p1[1] + t * t * p2[1]
        out.append((x, y))
    return out


def layer():
    return Image.new("RGBA", (S, S), (0, 0, 0, 0))


def pigtail(img, side, cx, top_y, spread):
    """One pigtail flaring out and down from the side of the head.
    side = -1 left, +1 right."""
    s = side
    root = (cx + s * spread * 0.6, top_y - 10)
    a = (cx + s * spread * 1.5, top_y + 20)
    tip = (cx + s * spread * 1.38, top_y + 470)
    back = (cx + s * spread * 0.85, top_y + 150)
    pts = []
    pts += bezier(root, (cx + s * spread * 1.05, top_y - 160), a)
    pts += bezier(a, (cx + s * spread * 1.8, top_y + 210), tip)
    pts += bezier(tip, (cx + s * spread * 1.12, top_y + 260), back)
    d = ImageDraw.Draw(img)
    d.polygon(pts, fill=PINK, outline=INK, width=24)
    # strands
    for k, off in enumerate((0.15, 0.45, 0.75)):
        p0 = (cx + s * spread * (0.85 + off * 0.25), top_y + 10 + off * 30)
        p1 = (cx + s * spread * (1.25 + off * 0.2), top_y + 60 + off * 100)
        p2 = (cx + s * spread * (1.22 + off * 0.12), top_y + 280 + off * 150)
        d.line(bezier(p0, p1, p2), fill=PINK_DARK if k != 1 else PINK_LIGHT, width=12, joint="curve")
    # hair tie at the root
    tx, ty = cx + s * spread * 0.86, top_y + 30
    d.ellipse([tx - 42, ty - 42, tx + 42, ty + 42], fill=TIE, outline=INK, width=14)


def goggles(d, cx, ey, gap, r):
    for ex in (cx - gap, cx + gap):
        d.ellipse([ex - r - 18, ey - r - 18, ex + r + 18, ey + r + 18], fill=INK)
        d.ellipse([ex - r, ey - r, ex + r, ey + r], fill=BRASS)
        lr = r * 0.74
        d.ellipse([ex - lr, ey - lr, ex + lr, ey + lr], fill=LENS)
        d.ellipse([ex - lr * 0.6, ey - lr * 0.35, ex + lr * 0.6, ey + lr * 0.84], fill=PUPIL)
        d.ellipse([ex - lr * 0.35, ey - lr * 0.19, ex + lr * 0.03, ey + lr * 0.19], fill=(255, 255, 255, 255))
        d.ellipse([ex + lr * 0.16, ey + lr * 0.35, ex + lr * 0.32, ey + lr * 0.51], fill=(255, 255, 255, 230))
    d.rectangle([cx - gap + r, ey - 10, cx + gap - r, ey + 10], fill=BRASS)


def fuse_and_spark(img, base, tip):
    d = ImageDraw.Draw(img)
    mid = ((base[0] + tip[0]) / 2 + 10, (base[1] + tip[1]) / 2 - 30)
    path = bezier(base, mid, tip, 12)
    d.line(path, fill=INK, width=50, joint="curve")
    d.line(path, fill=FUSE, width=26, joint="curve")
    d.rounded_rectangle([base[0] - 80, base[1] - 20, base[0] + 80, base[1] + 60], radius=22, fill=METAL, outline=INK, width=22)
    glow = layer()
    ImageDraw.Draw(glow).ellipse([tip[0] - 100, tip[1] - 100, tip[0] + 100, tip[1] + 100], fill=(255, 200, 60, 150))
    img.alpha_composite(glow.filter(ImageFilter.GaussianBlur(30)))
    d = ImageDraw.Draw(img)
    d.polygon(star(tip[0], tip[1], 112, 44, 8, 0.2), fill=INK)
    d.polygon(star(tip[0], tip[1], 92, 32, 8, 0.2), fill=GOLD)
    d.ellipse([tip[0] - 22, tip[1] - 22, tip[0] + 22, tip[1] + 22], fill=(255, 255, 240, 255))


def blush_and_smile(img, cx, y, spread, mouth):
    b = layer()
    bd = ImageDraw.Draw(b)
    for bx in (cx - spread, cx + spread):
        bd.ellipse([bx - 54, y - 24, bx + 54, y + 24], fill=BLUSH)
    img.alpha_composite(b.filter(ImageFilter.GaussianBlur(6)))
    d = ImageDraw.Draw(img)
    d.arc([cx - mouth, y - 10, cx + mouth, y + 80], start=20, end=160, fill=INK, width=20)


def draw_bomb():
    img = layer()
    cx, cy, r = 512, 620, 290
    pigtail(img, -1, cx, 400, 300)
    pigtail(img, +1, cx, 400, 300)
    fuse_and_spark(img, (cx, 320), (cx + 150, 150))
    d = ImageDraw.Draw(img)
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=BODY, outline=INK, width=30)
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).ellipse([cx - r + 30, cy - r + 30, cx + r - 30, cy + r - 30], fill=255)
    shade = layer()
    ImageDraw.Draw(shade).ellipse([cx - 210, cy - 250, cx + 50, cy - 40], fill=BODY_LIGHT)
    shade = shade.filter(ImageFilter.GaussianBlur(55))
    img.paste(shade, (0, 0), Image.composite(shade, layer(), mask).split()[3])
    strap = layer()
    ImageDraw.Draw(strap).rectangle([0, cy - 75, S, cy - 5], fill=STRAP)
    img.paste(strap, (0, 0), Image.composite(strap, layer(), mask).split()[3])
    d = ImageDraw.Draw(img)
    goggles(d, cx, cy - 40, 108, 96)
    blush_and_smile(img, cx, cy + 110, 175, 56)
    return img


def draw_ziri():
    img = layer()
    cx, cy = 512, 610
    # pigtails behind everything
    pigtail(img, -1, cx, 330, 270)
    pigtail(img, +1, cx, 330, 270)
    d = ImageDraw.Draw(img)
    # gnome ears: long, pointed, angled down a little
    for s in (-1, 1):
        ear = [(cx + s * 200, cy - 70), (cx + s * 420, cy + 10), (cx + s * 230, cy + 110)]
        d.polygon(ear, fill=SKIN, outline=INK, width=22)
        d.polygon([(cx + s * 225, cy - 25), (cx + s * 360, cy + 15), (cx + s * 240, cy + 70)], fill=SKIN_SHADE)
        # silver ear rings along the ear edge
        for (ex, ey) in ((cx + s * 330, cy + 62), (cx + s * 280, cy + 88)):
            d.ellipse([ex - 20, ey - 20, ex + 20, ey + 20], fill=SILVER, outline=INK, width=8)
    # face
    d.ellipse([cx - 255, cy - 250, cx + 255, cy + 250], fill=SKIN, outline=INK, width=26)
    # fringe: scalloped bangs, then a pink cap over the top of the head
    head = Image.new("L", (S, S), 0)
    ImageDraw.Draw(head).ellipse([cx - 268, cy - 300, cx + 268, cy + 150], fill=255)
    bangs = layer()
    bd = ImageDraw.Draw(bangs)
    for i, bx in enumerate(range(cx - 240, cx + 241, 96)):
        drop = 40 if i % 2 == 0 else 10
        bd.ellipse([bx - 66, cy - 215, bx + 66, cy - 95 + drop], fill=PINK, outline=INK, width=18)
    bangs.putalpha(Image.composite(bangs.split()[3], Image.new("L", (S, S), 0), head))
    img.alpha_composite(bangs)
    cap = layer()
    ImageDraw.Draw(cap).ellipse([cx - 268, cy - 300, cx + 268, cy + 150], fill=PINK)
    top = Image.new("L", (S, S), 0)
    ImageDraw.Draw(top).rectangle([0, 0, S, cy - 160], fill=255)
    cap.putalpha(Image.composite(cap.split()[3], Image.new("L", (S, S), 0), top))
    img.alpha_composite(cap)
    d = ImageDraw.Draw(img)
    # fringe outline + a few strands
    d.arc([cx - 272, cy - 300, cx + 272, cy + 150], start=180, end=360, fill=INK, width=24)
    for a, b, c in (((cx - 120, cy - 250), (cx - 60, cy - 200), (cx - 20, cy - 130)),
                    ((cx + 40, cy - 260), (cx + 90, cy - 200), (cx + 120, cy - 135))):
        d.line(bezier(a, b, c), fill=PINK_DARK, width=12, joint="curve")
    d.line(bezier((cx - 200, cy - 200), (cx - 150, cy - 240), (cx - 60, cy - 268)), fill=PINK_LIGHT, width=14, joint="curve")
    # goggle strap + goggles as her eyes
    strap = layer()
    ImageDraw.Draw(strap).rectangle([cx - 262, cy - 28, cx + 262, cy + 26], fill=STRAP)
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).ellipse([cx - 255, cy - 250, cx + 255, cy + 250], fill=255)
    img.paste(strap, (0, 0), Image.composite(strap, layer(), mask).split()[3])
    d = ImageDraw.Draw(img)
    goggles(d, cx, cy, 112, 98)
    blush_and_smile(img, cx, cy + 135, 165, 44)
    # little fuse on top of her head
    fuse_and_spark(img, (cx, cy - 300), (cx + 140, cy - 470))
    return img


def squared(big):
    box = big.getbbox()
    side = max(box[2] - box[0], box[3] - box[1]) + 24
    sq = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    sq.paste(big.crop(box), ((side - (box[2] - box[0])) // 2, (side - (box[3] - box[1])) // 2))
    return sq


def sizes_sheet(sq):
    sheet = Image.new("RGBA", (520, 280), (240, 240, 244, 255))
    x = 10
    for n in (16, 24, 32, 48, 64, 128):
        im = sq.resize((n, n), Image.LANCZOS)
        sheet.paste(im, (x, 140 - n // 2), im)
        x += n + 20
    dark = Image.new("RGBA", (520, 60), (32, 32, 36, 255))
    x = 10
    for n in (16, 24, 32):
        im = sq.resize((n, n), Image.LANCZOS)
        dark.paste(im, (x, 30 - n // 2), im)
        x += n + 20
    sheet.paste(dark, (0, 220))
    return sheet


def main():
    chosen = sys.argv[1] if len(sys.argv) > 1 else "ziri"
    os.makedirs(OUT, exist_ok=True)
    styles = {"bomb": draw_bomb, "ziri": draw_ziri}
    for name, fn in styles.items():
        sq = squared(fn())
        sq.resize((256, 256), Image.LANCZOS).save(os.path.join(OUT, "icon-option-" + name + ".png"))
        sizes_sheet(sq).save(os.path.join(OUT, "icon-sizes-" + name + ".png"))
        if name == chosen:
            sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
            frames = [sq.resize((n, n), Image.LANCZOS) for n in sizes]
            frames[-1].save(os.path.join(OUT, "icon.ico"), format="ICO", sizes=[(n, n) for n in sizes],
                            append_images=frames[:-1])
    print("icon.ico uses style:", chosen)


if __name__ == "__main__":
    main()
