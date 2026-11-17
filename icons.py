"""Generate SolidWorks-style icons for the Mfg Chamfer add-in.

Isometric stepped block (gray), yellow chamfer along the front top edge that stops
short of the wall with a conical tool runout, plus a small blue chamfer mill.
"""
import math, io, os, sys
import cairosvg
from PIL import Image, ImageEnhance

C30 = math.cos(math.radians(30))

def P(x, y, z):
    return ((x - y) * C30, (x + y) * 0.5 - z)

# ---- geometry (model units) -------------------------------------------------
X1, Y1, Z1 = 11.0, 6.0, 3.2        # lower block
XW, ZW = 3.0, 8.5                  # wall block: x 0..XW, z Z1..ZW
c = 1.7                            # chamfer size
xs = 6.4                           # stop point (tool axis) of the chamfer

def poly(pts, fill, stroke="#2b2b2b", sw=0.28, extra=""):
    d = " ".join(f"{p[0]:.3f},{p[1]:.3f}" for p in pts)
    return f'<polygon points="{d}" fill="{fill}" stroke="{stroke}" stroke-width="{sw}" stroke-linejoin="round" {extra}/>'

def faces(pal):
    top, side, front, cham, cham_d, tool, tool_d = pal
    s = []
    # wall block (behind)
    s.append(poly([P(0, 0, ZW), P(XW, 0, ZW), P(XW, Y1, ZW), P(0, Y1, ZW)], top))          # wall top
    s.append(poly([P(XW, 0, Z1), P(XW, 0, ZW), P(XW, Y1, ZW), P(XW, Y1, Z1)], side))      # wall +x face
    s.append(poly([P(0, Y1, 0), P(XW, Y1, 0), P(XW, Y1, ZW), P(0, Y1, ZW)], front))       # wall +y face
    # lower block top (minus chamfer area drawn later)
    s.append(poly([P(XW, 0, Z1), P(X1, 0, Z1), P(X1, Y1, Z1), P(XW, Y1, Z1)], top))
    # right face (+x)
    s.append(poly([P(X1, 0, 0), P(X1, 0, Z1), P(X1, Y1 - c, Z1), P(X1, Y1, Z1 - c), P(X1, Y1, 0)], side))
    # front face (+y)
    s.append(poly([P(XW, Y1, 0), P(X1, Y1, 0), P(X1, Y1, Z1 - c), P(xs, Y1, Z1 - c),
                   P(xs - c, Y1, Z1), P(XW, Y1, Z1)], front))
    # chamfer face + conical runout
    arc = [P(xs - c * math.sin(t), Y1 - c * math.cos(t), Z1) for t in [i * (math.pi / 2) / 10 for i in range(11)]]
    ch = [P(X1, Y1 - c, Z1)] + arc + [P(xs, Y1, Z1 - c), P(X1, Y1, Z1 - c)]
    s.append(poly(ch, cham, stroke=cham_d, sw=0.32))
    # line where flat chamfer meets the cone
    a, b = P(xs, Y1 - c, Z1), P(xs, Y1, Z1 - c)
    s.append(f'<line x1="{a[0]:.3f}" y1="{a[1]:.3f}" x2="{b[0]:.3f}" y2="{b[1]:.3f}" stroke="{cham_d}" stroke-width="0.22"/>')
    # chamfer mill above the runout (axis vertical through (xs, Y1))
    if tool:
        r, h0, hc, hs = c, 1.4, 1.6, 3.0   # radius, gap above top, cone height, shank height
        cx, cy = P(xs, Y1, Z1 + h0)
        top_y = P(xs, Y1, Z1 + h0 + hc)[1]
        sh_top = P(xs, Y1, Z1 + h0 + hc + hs)[1]
        rw = r * 1.0
        # shank
        s.append(f'<rect x="{cx - rw:.3f}" y="{sh_top:.3f}" width="{2 * rw:.3f}" height="{top_y - sh_top:.3f}" fill="{tool}" stroke="{tool_d}" stroke-width="0.28"/>')
        # cone (tip down)
        s.append(poly([(cx - rw, top_y), (cx + rw, top_y), (cx, cy)], tool, stroke=tool_d))
        s.append(f'<line x1="{cx:.3f}" y1="{sh_top:.3f}" x2="{cx:.3f}" y2="{cy:.3f}" stroke="#ffffff" stroke-width="0.25" stroke-opacity="0.55"/>')
    return s

PAL = ("#eef0f2", "#9aa3ad", "#c3cad1", "#ffd21f", "#9a7400", "#3d8be0", "#123f7a")

def svg(pal=PAL, tool=True):
    pal = list(pal)
    if not tool:
        pal[5] = None
    body = faces(pal)
    # bounding box
    xs_, ys_ = [], []
    for pt in [P(x, y, z) for x in (0, X1) for y in (0, Y1) for z in (0, ZW)]:
        xs_.append(pt[0]); ys_.append(pt[1])
    ys_.append(P(xs, Y1, Z1 + 6.0)[1])
    minx, maxx, miny, maxy = min(xs_), max(xs_), min(ys_), max(ys_)
    w, h = maxx - minx, maxy - miny
    s = max(w, h) * 1.06
    ox = minx - (s - w) / 2
    oy = miny - (s - h) / 2
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="{ox:.3f} {oy:.3f} {s:.3f} {s:.3f}">'
            + "".join(body) + "</svg>")

