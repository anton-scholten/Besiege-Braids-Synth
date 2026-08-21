#!/usr/bin/env python3
"""Builds the synth block's mesh and texture.

    ./tools/make-block-mesh.py

The block is Special Effects' text block cage (tools/cage.obj, its lettering
already stripped) with a pair of beamed semiquavers standing inside it. The note
is *baked into the mesh* rather than added at load, for two reasons: Besiege
renders the toolbar icon from the block's mesh, so anything added at runtime is
absent from it; and a mesh needs no material of its own, so there is no shader to
find and nothing to go wrong at load.

The note is still a formula -- the constants below are the whole of it -- and this
script is what turns the formula into the shipped geometry. Adjust and re-run.
"""

import io, os, math, struct, zlib

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
CAGE = os.path.join(HERE, "cage.obj")
MESH = os.path.join(REPO, "BraidsSynth/Resources/SynthBlock/SynthBlock.obj")
TEX  = os.path.join(REPO, "BraidsSynth/Resources/SynthBlock/SynthBlock.png")

# ---- the note, in its own flat units -------------------------------------
HEAD_RX, HEAD_RY, HEAD_TILT = 0.175, 0.135, 22.0
STEM_W, BEAM_D, BEAM_GAP    = 0.055, 0.115, 0.19
HALF_THICK, SEGMENTS        = 0.09, 28
LEFT_HEAD,  RIGHT_HEAD      = (-0.34, -0.42), (0.26, -0.18)
LEFT_TOP,   RIGHT_TOP       = 0.46, 0.70

# How tall the note stands in mesh units. The cage spans two of them and is worn
# at half scale, so this is 0.66 of a block.
NOTE_HEIGHT = 1.32

# Which way round the note reads. The sign turns it through 180 degrees about the
# upright: the note is symmetric through its own plane, so negating x alone comes
# to the same solid as turning it, and it stays a note rather than becoming its
# mirror image. Winding is worked out from the geometry afterwards, so either sign
# is solid and lit the right way round.
FACING = 1.0

# Where on the texture the note takes its colour. The cage's unwrap only reaches
# u 0.61 and v 0.63 upwards, so this near corner is free and is painted black.
INK_UV = (0.25, 0.25)
GREY, INK = (80, 80, 80), (18, 18, 18)


def head(cx, cy):
    t = math.radians(HEAD_TILT); co, si = math.cos(t), math.sin(t)
    out = []
    for i in range(SEGMENTS):
        a = 2 * math.pi * i / SEGMENTS
        x, y = HEAD_RX * math.cos(a), HEAD_RY * math.sin(a)
        out.append((cx + x * co - y * si, cy + x * si + y * co))
    return out


def rect(x0, y0, x1, y1):
    return [(x0, y0), (x1, y0), (x1, y1), (x0, y1)]


def note_outlines():
    parts = [head(*LEFT_HEAD), head(*RIGHT_HEAD)]
    lstem = LEFT_HEAD[0] + HEAD_RX - STEM_W / 2
    rstem = RIGHT_HEAD[0] + HEAD_RX - STEM_W / 2
    parts.append(rect(lstem - STEM_W / 2, LEFT_HEAD[1], lstem + STEM_W / 2, LEFT_TOP))
    parts.append(rect(rstem - STEM_W / 2, RIGHT_HEAD[1], rstem + STEM_W / 2, RIGHT_TOP))
    slope = (RIGHT_TOP - LEFT_TOP) / (rstem - lstem)
    x0, x1 = lstem - STEM_W / 2, rstem + STEM_W / 2
    for i in range(2):
        drop = i * BEAM_GAP
        y0 = LEFT_TOP + slope * (x0 - lstem) - drop
        y1 = LEFT_TOP + slope * (x1 - lstem) - drop
        parts.append([(x0, y0 - BEAM_D), (x1, y1 - BEAM_D), (x1, y1), (x0, y0)])
    return parts


def sub(a, b): return (a[0]-b[0], a[1]-b[1], a[2]-b[2])
def cross(a, b): return (a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0])
def dot(a, b): return a[0]*b[0] + a[1]*b[1] + a[2]*b[2]
def norm(a):
    m = math.sqrt(dot(a, a))
    return (a[0]/m, a[1]/m, a[2]/m) if m > 1e-12 else (0.0, 1.0, 0.0)


