"""Generate the launcher's application icon.

Deliberately not Claude's mark: this is an independent third-party tool, so the icon uses
a neutral "session list + launch" motif rather than anything resembling official branding.
Drawn at 8x and downsampled so the small sizes stay clean.
"""
import os
from PIL import Image, ImageDraw

OUT_DIR = r"D:\ClaudeLauncher\src\ClaudeLauncher.App"
ICO = os.path.join(OUT_DIR, "app.ico")
PREVIEW = r"D:\Temp\claude\claude-launcher\icon_preview.png"

ACCENT = (201, 100, 66)        # #C96442 — same warm tone as the app's primary button
ACCENT_DARK = (168, 78, 49)
CREAM = (245, 240, 235)
CREAM_DIM = (245, 240, 235, 170)

SS = 8                          # supersample factor


def rounded(draw, box, radius, fill):
    draw.rounded_rectangle(box, radius=radius, fill=fill)


def render(size):
    """Draw one icon at `size` px, via an 8x supersampled canvas.

    Small sizes get their own simplified artwork rather than a downscaled large one:
    at 16 px, three thin bars plus a dot column merge into an unreadable blob.
    """
    s = size * SS
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    small = size <= 32

    # Background tile. A filled shape reads on both light and dark taskbars, which a
    # dark-on-transparent glyph would not.
    pad = s * (0.02 if small else 0.045)
    radius = s * (0.20 if small else 0.225)
    if not small:
        rounded(d, (pad, pad, s - pad, s - pad), radius, ACCENT_DARK)
        rounded(d, (pad, pad, s - pad, s - pad * 1.6), radius, ACCENT)
    else:
        # No bottom shadow lip: at this scale it just muddies the edge.
        rounded(d, (pad, pad, s - pad, s - pad), radius, ACCENT)

    if small:
        # Two thick bars, full contrast, no dot column. Fewer, larger shapes survive
        # the pixel grid; a third bar would close the gaps.
        bar_h = s * 0.155
        gap = s * 0.145
        left = s * 0.20
        top = s * 0.295
        for i, w in enumerate((0.60, 0.42)):
            y = top + i * (bar_h + gap)
            rounded(d, (left, y, left + s * w, y + bar_h), bar_h / 2, CREAM)
    else:
        # Three stacked rows = a session list, with a leading dot column for the
        # catalog feel. Decreasing width gives the shape a direction.
        bar_h = s * 0.098
        gap = s * 0.072
        left = s * 0.235
        top = s * 0.285
        for i, w in enumerate((0.53, 0.43, 0.33)):
            y = top + i * (bar_h + gap)
            rounded(d, (left, y, left + s * w, y + bar_h), bar_h / 2,
                    CREAM if i == 0 else CREAM_DIM)

        dot_r = bar_h * 0.36
        dot_x = s * 0.155
        for i in range(3):
            y = top + i * (bar_h + gap) + bar_h / 2
            d.ellipse((dot_x - dot_r, y - dot_r, dot_x + dot_r, y + dot_r),
                      fill=CREAM if i == 0 else CREAM_DIM)

    return img.resize((size, size), Image.LANCZOS)


sizes = [256, 128, 64, 48, 32, 24, 16]
images = [render(n) for n in sizes]

os.makedirs(os.path.dirname(PREVIEW), exist_ok=True)
images[0].save(ICO, format="ICO", sizes=[(n, n) for n in sizes])

# Side-by-side preview at real scale, on a dark strip like a taskbar.
strip_h = 300
strip = Image.new("RGBA", (sum(sizes) + 40 * len(sizes), strip_h), (32, 32, 36, 255))
x = 20
for img, n in zip(images, sizes):
    strip.alpha_composite(img, (x, (strip_h - n) // 2))
    x += n + 40
# Second row: the small sizes blown up 6x (nearest) so the pixel grid is visible.
zoom = [n for n in sizes if n <= 48]
zh = 6
row2_h = max(zoom) * zh + 40
big = Image.new("RGBA", (strip.width, strip_h + row2_h), (32, 32, 36, 255))
big.alpha_composite(strip, (0, 0))
x = 20
for n in zoom:
    im = render(n).resize((n * zh, n * zh), Image.NEAREST)
    big.alpha_composite(im, (x, strip_h + 20))
    x += n * zh + 30
big.save(PREVIEW)

print("ICO :", ICO, "(%d 个尺寸)" % len(sizes))
print("预览:", PREVIEW)
print("尺寸:", sizes)
