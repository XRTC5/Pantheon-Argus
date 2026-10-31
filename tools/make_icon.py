"""
Generates argus.ico: the eye, at every size Windows asks for.

Written by hand against zlib rather than pulled from Pillow, because a build
dependency the size of an imaging library is a poor trade for one 3KB file
that changes approximately never.

Run:  py -3.12 tools/make_icon.py
"""

import struct
import zlib
from pathlib import Path

# Matches the panel palette exactly. The tray icon and the header eye being
# visibly different shades of cyan is the kind of detail that reads as sloppy.
ACCENT = (0x22, 0xD3, 0xEE)
DARK = (0x07, 0x0B, 0x10)
RING_BG = (0x0C, 0x12, 0x18)

SIZES = (16, 20, 24, 32, 48, 64, 128, 256)


def _coverage(px, py, cx, cy, radius, samples=4):
    """Box-sampled coverage of a disc over one pixel, for free antialiasing."""
    hits = 0
    step = 1.0 / samples
    for sy in range(samples):
        for sx in range(samples):
            dx = px + (sx + 0.5) * step - cx
            dy = py + (sy + 0.5) * step - cy
            if dx * dx + dy * dy <= radius * radius:
                hits += 1
    return hits / (samples * samples)


def _blend(under, over, alpha):
    return tuple(round(u + (o - u) * alpha) for u, o in zip(under, over))


def render(size):
    """Returns RGBA rows for one square icon."""
    c = size / 2.0
    outer = size * 0.47
    inner_hole = size * 0.385  # ring thickness lives between these two
    pupil = size * 0.155

    # At 16px a proportional ring lands under one pixel and greys out to mush,
    # so the small sizes get a floor of a full pixel of ring and pupil.
    if outer - inner_hole < 1.0:
        inner_hole = outer - 1.0
    if pupil < 1.6:
        pupil = 1.6

    rows = []
    for y in range(size):
        row = bytearray()
        for x in range(size):
            a_outer = _coverage(x, y, c, c, outer)
            a_hole = _coverage(x, y, c, c, inner_hole)
            a_pupil = _coverage(x, y, c, c, pupil)

            # Ring is the area inside the outer disc but outside the hole.
            ring = max(0.0, a_outer - a_hole)
            body = max(0.0, a_hole - a_pupil)

            alpha = max(a_outer, a_pupil)
            if alpha <= 0.0:
                row += bytes((0, 0, 0, 0))
                continue

            colour = DARK
            if body > 0:
                colour = _blend(colour, RING_BG, body)
            if ring > 0:
                colour = _blend(colour, ACCENT, ring)
            if a_pupil > 0:
                colour = _blend(colour, ACCENT, a_pupil)

            row += bytes((*colour, round(alpha * 255)))
        rows.append(bytes(row))
    return rows


def to_png(size, rows):
    raw = b"".join(b"\x00" + r for r in rows)

    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body))

    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(raw, 9))
        + chunk(b"IEND", b"")
    )


def main():
    images = [(s, to_png(s, render(s))) for s in SIZES]

    header = struct.pack("<HHH", 0, 1, len(images))
    offset = len(header) + 16 * len(images)

    entries, blobs = b"", b""
    for size, png in images:
        # 256 is stored as 0 in the directory; the field is a single byte.
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(png), offset)
        blobs += png
        offset += len(png)

    out = Path(__file__).resolve().parent.parent / "src" / "Argus" / "argus.ico"
    out.write_bytes(header + entries + blobs)
    print(f"wrote {out}  ({out.stat().st_size:,} bytes, {len(images)} sizes)")


if __name__ == "__main__":
    main()