def build_note():
    parts = note_outlines()
    flat = [p for part in parts for p in part]
    lo = (min(p[0] for p in flat), min(p[1] for p in flat))
    hi = (max(p[0] for p in flat), max(p[1] for p in flat))
    mid = ((lo[0]+hi[0])/2, (lo[1]+hi[1])/2)
    scale = NOTE_HEIGHT / max(hi[0]-lo[0], hi[1]-lo[1])

    def place(u, v, w):
        """Flat layout to mesh space: up becomes +z, thickness becomes y."""
        x = (u - mid[0]) * scale * FACING
        z = (v - mid[1]) * scale
        return (x, w * scale, z)

    tris = []
    for part in parts:
        solid = []
        n = len(part)
        for i in range(1, n - 1):
            solid.append([place(*part[0], w=-HALF_THICK),
                          place(*part[i], w=-HALF_THICK),
                          place(*part[i+1], w=-HALF_THICK)])
            solid.append([place(*part[0], w=HALF_THICK),
                          place(*part[i], w=HALF_THICK),
                          place(*part[i+1], w=HALF_THICK)])
        for i in range(n):
            a, b = part[i], part[(i+1) % n]
            solid.append([place(*a, w=-HALF_THICK), place(*b, w=-HALF_THICK),
                          place(*b, w=HALF_THICK)])
            solid.append([place(*a, w=-HALF_THICK), place(*b, w=HALF_THICK),
                          place(*a, w=HALF_THICK)])
        # Every part is convex, so "outward" is "away from the part's middle".
        # Winding is settled here rather than reasoned about, which is what lets
        # FACING be flipped without turning the note inside out.
        centre = [sum(t[k][j] for t in solid for k in range(3)) / (3*len(solid))
                  for j in range(3)]
        for t in solid:
            fn = cross(sub(t[1], t[0]), sub(t[2], t[0]))
            fc = [sum(t[k][j] for k in range(3))/3 for j in range(3)]
            if dot(fn, sub(fc, centre)) < 0:
                t[1], t[2] = t[2], t[1]
                fn = cross(sub(t[1], t[0]), sub(t[2], t[0]))
            tris.append((t, norm(fn)))
    return tris


def read_cage():
    V, VT, VN, F = [], [], [], []
    for line in io.open(CAGE):
        t = line.split()
        if not t: continue
        if t[0] == 'v': V.append(tuple(t[1:4]))
        elif t[0] == 'vt': VT.append(tuple(t[1:3]))
        elif t[0] == 'vn': VN.append(tuple(map(float, t[1:4])))
        elif t[0] == 'f':
            F.append([tuple(int(x) for x in c.split('/')) for c in t[1:]])
    return V, VT, VN, F


def main():
    V, VT, VN, F = read_cage()
    out = ["# Braids Synth block. Generated by tools/make-block-mesh.py -- edit that,",
           "# not this. The cage is Special Effects' text block (wizz6rd + dagriefaa,",
           "# Workshop 2870726285) with its lettering stripped; the note is a formula.",
           "o SynthBlock"]

    verts = ["v %s %s %s" % v for v in V]
    uvs = ["vt %s %s" % t for t in VT]
    normals = ["vn %.6f %.6f %.6f" % n for n in VN]

    # The frame is an outer skin with no inner walls, so from inside the block its
    # far pillars are culled away and it looks hollow. A reversed copy of every
    # face, with the normal turned round, makes it read solid from any angle.
    flipped_base = len(normals)
    normals += ["vn %.6f %.6f %.6f" % (-n[0], -n[1], -n[2]) for n in VN]

    faces = []
    for f in F:
        faces.append(" ".join("%d/%d/%d" % (a, b, c) for a, b, c in f))
    for f in F:
        faces.append(" ".join("%d/%d/%d" % (a, b, c + flipped_base)
                              for a, b, c in reversed(f)))

    ink = len(uvs) + 1
    uvs.append("vt %.6f %.6f" % INK_UV)

    for tri, n in build_note():
        normals.append("vn %.6f %.6f %.6f" % n)
        ni = len(normals)
        idx = []
        for p in tri:
            verts.append("v %.6f %.6f %.6f" % p)
            idx.append(len(verts))
        faces.append(" ".join("%d/%d/%d" % (i, ink, ni) for i in idx))

    out += verts + uvs + normals + ["s off"] + ["f " + f for f in faces]
    io.open(MESH, "w").write("\n".join(out) + "\n")
    print("mesh: %d verts, %d faces (%d cage, %d cage reversed, %d note)"
          % (len(verts), len(faces), len(F), len(F), len(faces) - 2*len(F)))

    W = H = 256
    rows = []
    for y in range(H):
        v = 1.0 - (y + 0.5) / H
        row = bytearray()
        for x in range(W):
            u = (x + 0.5) / W
            row += bytes(INK if (u < 0.5 and v < 0.5) else GREY)
        rows.append(bytes(row))
    raw = b"".join(b"\x00" + r for r in rows)
    def chunk(tag, body):
        return (struct.pack(">I", len(body)) + tag + body
                + struct.pack(">I", zlib.crc32(tag + body) & 0xffffffff))
    png = (b"\x89PNG\r\n\x1a\n"
           + chunk(b"IHDR", struct.pack(">IIBBBBB", W, H, 8, 2, 0, 0, 0))
           + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))
    io.open(TEX, "wb").write(png)
    print("texture: %dx%d, %d bytes, grey rgb%s with the note's corner rgb%s"
          % (W, H, len(png), GREY, INK))


if __name__ == "__main__":
    main()
