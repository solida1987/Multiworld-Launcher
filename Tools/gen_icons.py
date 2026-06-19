"""Generate placeholder solid-color 256x256 PNG icons for each game plugin.
Run from the Launcher V2.0.0 directory:  python Tools/gen_icons.py
"""

import os, struct, zlib

def make_png(width, height, r, g, b):
    """Build a minimal solid-color PNG in pure Python (no Pillow)."""
    def chunk(name: bytes, data: bytes) -> bytes:
        body = name + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    sig  = b"\x89PNG\r\n\x1a\n"
    ihdr = chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
    row  = bytes([0]) + bytes([r, g, b] * width)   # filter-none row
    raw  = row * height
    idat = chunk(b"IDAT", zlib.compress(raw, 6))
    iend = chunk(b"IEND", b"")
    return sig + ihdr + idat + iend

ICONS = [
    # filename                        R    G    B
    ("diablo2_archipelago.png",      180,  30,  20),   # blood-red
    ("openttd_archipelago.png",       20,  90, 180),   # transport-blue
    ("placeholder.png",               80,  80, 120),   # neutral purple-gray
]

def main():
    out_dir = os.path.join(os.path.dirname(__file__), "..", "Assets")
    os.makedirs(out_dir, exist_ok=True)
    for fname, r, g, b in ICONS:
        path = os.path.join(out_dir, fname)
        with open(path, "wb") as f:
            f.write(make_png(256, 256, r, g, b))
        print(f"  Written: {path}")
    print("Done.")

if __name__ == "__main__":
    main()