def render(size, pal=PAL, tool=True, w=None, h=None):
    png = cairosvg.svg2png(bytestring=svg(pal, tool).encode(), output_width=size, output_height=size)
    im = Image.open(io.BytesIO(png)).convert("RGBA")
    if w and h:  # pad to w x h (e.g. 16x18)
        canvas = Image.new("RGBA", (w, h), (0, 0, 0, 0))
        canvas.paste(im, ((w - size) // 2, (h - size) // 2), im)
        im = canvas
    return im

def suppressed(im):
    g = ImageEnhance.Color(im).enhance(0.0)
    g = ImageEnhance.Brightness(g).enhance(1.25)
    a = g.split()[3].point(lambda v: int(v * 0.55))
    g.putalpha(a)
    return g

HL = ("#dfeeff", "#6f9fd6", "#a9c8ec", "#ffe35a", "#1a4f99", "#3d8be0", "#123f7a")

if __name__ == "__main__":
    out = sys.argv[1]
    os.makedirs(out, exist_ok=True)
    # CommandManager icon lists (single-command strips)
    for sz in (20, 32, 40, 64, 96, 128):
        render(sz).save(f"{out}/toolbar_{sz}.png")
    # FeatureManager tree icons: regular / suppressed / highlighted
    for sz in (20, 32, 40):
        reg = render(sz, tool=False)
        reg.save(f"{out}/feat_reg_{sz}.png")
        suppressed(reg).save(f"{out}/feat_sup_{sz}.png")
        render(sz, HL, tool=False).save(f"{out}/feat_hl_{sz}.png")
    # legacy 16x18 bitmaps (for older SW / fallback)
    render(16, tool=False, w=16, h=18).save(f"{out}/feat_reg_16.png")
    suppressed(render(16, tool=False, w=16, h=18)).save(f"{out}/feat_sup_16.png")
    render(16, HL, tool=False, w=16, h=18).save(f"{out}/feat_hl_16.png")
    # PropertyManager title bitmap
    render(20).save(f"{out}/pm_title.png")
    # Add-in manager icon (16x16)
    render(16).save(f"{out}/addin_16.png")
    open(f"{out}/master.svg", "w").write(svg())
    # preview sheet
    sheet = Image.new("RGBA", (560, 170), (255, 255, 255, 255))
    x = 10
    for sz in (20, 32, 40, 64, 128):
        im = Image.open(f"{out}/toolbar_{sz}.png"); sheet.paste(im, (x, 10), im); x += sz + 14
    x = 10
    for n in ("reg", "sup", "hl"):
        for sz in (20, 32):
            im = Image.open(f"{out}/feat_{n}_{sz}.png"); sheet.paste(im, (x, 140 - sz), im); x += sz + 10
    sheet = sheet.resize((sheet.width * 2, sheet.height * 2), Image.NEAREST)
    sheet.save(f"{out}/_preview.png")


# ---- copy / paste variants + 3-icon toolbar strips ---------------------------------------
from PIL import ImageDraw

def _badge_copy(sz):
    """two overlapping sheets, SW-style blue outline"""
    im = Image.new("RGBA", (sz, sz), (0, 0, 0, 0)); d = ImageDraw.Draw(im)
    w = max(1, round(sz / 20))
    s = sz * 0.42; x0, y0 = sz - s - sz * 0.02, sz - s - sz * 0.02
    o = s * 0.28
    for dx, dy in ((-o, -o), (0, 0)):
        d.rectangle([x0 + dx, y0 + dy, x0 + dx + s * 0.78, y0 + dy + s], fill=(255, 255, 255, 255), outline=(18, 63, 122, 255), width=w)
    return im

def _badge_paste(sz):
    """clipboard with a sheet"""
    im = Image.new("RGBA", (sz, sz), (0, 0, 0, 0)); d = ImageDraw.Draw(im)
    w = max(1, round(sz / 20))
    s = sz * 0.48; x0, y0 = sz - s - sz * 0.01, sz - s - sz * 0.01
    d.rounded_rectangle([x0, y0 + s * 0.08, x0 + s * 0.82, y0 + s], radius=max(1, s * 0.1), fill=(176, 122, 58, 255), outline=(92, 58, 20, 255), width=w)
    d.rectangle([x0 + s * 0.14, y0 + s * 0.28, x0 + s * 0.68, y0 + s * 0.9], fill=(255, 255, 255, 255))
    d.rectangle([x0 + s * 0.24, y0, x0 + s * 0.58, y0 + s * 0.2], fill=(150, 150, 150, 255), outline=(60, 60, 60, 255), width=w)
    return im

def variant(sz, badge):
    base = render(sz, tool=False)
    small = base.resize((round(sz * 0.8), round(sz * 0.8)), Image.LANCZOS)
    im = Image.new("RGBA", (sz, sz), (0, 0, 0, 0)); im.paste(small, (0, 0), small)
    b = badge(sz); im.alpha_composite(b)
    return im

def strips(out):
    for sz in (20, 32, 40, 64, 96, 128):
        strip = Image.new("RGBA", (sz * 3, sz), (0, 0, 0, 0))
        for i, im in enumerate((render(sz), variant(sz, _badge_copy), variant(sz, _badge_paste))):
            strip.paste(im, (i * sz, 0), im)
        strip.save(f"{out}/toolbar_{sz}.png")
    prev = Image.open(f"{out}/toolbar_64.png"); bg = Image.new("RGBA", prev.size, (255, 255, 255, 255)); bg.alpha_composite(prev)
    bg.resize((prev.width * 2, prev.height * 2), Image.NEAREST).save("/tmp/strip_preview.png")
    prev = Image.open(f"{out}/toolbar_20.png"); bg = Image.new("RGBA", prev.size, (255, 255, 255, 255)); bg.alpha_composite(prev)
    bg.resize((prev.width * 6, prev.height * 6), Image.NEAREST).save("/tmp/strip20_preview.png")

if __name__ == "__main__" and len(sys.argv) > 2 and sys.argv[2] == "strips":
    strips(sys.argv[1])
