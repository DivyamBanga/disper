"""Generates Disper's icons (app + tray states) with Pillow. Run: python tools/make_icons.py"""
from PIL import Image, ImageDraw
import os

OUT = os.path.join(os.path.dirname(__file__), "..", "src", "Disper", "Assets")
os.makedirs(OUT, exist_ok=True)
SS = 8  # supersampling factor

# Four bars, heights as fraction of the glyph box, like a small voice waveform.
BARS = [0.42, 0.78, 1.0, 0.58]


def draw_bars(draw, box, color, weight=0.16, gap=0.12):
    """Draw rounded vertical bars centered in box=(x0,y0,x1,y1)."""
    x0, y0, x1, y1 = box
    w, h = x1 - x0, y1 - y0
    n = len(BARS)
    bw = w * weight
    g = w * gap
    total = n * bw + (n - 1) * g
    sx = x0 + (w - total) / 2
    cy = y0 + h / 2
    for i, f in enumerate(BARS):
        bh = h * f
        bx0 = sx + i * (bw + g)
        draw.rounded_rectangle([bx0, cy - bh / 2, bx0 + bw, cy + bh / 2], radius=bw / 2, fill=color)


def app_icon(size):
    s = size * SS
    im = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    pad = s * 0.04
    r = s * 0.235
    d.rounded_rectangle([pad, pad, s - pad, s - pad], radius=r, fill=(20, 20, 23, 255))
    # subtle top highlight ring
    d.rounded_rectangle([pad, pad, s - pad, s - pad], radius=r, outline=(255, 255, 255, 26), width=max(1, s // 96))
    inset = s * 0.28
    draw_bars(d, (inset, inset, s - inset, s - inset), (250, 250, 250, 255))
    return im.resize((size, size), Image.LANCZOS)


def glyph_icon(size, color):
    s = size * SS
    im = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    inset = s * 0.12
    draw_bars(d, (inset, inset, s - inset, s - inset), color, weight=0.17, gap=0.10)
    return im.resize((size, size), Image.LANCZOS)


def save_ico(path, frames):
    # Pillow drops any size larger than the first frame, so store the largest first.
    frames = sorted(frames, key=lambda f: -f.width)
    frames[0].save(path, format="ICO", sizes=[(f.width, f.height) for f in frames], append_images=frames[1:])


sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
save_ico(os.path.join(OUT, "disper.ico"), [app_icon(z) for z in sizes])
tray_sizes = [16, 20, 24, 32, 48]
save_ico(os.path.join(OUT, "tray-dark.ico"), [glyph_icon(z, (255, 255, 255, 255)) for z in tray_sizes])   # for dark taskbar
save_ico(os.path.join(OUT, "tray-light.ico"), [glyph_icon(z, (20, 20, 23, 255)) for z in tray_sizes])     # for light taskbar
save_ico(os.path.join(OUT, "tray-active.ico"), [glyph_icon(z, (255, 92, 92, 255)) for z in tray_sizes])   # listening
app_icon(256).save(os.path.join(OUT, "disper-256.png"))
print("icons written to", os.path.abspath(OUT))
