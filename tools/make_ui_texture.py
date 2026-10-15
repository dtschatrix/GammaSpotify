#!/usr/bin/env python3
# Makes the PDA tab's texture and its texture list:
#   gamedata/textures/ui/ui_spotify_radio.dds                  uncompressed 32-bit, no mipmaps
#   gamedata/configs/ui/textures_descr/ui_spotify_radio.xml    the named pieces in it
# The engine reads every file in textures_descr, so the mod adds its own without touching the game's.
#
# Pieces:
#   spr_white                  plain white; statics tint it in the XML (<texture r= g= b=>)
#   spr_btn_<w>x<h>_e/_h/_t/_d one per button size and state (normal, mouse over, pressed,
#                              disabled): a fill with a 1 px border, drawn at the button's size
#   spr_edit_b/_back/_e        the search box: left end, middle (repeated), right end
# Every piece is surrounded by a copy of its own edge pixels, so filtering never picks up a
# neighbour. Colours are those of the "PDA Radio Tab" design.
import os, struct

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "mod", "Spotify Radio", "gamedata")
W, H = 512, 256

STATES = {                       # state: (fill, border)
    "e": ((38, 44, 35), (74, 84, 66)),
    "h": ((52, 60, 47), (106, 120, 96)),
    "t": ((22, 26, 20), (74, 84, 66)),
    "d": ((30, 34, 28), (58, 66, 52)),
}
BUTTONS = [(172, 28), (52, 32), (96, 32), (64, 32), (106, 32), (62, 24), (52, 22), (62, 22), (96, 30), (32, 22)]
EDIT_FILL, EDIT_EDGE, EDIT_H = (14, 17, 13), (106, 120, 96), 30

px = bytearray(W * H * 4)
pieces = []                      # (id, x, y, w, h)
shelf = {"x": 0, "y": 0, "h": 0}


def place(w, h):
    """Room for a w x h piece plus its 1 px margin, row by row."""
    if shelf["x"] + w + 2 > W:
        shelf["x"], shelf["y"], shelf["h"] = 0, shelf["y"] + shelf["h"], 0
    x, y = shelf["x"] + 1, shelf["y"] + 1
    shelf["x"] += w + 2
    shelf["h"] = max(shelf["h"], h + 2)
    assert shelf["y"] + shelf["h"] <= H, "texture too small"
    return x, y


def piece(name, w, h, colour_at):
    x0, y0 = place(w, h)
    for yy in range(-1, h + 1):
        for xx in range(-1, w + 1):
            r, g, b = colour_at(min(max(xx, 0), w - 1), min(max(yy, 0), h - 1))
            i = ((y0 + yy) * W + x0 + xx) * 4
            px[i:i + 4] = bytes((b, g, r, 255))      # BGRA
    pieces.append((name, x0, y0, w, h))


piece("spr_white", 8, 8, lambda x, y: (255, 255, 255))
for w, h in BUTTONS:
    for s, (fill, edge) in STATES.items():
        piece(f"spr_btn_{w}x{h}_{s}", w, h,
              lambda x, y, w=w, h=h, fill=fill, edge=edge: edge if x in (0, w - 1) or y in (0, h - 1) else fill)
edge_row = lambda y: y in (0, EDIT_H - 1)
piece("spr_edit_b", 2, EDIT_H, lambda x, y: EDIT_EDGE if x == 0 or edge_row(y) else EDIT_FILL)
piece("spr_edit_back", 4, EDIT_H, lambda x, y: EDIT_EDGE if edge_row(y) else EDIT_FILL)
piece("spr_edit_e", 2, EDIT_H, lambda x, y: EDIT_EDGE if x == 1 or edge_row(y) else EDIT_FILL)

# DDS: a 128-byte header, then the rows top to bottom
header = struct.pack("<4s7I11I8I5I", b"DDS ",
                     124, 0x100F, H, W, W * 4, 0, 0,               # size, flags (caps|height|width|pitch|pixelformat), h, w, pitch, depth, mipmaps
                     *([0] * 11),
                     32, 0x41, 0, 32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000,   # A8R8G8B8
                     0x1000, 0, 0, 0, 0)
dds = os.path.join(ROOT, "textures", "ui", "ui_spotify_radio.dds")
os.makedirs(os.path.dirname(dds), exist_ok=True)
with open(dds, "wb") as f:
    f.write(header + bytes(px))

descr = os.path.join(ROOT, "configs", "ui", "textures_descr", "ui_spotify_radio.xml")
os.makedirs(os.path.dirname(descr), exist_ok=True)
with open(descr, "w", newline="\r\n") as f:
    f.write("<w>\n\t<!-- Made by tools/make_ui_texture.py; edit that, not this. -->\n")
    f.write('\t<file name="ui\\ui_spotify_radio">\n')
    for name, x, y, w, h in pieces:
        f.write(f'\t\t<texture id="{name}" x="{x}" y="{y}" width="{w}" height="{h}" />\n')
    f.write("\t</file>\n</w>\n")
print(f"{len(pieces)} pieces, {shelf['y'] + shelf['h']} of {H} rows used: {dds}")
