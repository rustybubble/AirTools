"""E7: copy a GLB with its (single) texture replaced by a PNG, so the bench harness can render the
KTX2-decoded atlas. swap_tex.py in.glb decoded.png out.glb (the PNG is resampled to the original size)."""

import io
import json
import struct
import sys
from pathlib import Path

from PIL import Image

src, png, dst = map(Path, sys.argv[1:])
b = src.read_bytes()
jl = struct.unpack_from("<I", b, 12)[0]
js = json.loads(b[20 : 20 + jl])
bl = struct.unpack_from("<I", b, 20 + jl)[0]
bin_ = b[28 + jl : 28 + jl + bl]
im = js["images"][0]
bv = js["bufferViews"][im["bufferView"]]
w, h = Image.open(io.BytesIO(bin_[bv.get("byteOffset", 0) :][: bv["byteLength"]])).size
buf = io.BytesIO()
Image.open(png).convert("RGB").resize((w, h), Image.BICUBIC).save(buf, "PNG")
new = buf.getvalue()
off = (len(bin_) + 3) // 4 * 4
bin_ = bin_.ljust(off, b"\0") + new
bin_ = bin_.ljust((len(bin_) + 3) // 4 * 4, b"\0")
bv["byteOffset"], bv["byteLength"] = off, len(new)
bv.pop("byteStride", None)
im["mimeType"] = "image/png"
js["buffers"][0]["byteLength"] = len(bin_)
jb = json.dumps(js, separators=(",", ":")).encode()
jb = jb.ljust((len(jb) + 3) // 4 * 4, b" ")
out = b"glTF" + struct.pack("<II", 2, 12 + 8 + len(jb) + 8 + len(bin_))
out += struct.pack("<I", len(jb)) + b"JSON" + jb + struct.pack("<I", len(bin_)) + b"BIN\0" + bin_
dst.write_bytes(out)
import trimesh  # self-check: it loads and the texture has the original size

m = trimesh.load(dst, force="mesh")
assert m.visual.material.baseColorTexture.size == (w, h)
print(dst, len(out) / 1e6, "MB")
