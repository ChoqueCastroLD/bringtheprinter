import sys
from PIL import Image, ImageDraw, ImageFilter, ImageFont

base_path, out_path = sys.argv[1], sys.argv[2]
S = 4
INK = (42, 37, 34, 255)
BG = (216, 198, 183, 255)
BOX = (196, 180, 166, 255)
TEAL = (111, 146, 145, 255)
FONT = ImageFont.truetype("C:/Windows/Fonts/segoeuil.ttf", 34)


def canvas(w, h):
    return Image.new("RGBA", (w * S, h * S), (0, 0, 0, 0))


def poly(d, pts, fill, width=3):
    d.polygon([(x * S, y * S) for x, y in pts], fill=fill)
    d.line([(x * S, y * S) for x, y in pts + [pts[0]]], fill=INK, width=width * S, joint="curve")


def finish(img, w, h, shadow=None):
    out = Image.new("RGBA", img.size, (0, 0, 0, 0))
    if shadow:
        sh = Image.new("RGBA", img.size, (0, 0, 0, 0))
        ImageDraw.Draw(sh).ellipse([v * S for v in shadow], fill=(90, 75, 65, 120))
        out.alpha_composite(sh.filter(ImageFilter.GaussianBlur(6 * S)))
    out.alpha_composite(img)
    return out.resize((w, h), Image.LANCZOS)


def printer(w=250, h=278, shadow=True):
    img = canvas(w, h)
    d = ImageDraw.Draw(img)
    metal, metal_dark, side = (150, 140, 124, 255), (88, 82, 74, 255), (122, 113, 100, 255)
    poly(d, [(52, 92), (98, 66), (206, 66), (160, 92)], (172, 162, 146, 255))
    poly(d, [(160, 92), (206, 66), (206, 206), (160, 236)], side)
    poly(d, [(172, 118), (194, 104), (194, 172), (172, 188)], (40, 40, 44, 255), 2)
    poly(d, [(52, 92), (160, 92), (160, 236), (52, 236)], (46, 52, 62, 255))
    poly(d, [(60, 132), (146, 132), (152, 122), (66, 122)], (70, 64, 60, 255), 2)
    poly(d, [(98, 120), (128, 120), (130, 114), (100, 114)], (40, 38, 36, 255), 2)
    poly(d, [(60, 196), (148, 196), (154, 184), (66, 184)], (46, 96, 168, 255), 2)
    poly(d, [(52, 200), (160, 200), (160, 236), (52, 236)], metal_dark)
    poly(d, [(62, 208), (110, 208), (110, 228), (62, 228)], (24, 30, 26, 255), 2)
    d.rectangle([66 * S, 213 * S, 82 * S, 223 * S], fill=(80, 220, 90, 255))
    d.rectangle([88 * S, 213 * S, 105 * S, 223 * S], fill=(80, 220, 90, 255))
    poly(d, [(146, 92), (160, 92), (160, 200), (146, 200)], metal)
    glass = Image.new("RGBA", img.size, (0, 0, 0, 0))
    ImageDraw.Draw(glass).polygon([(x * S, y * S) for x, y in [(52, 92), (146, 92), (146, 200), (52, 200)]], fill=(150, 190, 215, 70))
    img.alpha_composite(glass)
    d.line([(64 * S, 104 * S), (84 * S, 104 * S)], fill=(230, 240, 245, 200), width=3 * S)
    return finish(img, w, h, (40, 226, 220, 250) if shadow else None)


def laptop(w=250, h=278, shadow=True):
    img = canvas(w, h)
    d = ImageDraw.Draw(img)
    poly(d, [(60, 70), (176, 58), (192, 150), (70, 166)], (58, 60, 66, 255))
    poly(d, [(70, 80), (170, 70), (183, 142), (78, 155)], (36, 122, 150, 255), 2)
    poly(d, [(100, 96), (146, 91), (152, 124), (106, 130)], (180, 230, 240, 255), 2)
    poly(d, [(70, 166), (192, 150), (226, 196), (90, 216)], (96, 98, 104, 255))
    poly(d, [(86, 172), (186, 158), (206, 186), (100, 200)], (62, 64, 70, 255), 2)
    for i in range(1, 4):
        a = (86 + 14 * i * 0.33, 172 + 28 * i / 4)
        d.line([((86 + (100 - 86) * i / 4) * S, (172 + (200 - 172) * i / 4) * S), ((186 + (206 - 186) * i / 4) * S, (158 + (186 - 158) * i / 4) * S)], fill=(40, 42, 46, 255), width=2 * S)
    poly(d, [(134, 202), (170, 197), (176, 206), (140, 211)], (80, 82, 88, 255), 2)
    return finish(img, w, h, (60, 200, 240, 236) if shadow else None)


def small(img, w=64):
    img = img.crop(img.getbbox())
    return img.resize((w, int(w * img.height / img.width)), Image.LANCZOS)


def tinted(path, fill, w=76):
    src = Image.open(path).convert("RGBA")
    src = src.resize((w * S, w * S), Image.LANCZOS)
    alpha = src.getchannel("A").point(lambda a: 255 if a > 100 else 0)
    outside = Image.new("L", (alpha.width + 2, alpha.height + 2), 0)
    outside.paste(alpha, (1, 1))
    ImageDraw.floodfill(outside, (0, 0), 128)
    sil = outside.crop((1, 1, alpha.width + 1, alpha.height + 1)).point(lambda v: 0 if v == 128 else 255)
    border = sil.filter(ImageFilter.MaxFilter(2 * S + 1))
    out = Image.new("RGBA", src.size, (0, 0, 0, 0))
    out.paste(Image.new("RGBA", src.size, INK), (0, 0), border)
    out.paste(Image.new("RGBA", src.size, fill), (0, 0), alpha)
    return out.resize((w, w), Image.LANCZOS)


page = Image.open(base_path).convert("RGBA")
d = ImageDraw.Draw(page)
d.rectangle([80, 200, 1000, 900], fill=BG)
d.rounded_rectangle([78, 935, 356, 994], radius=12, fill=TEAL)
for top in (215, 595):
    d.rounded_rectangle([91, top, 342, top + 278], radius=22, fill=BOX, outline=(110, 98, 90, 255), width=1)
page.alpha_composite(printer(), (92, 216))
page.alpha_composite(laptop(), (92, 596))

assets = sys.argv[3]
rows = [
    (small(printer(shadow=False), 56), 1),
    (tinted(f"{assets}/icon416.png", (72, 140, 84, 255)), 2),
    (tinted(f"{assets}/icon418.png", (190, 98, 60, 255)), 4),
    (tinted(f"{assets}/icon419.png", (170, 172, 176, 255)), 2),
]
for i, (icon, count) in enumerate(rows):
    y = 238 + i * 64
    icon = small(icon, icon.width if icon.width < 70 else 60)
    page.alpha_composite(icon, (402 - icon.width // 2, y + 30 - icon.height // 2))
    d.text((454, y + 30), f"x{count}", font=FONT, fill=(30, 30, 30, 255), anchor="lm")
icon = small(laptop(shadow=False), 66)
page.alpha_composite(icon, (402 - icon.width // 2, 658 - icon.height // 2))
d.text((454, 658), "x1", font=FONT, fill=(30, 30, 30, 255), anchor="lm")
page.convert("RGB").save(out_path)
