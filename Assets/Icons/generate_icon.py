#!/usr/bin/env python3
"""
Babel Player Icon Generator — "B" Edition
Run: python generate_icon.py
Output: babel_icon_full.png (1024x1024 RGBA PNG)
Requires: Pillow  (pip install Pillow)
"""

import math, os
from PIL import Image, ImageDraw

SIZE   = 1024
PAD    = 18
cx, cy = SIZE // 2, SIZE // 2
R      = SIZE // 2 - PAD

BG    = (13,  27,  52,  255)
NAVY  = (22,  58,  105, 255)
GOLD  = (185, 140, 50,  255)
WHITE = (255, 255, 255, 255)
LINE_W = 9
CORNER = 180

def rounded_rect(draw, xy, radius, fill):
    x0, y0, x1, y1 = xy
    draw.rectangle([x0+radius, y0, x1-radius, y1], fill=fill)
    draw.rectangle([x0, y0+radius, x1, y1-radius], fill=fill)
    for ex, ey in [(x0,y0),(x1-2*radius,y0),(x0,y1-2*radius),(x1-2*radius,y1-2*radius)]:
        draw.ellipse([ex, ey, ex+2*radius, ey+2*radius], fill=fill)

# "B" letterform — gold filled polygons + navy counter cutouts
# All coords normalised to [-1, 1] globe space
SPINE       = [(-0.42,-0.72),(-0.12,-0.72),(-0.12, 0.72),(-0.42, 0.72)]
UPPER_BUMP  = [(-0.12,-0.72),(0.32,-0.72),(0.48,-0.60),(0.52,-0.44),(0.48,-0.28),(0.32,-0.18),(-0.12,-0.18)]
LOWER_BUMP  = [(-0.12,-0.10),(0.36,-0.10),(0.54, 0.04),(0.58, 0.24),(0.52, 0.44),(0.36, 0.58),(-0.12, 0.72)]
MID_CUTOUT  = [(-0.12,-0.18),(0.32,-0.18),(0.32,-0.10),(-0.12,-0.10)]
INNER_UPPER = [(-0.10,-0.64),(0.24,-0.64),(0.36,-0.54),(0.38,-0.44),(0.34,-0.34),(0.22,-0.26),(-0.10,-0.26)]
INNER_LOWER = [(-0.10,-0.02),(0.28,-0.02),(0.42, 0.12),(0.44, 0.26),(0.38, 0.40),(0.26, 0.50),(-0.10, 0.64)]

def sc(pts):
    return [(int(cx + x*R), int(cy + y*R)) for x, y in pts]

img  = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
draw = ImageDraw.Draw(img)
rounded_rect(draw, [0, 0, SIZE-1, SIZE-1], CORNER, BG)

globe = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
gd    = ImageDraw.Draw(globe)
gd.ellipse([cx-R, cy-R, cx+R, cy+R], fill=NAVY)

for poly in [SPINE, UPPER_BUMP, LOWER_BUMP]:
    gd.polygon(sc(poly), fill=GOLD)
for poly in [MID_CUTOUT, INNER_UPPER, INNER_LOWER]:
    gd.polygon(sc(poly), fill=NAVY)

mask = Image.new("L", (SIZE, SIZE), 0)
ImageDraw.Draw(mask).ellipse([cx-R, cy-R, cx+R, cy+R], fill=255)
globe.putalpha(mask)
img.paste(globe, (0, 0), globe)

draw = ImageDraw.Draw(img)

for lon_deg in range(-90, 91, 30):
    lon = math.radians(lon_deg)
    pts = []
    for lat_deg in range(-90, 91, 2):
        lat = math.radians(lat_deg)
        x2d = math.cos(lat) * math.sin(lon)
        y2d = -math.sin(lat)
        if x2d**2 + y2d**2 <= 1.0:
            pts.append((int(cx + x2d*R), int(cy + y2d*R)))
    if len(pts) > 2:
        draw.line(pts, fill=WHITE, width=LINE_W)

for lat_deg in [-60, -30, 0, 30, 60]:
    lat = math.radians(lat_deg)
    pts = []
    for lon_deg in range(-180, 181, 2):
        lon  = math.radians(lon_deg)
        x2d  = math.cos(lat) * math.sin(lon)
        y2d  = -math.sin(lat)
        if x2d**2 + y2d**2 <= 1.0:
            pts.append((int(cx + x2d*R), int(cy + y2d*R)))
    if len(pts) > 2:
        draw.line(pts, fill=WHITE, width=LINE_W)

draw.ellipse([cx-R, cy-R, cx+R, cy+R], outline=WHITE, width=LINE_W+2)

tri_h = int(R * 0.38)
tri_w = int(R * 0.42)
t_cx  = cx + int(R * 0.08)
t_cy  = cy
draw.polygon([
    (t_cx - tri_w//2, t_cy - tri_h//2),
    (t_cx + tri_w//2, t_cy),
    (t_cx - tri_w//2, t_cy + tri_h//2),
], fill=WHITE)

out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "babel_icon_full.png")
img.save(out, "PNG")
print(f"Saved: {out}  ({SIZE}x{SIZE} RGBA PNG)")
