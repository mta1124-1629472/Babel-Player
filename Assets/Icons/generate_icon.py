#!/usr/bin/env python3
"""
Babel Player Icon Generator
Run: python generate_icon.py
Output: babel_icon_full.png (1024x1024 RGBA PNG)
Requires: Pillow  (pip install Pillow)
"""

import math, os
from PIL import Image, ImageDraw

SIZE   = 1024
PAD    = 18
cx, cy = SIZE // 2, SIZE // 2
R      = SIZE // 2 - PAD        # globe radius ~494px

BG    = (13,  27,  52,  255)    # deep navy background
NAVY  = (22,  58,  105, 255)    # ocean fill
GOLD  = (185, 140, 50,  255)    # landmass gold
WHITE = (255, 255, 255, 255)
LINE_W = 9
CORNER = 180                    # rounded-square corner radius

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def rounded_rect(draw, xy, radius, fill):
    x0, y0, x1, y1 = xy
    draw.rectangle([x0+radius, y0, x1-radius, y1], fill=fill)
    draw.rectangle([x0, y0+radius, x1, y1-radius], fill=fill)
    for ex, ey in [(x0,y0),(x1-2*radius,y0),(x0,y1-2*radius),(x1-2*radius,y1-2*radius)]:
        draw.ellipse([ex, ey, ex+2*radius, ey+2*radius], fill=fill)

def sc(pts, r=R, ox=cx, oy=cy):
    """Scale normalised [-1,1] continent coords to pixel space."""
    return [(int(ox + x*r), int(oy + y*r)) for x, y in pts]

# ---------------------------------------------------------------------------
# Continent polygons  (normalised -1..1, Y down)
# ---------------------------------------------------------------------------
CONTINENTS = [
    # North America
    [(-0.62,-0.62),(-0.30,-0.72),(-0.14,-0.55),(-0.10,-0.30),
     (-0.26,-0.10),(-0.46,-0.08),(-0.62,-0.28),(-0.68,-0.48)],
    # South America
    [(-0.38, 0.08),(-0.18, 0.02),(-0.10, 0.20),(-0.16, 0.60),
     (-0.32, 0.72),(-0.44, 0.52),(-0.46, 0.28),(-0.40, 0.14)],
    # Europe
    [( 0.04,-0.64),( 0.18,-0.70),( 0.26,-0.58),( 0.22,-0.44),
     ( 0.10,-0.40),( 0.02,-0.50)],
    # Africa
    [( 0.06,-0.30),( 0.24,-0.36),( 0.36,-0.20),( 0.38, 0.10),
     ( 0.28, 0.48),( 0.12, 0.56),( 0.00, 0.40),( 0.00, 0.10),(-0.06,-0.10)],
    # Asia
    [( 0.16,-0.72),( 0.50,-0.68),( 0.72,-0.48),( 0.78,-0.20),
     ( 0.64, 0.02),( 0.48, 0.08),( 0.32,-0.02),( 0.26,-0.20),
     ( 0.28,-0.38),( 0.14,-0.44),( 0.02,-0.52),( 0.08,-0.64)],
    # Australia
    [( 0.50, 0.22),( 0.66, 0.20),( 0.74, 0.34),( 0.70, 0.50),
     ( 0.54, 0.56),( 0.44, 0.46),( 0.42, 0.32)],
]

# ---------------------------------------------------------------------------
# Build image
# ---------------------------------------------------------------------------
img  = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
draw = ImageDraw.Draw(img)

# Navy rounded-square background
rounded_rect(draw, [0, 0, SIZE-1, SIZE-1], CORNER, BG)

# --- Globe layer (clipped to circle) ---
globe = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
gd    = ImageDraw.Draw(globe)

gd.ellipse([cx-R, cy-R, cx+R, cy+R], fill=NAVY)   # ocean

for poly in CONTINENTS:                             # landmasses
    gd.polygon(sc(poly), fill=GOLD)

# Circular clip mask
mask = Image.new("L", (SIZE, SIZE), 0)
ImageDraw.Draw(mask).ellipse([cx-R, cy-R, cx+R, cy+R], fill=255)
globe.putalpha(mask)
img.paste(globe, (0, 0), globe)

draw = ImageDraw.Draw(img)

# --- Latitude / longitude grid ---
for lon_deg in range(-90, 91, 30):                  # 7 meridians
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

for lat_deg in [-60, -30, 0, 30, 60]:              # 5 parallels
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

# Globe outer ring
draw.ellipse([cx-R, cy-R, cx+R, cy+R], outline=WHITE, width=LINE_W+2)

# --- Play triangle ---
tri_h  = int(R * 0.52)
tri_w  = int(R * 0.58)
t_cx   = cx + int(R * 0.06)    # slight optical right offset
t_cy   = cy
draw.polygon([
    (t_cx - tri_w//2, t_cy - tri_h//2),
    (t_cx + tri_w//2, t_cy),
    (t_cx - tri_w//2, t_cy + tri_h//2),
], fill=WHITE)

# ---------------------------------------------------------------------------
# Save
# ---------------------------------------------------------------------------
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "babel_icon_full.png")
img.save(out, "PNG")
print(f"Saved: {out}  ({SIZE}x{SIZE} RGBA PNG)")
