#!/usr/bin/env python3
"""Write an exact-size proxy box as a binary glTF (GLB). Stdlib only.

usage: make_box_glb.py <width_mm> <height_mm> <depth_mm> <out.glb> [r g b] [--mount -z|+z|-y|+y|-x|+x]
glTF is +Y up, metres. The box is centred on the origin, or with --mount the centre of that face sits at the origin
(parts contract §1b: "origin at the mounting face centre").
"""
import json, struct, sys


def main():
    args = sys.argv[1:]
    mount = None
    if "--mount" in args:
        i = args.index("--mount")
        mount = args[i + 1]
        del args[i:i + 2]
    w, h, d = (float(v) / 1000.0 for v in args[0:3])
    out = args[3]
    rgb = [float(v) for v in args[4:7]] if len(args) >= 7 else [0.9, 0.9, 0.9]
    hx, hy, hz = w / 2, h / 2, d / 2
    # Offset so the mount face centre lands on the origin (the box extends away from that face).
    off = [0.0, 0.0, 0.0]
    if mount:
        axis = "xyz".index(mount[1]); sign = -1 if mount[0] == "-" else 1
        off[axis] = -sign * (hx, hy, hz)[axis]
    faces = [  # normal, 4 corners (CCW seen from outside)
        ((1, 0, 0), [(hx, -hy, hz), (hx, -hy, -hz), (hx, hy, -hz), (hx, hy, hz)]),
        ((-1, 0, 0), [(-hx, -hy, -hz), (-hx, -hy, hz), (-hx, hy, hz), (-hx, hy, -hz)]),
        ((0, 1, 0), [(-hx, hy, hz), (hx, hy, hz), (hx, hy, -hz), (-hx, hy, -hz)]),
        ((0, -1, 0), [(-hx, -hy, -hz), (hx, -hy, -hz), (hx, -hy, hz), (-hx, -hy, hz)]),
        ((0, 0, 1), [(-hx, -hy, hz), (hx, -hy, hz), (hx, hy, hz), (-hx, hy, hz)]),
        ((0, 0, -1), [(hx, -hy, -hz), (-hx, -hy, -hz), (-hx, hy, -hz), (hx, hy, -hz)]),
    ]
    pos, nrm, idx = bytearray(), bytearray(), bytearray()
    for i, (n, corners) in enumerate(faces):
        for c in corners:
            pos += struct.pack("<3f", c[0] + off[0], c[1] + off[1], c[2] + off[2])
            nrm += struct.pack("<3f", *n)
        b = i * 4
        idx += struct.pack("<6H", b, b + 1, b + 2, b, b + 2, b + 3)
    bin_ = pos + nrm + idx
    while len(bin_) % 4:
        bin_ += b"\0"
    gltf = {
        "asset": {"version": "2.0", "generator": "AirTools make_box_glb.py"},
        "scene": 0, "scenes": [{"nodes": [0]}],
        "nodes": [{"mesh": 0, "name": "ProxyBox"}],
        "meshes": [{"primitives": [{"attributes": {"POSITION": 0, "NORMAL": 1}, "indices": 2, "material": 0}]}],
        "materials": [{"pbrMetallicRoughness": {"baseColorFactor": rgb + [1.0], "metallicFactor": 0.0, "roughnessFactor": 0.8}}],
        "buffers": [{"byteLength": len(bin_)}],
        "bufferViews": [
            {"buffer": 0, "byteOffset": 0, "byteLength": len(pos), "target": 34962},
            {"buffer": 0, "byteOffset": len(pos), "byteLength": len(nrm), "target": 34962},
            {"buffer": 0, "byteOffset": len(pos) + len(nrm), "byteLength": len(idx), "target": 34963},
        ],
        "accessors": [
            {"bufferView": 0, "componentType": 5126, "count": 24, "type": "VEC3",
             "min": [-hx + off[0], -hy + off[1], -hz + off[2]], "max": [hx + off[0], hy + off[1], hz + off[2]]},
            {"bufferView": 1, "componentType": 5126, "count": 24, "type": "VEC3"},
            {"bufferView": 2, "componentType": 5123, "count": 36, "type": "SCALAR"},
        ],
    }
    js = json.dumps(gltf, separators=(",", ":")).encode()
    while len(js) % 4:
        js += b" "
    total = 12 + 8 + len(js) + 8 + len(bin_)
    with open(out, "wb") as f:
        f.write(struct.pack("<III", 0x46546C67, 2, total))
        f.write(struct.pack("<II", len(js), 0x4E4F534A) + js)
        f.write(struct.pack("<II", len(bin_), 0x004E4942) + bin_)
    print("wrote %s (%d bytes), box %.3f x %.3f x %.3f m, mount %s" % (out, total, w, h, d, mount or "centre"))


if __name__ == "__main__":
    main()
