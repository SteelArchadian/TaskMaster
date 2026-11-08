"""Cut the icon art off its white background and write a multi-size .ico plus a preview PNG."""
import sys
from collections import deque
from PIL import Image

src, out_ico, out_png = sys.argv[1:4]
im = Image.open(src).convert("RGB")
w, h = im.size
px = im.load()

# Background = near-white region connected to the borders (flood fill), so pale parts of the art stay opaque.
bg = bytearray(w * h)
q = deque()
def near_white(x, y):
    r, g, b = px[x, y]
    return min(r, g, b) > 228
for x in range(w):
    for y in (0, h - 1):
        if near_white(x, y) and not bg[y * w + x]:
            bg[y * w + x] = 1; q.append((x, y))
for y in range(h):
    for x in (0, w - 1):
        if near_white(x, y) and not bg[y * w + x]:
            bg[y * w + x] = 1; q.append((x, y))
while q:
    x, y = q.popleft()
    for nx, ny in ((x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)):
        if 0 <= nx < w and 0 <= ny < h and not bg[ny * w + nx] and near_white(nx, ny):
            bg[ny * w + nx] = 1; q.append((nx, ny))

# Background pixels: un-blend from white so the soft glow/shadow keeps its colour with partial alpha.
outim = Image.new("RGBA", (w, h))
op = outim.load()
for y in range(h):
    for x in range(w):
        r, g, b = px[x, y]
        if not bg[y * w + x]:
            op[x, y] = (r, g, b, 255)
            continue
        a = max(255 - r, 255 - g, 255 - b) / 255
        a = min(1.0, a * 1.6)  # the glow is faint on white; lift it a little
        if a < 0.06:
            op[x, y] = (0, 0, 0, 0)
            continue
        f = lambda c: max(0, min(255, round((c - 255 * (1 - a)) / a)))
        op[x, y] = (f(r), f(g), f(b), round(a * 255))

box = outim.getchannel("A").point(lambda v: 255 if v > 20 else 0).getbbox()
art = outim.crop(box)
side = max(art.size)
pad = round(side * 0.02)
sq = Image.new("RGBA", (side + 2 * pad, side + 2 * pad))
sq.paste(art, ((sq.width - art.width) // 2, (sq.height - art.height) // 2))
sq = sq.resize((256, 256), Image.LANCZOS)
sq.save(out_png)
sq.save(out_ico, sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (256, 256)])
print("ok", box, sq.size)
