"""E7: KTX2 (BasisU) atlas compression of a pipeline mesh.glb via gltfpack. Measures GLB size, GPU
texture memory and atlas PSNR (compressed vs original) over all texels and over used texels."""

import io
import json
import os
import struct
import subprocess
import sys
from pathlib import Path

import numpy as np
from PIL import Image

D = Path("/workspace/tools/e7-ktx")
KTX = D / "KTX-Software-4.4.2-Linux-x86_64"
env = dict(os.environ, LD_LIBRARY_PATH=str(KTX / "lib"))
src = Path(sys.argv[1])
out = D / "out"
out.mkdir(exist_ok=True)


def glb_images(p):
    b = p.read_bytes()
    jl = struct.unpack_from("<I", b, 12)[0]
    js = json.loads(b[20 : 20 + jl])
    bin_ = b[20 + jl + 8 :]
    res = []
    for im in js.get("images", []):
        bv = js["bufferViews"][im["bufferView"]]
        o = bv.get("byteOffset", 0)
        res.append((im.get("mimeType"), bin_[o : o + bv["byteLength"]]))
    return js, res


def psnr(a, b, m=None):
    d = (a.astype(np.float64) - b.astype(np.float64)) ** 2
    mse = d[m].mean() if m is not None else d.mean()
    return 10 * np.log10(255**2 / mse)


js, ims = glb_images(src)
orig = [np.asarray(Image.open(io.BytesIO(d)).convert("RGB")) for _, d in ims]
print(
    "source",
    src,
    f"{src.stat().st_size / 1e6:.2f} MB",
    "images",
    [(m, o.shape) for (m, _), o in zip(ims, orig)],
)
for o in orig:
    rgba = o.shape[0] * o.shape[1] * 4
    print(f"  RGBA8 GPU: {rgba / 2**20:.1f} MiB, with mips {rgba * 4 / 3 / 2**20:.1f} MiB")
# self-check of the PSNR helper: identical -> inf, 1-level offset -> 48.13 dB
z = np.zeros((4, 4, 3), np.uint8)
assert abs(psnr(z, z + 1) - 48.13) < 0.01
for tag, fl in [
    ("etc1s", ["-tc", "-noq"]),
    ("etc1s-q10", ["-tc", "-tq", "10", "-noq"]),
    ("uastc", ["-tc", "-tu", "-noq"]),
    ("etc1s-quant", ["-tc"]),
]:
    g = out / f"{tag}.glb"
    r = subprocess.run(
        [str(D / "gltfpack"), "-i", str(src), "-o", str(g), *fl],
        capture_output=True,
        check=False,
        text=True,
        env=env,
    )
    if r.returncode:
        print(tag, "FAILED", r.stderr[-500:])
        continue
    _, kims = glb_images(g)
    for i, ((mime, data), o) in enumerate(zip(kims, orig)):
        k = out / f"{tag}_{i}.ktx2"
        k.write_bytes(data)
        info = subprocess.run(
            [str(KTX / "bin/ktx"), "info", str(k)],
            capture_output=True,
            check=False,
            text=True,
            env=env,
        ).stdout
        keys = (
            "vkFormat",
            "pixelWidth",
            "pixelHeight",
            "levelCount",
            "supercompressionScheme",
            "colorModel",
        )
        lv = [l.strip() for l in info.splitlines() if any(x in l for x in keys)][:8]
        png = out / f"{tag}_{i}.png"
        png.unlink(missing_ok=True)
        e = subprocess.run(
            [str(KTX / "bin/ktx"), "extract", "--transcode", "rgba8", str(k), str(png)],
            capture_output=True,
            check=False,
            text=True,
            env=env,
        )
        if e.returncode:
            print(tag, "extract failed", e.stdout[-300:], e.stderr[-300:])
            continue
        dim = Image.open(png).convert("RGB")
        if dim.size != (
            o.shape[1],
            o.shape[0],
        ):  # gltfpack rounded the size; resample back to compare
            print(f"   {tag}: ktx2 is {dim.size}, resampled to {o.shape[1]}x{o.shape[0]} for PSNR")
            dim = dim.resize((o.shape[1], o.shape[0]), Image.BICUBIC)
        dec = np.asarray(dim)
        used = o.max(axis=2) > 8
        print(
            f"{tag} img{i}: {mime} {len(data) / 1e6:.2f} MB; PSNR all {psnr(o, dec):.2f} used {psnr(o, dec, used):.2f}"
            f" (flipped {psnr(o, dec[::-1], used):.2f}); used frac {used.mean():.3f}"
        )
        print("    " + "; ".join(lv))
    print(f"{tag}: GLB {g.stat().st_size / 1e6:.2f} MB  flags {fl}")
# baseline: the resample round trip alone (2047 -> 2048 -> 2047), to separate it from compression loss
for o in orig:
    h, w = o.shape[:2]
    rt = np.asarray(
        Image.fromarray(o)
        .resize((w, (h + 3) // 4 * 4), Image.BICUBIC)
        .resize((w, h), Image.BICUBIC)
    )
    print(f"resample round trip only: PSNR used {psnr(o, rt, o.max(axis=2) > 8):.2f}")
