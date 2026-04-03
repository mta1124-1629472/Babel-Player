#!/usr/bin/env python3
"""
Babel Player Icon Generator
Run: python generate_icon.py
Output: babel_icon_full.png (1024x1024 RGBA PNG)
Requires: Pillow  (pip install Pillow)

Font fallback order (first found wins):
  Windows: C:/Windows/Fonts/arialbd.ttf  (Arial Bold)
  Linux:   /usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf
  macOS:   /System/Library/Fonts/Helvetica.ttc
"""

import math, os, sys
from PIL import Image, ImageDraw, ImageFont

SIZE   = 1024
PAD    = 18
cx, cy = SIZE // 2, SIZE // 2
R      = SIZE // 2 - PAD

BG    = (13,  27,  52,  255)   # deep navy background
NAVY  = (22,  58,  105, 255)   # ocean blue
GOLD  = (185, 140, 50,  255)   # warm gold
WHITE = (255, 255, 255, 255)
LINE_W = 9
CORNER = 180

# --- Font resolution (cross-platform) ---
FONT_CANDIDATES = [
    "C:/Windows/Fonts/arialbd.ttf",          # Windows Arial Bold
    "C:/Windows/Fonts/calibrib.ttf",         # Windows Calibri Bold
    "C:/Windows/Fonts/segoeuib.ttf",         # Windows Segoe UI Bold
    "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",  # Linux
    "/System/Library/Fonts/Helvetica.ttc",   # macOS
    "/Library/Fonts/Arial Bold.ttf",         # macOS alt
]

font_path = next((f for f in FONT_CANDIDATES if os.path.exists(f)), None)
if font_path is None:
    print("ERROR: No bold font found. Install Pillow's default or add a .ttf path above.")
    sys.exit(1)
print(f"Using font: {font_path}")

# --- Helpers ---
def rounded_rect(draw, xy, radius, fill):
    x0, y0, x1, y1 = xy
    draw.rectangle([x0+radius, y0, x1-radius, y1], fill=fill)
    draw.rectangle([x0, y0+radius, x1, y1-radius], fill=fill)
    for ex, ey in [(x0,y0),(x1-2*radius,y0),(x0,y1-2*radius),(x1-2*radius,y1-2*radius)]:
        draw.ellipse([ex, ey, ex+2*radius, ey+2*radius], fill=fill)

# --- Build image ---
img  = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
draw = ImageDraw.Draw(img)
rounded_rect(draw, [0, 0, SIZE-1, SIZE-1], CORNER, BG)

# --- Globe layer ---
globe = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
gd    = ImageDraw.Draw(globe)
gd.ellipse([cx-R, cy-R, cx+R, cy+R], fill=NAVY)

# --- Gold "B" using real font ---
font_size = int(R * 1.44)
font = ImageFont.truetype(font_path, font_size)
bbox = gd.textbbox((0, 0), "B", font=font)
bw, bh = bbox[2]-bbox[0], bbox[3]-bbox[1]
b_x = cx - bw//2 - int(R * 0.04)
b_y = cy - bh//2 - int(R * 0.02)
gd.text((b_x, b_y), "B", font=font, fill=GOLD)

# --- Circular clip ---
mask = Image.new("L", (SIZE, SIZE), 0)
ImageDraw.Draw(mask).ellipse([cx-R, cy-R, cx+R, cy+R], fill=255)
globe.putalpha(mask)
img.paste(globe, (0, 0), globe)
draw = ImageDraw.Draw(img)

# --- Grid lines ---
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

# --- Play triangle (sits in the open bowl of the B) ---
tri_h = int(R * 0.36)
tri_w = int(R * 0.40)
t_cx  = cx + int(R * 0.14)
t_cy  = cy + int(R * 0.02)
draw.polygon([
    (t_cx - tri_w//2, t_cy - tri_h//2),
    (t_cx + tri_w//2, t_cy),
    (t_cx - tri_w//2, t_cy + tri_h//2),
], fill=WHITE)

# --- Save ---
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "babel_icon_full.png")
img.save(out, "PNG")
print(f"Saved: {out}  ({SIZE}x{SIZE} RGBA PNG)")
